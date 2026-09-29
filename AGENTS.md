# AGENTS.md —— DSH-App 项目交接说明

> 这个文件会被 harness **自动加载**进上下文。所以它只放**稳定的约定**和**踩过的坑**，
> 不放流水账（进度看 `进度.md`）。

## 这是什么

把 DeepSeek Harness 包装成一个**面向电脑小白的桌面软件**。目标用户不会配环境、不会敲命令，
拿到一个压缩包，双击就能用。

**和 `D:\DSH-Tray` 无关** —— 那个是开发者自用的托盘工具，本项目是独立产品，不要复用它的代码。

## 技术路线（已定，不要改）

**A 方案：外壳**。不自研聊天前端，用 WebView2 套住 `dsh web` 吐出来的界面。

理由：DSH 的界面是它自己渲染的，重写前端要依赖它未冻结的内部协议（当前 `0.1.5-rc.x`），
官方一升级就碎。外壳方案 DSH 升级能自动跟着升级。

## 技术栈（已实测可用，都靠 Windows 自带，不要引入新依赖）

| 用途 | 用什么 | 绝对不要用 |
|---|---|---|
| 语言 | C# 5（system csc.exe） | 不要用 C# 6+ 语法（`?.`、字符串插值、`nameof`），系统编译器不支持 |
| 编译器 | `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | 不要引入 .NET SDK / NuGet / MSBuild |
| 运行时 | .NET Framework 4.8（Win10 1903+ 自带） | 不要用 .NET 6/8 |
| 界面渲染 | WebView2 SDK `1.0.2210.55` 的 **net45** 程序集 | 不要用更高版本（新版本只出 .NET 6+ 程序集） |
| UI | 本地 HTML + CSS + JS | 不要引入前端框架 |

## 铁律一：所有含中文的文本文件必须 UTF-8 **带 BOM**

这不是风格问题，是**会导致程序崩溃**的问题。

PowerShell 5.1 和 csc.exe 对**无 BOM** 的文件会按系统 ANSI 代码页（本机 GBK/936）解码。
后果：中文乱码，而且**某个汉字的字节序列里若含 `0x5C`（反斜杠）或用掉了引号，
会直接吃掉字符串的结束引号，改变语法**。本项目开发过程中**连续踩中两次**，
报错信息是 `error CS1010: 常量中有换行符`，看起来完全不像编码问题。

规则：
- `src/*.cs`、`installer/*.cs`、`ui/*.html` 一律 UTF-8 with BOM
- `build/build.ps1` 内部**只写 ASCII**，由它负责给其它文件补 BOM
  （2026-09-20 实测：`build.ps1` 自己也违反过一次——第 1 行注释里写了个中文文件名，
  12 个非 ASCII 字节。已改成纯 ASCII；`build\` 下四个脚本现在都是 0 非 ASCII，保持住。）
- 读取时**永远按 UTF-8 解码**。用 GBK 去读 UTF-8 文件 = 同样的破坏

## 铁律二：程序必须编译成 `/target:winexe`

控制台子系统（`/target:exe`）的程序会被**控制台控制事件**杀死，退出码 `0xC000013A`
（`STATUS_CONTROL_C_EXIT`），**而且死得连日志都留不下**（`Main` 根本没进去）。
GUI 子系统不会。产品、诊断工具、原型一律 winexe。

## 铁律三：不要用管道抓 `dsh web` 的启动地址

**`dsh web` 只在 stdout 是文件时才打印 banner。** 用管道（`RedirectStandardOutput` +
`BeginOutputReadLine`）读取时它**完全不输出**，你会一直等不到地址。

而且这个 banner 是**唯一**能拿到 token 的途径（`dsh web --help` 没有相关选项）。

正确做法：把子进程的 stdout/stderr 泵到**日志文件**，再从文件里读 banner。见 `DshServer.Start`。

## 铁律四：token 是必须的，裸地址是 401

```
带 token: http://127.0.0.1:3099/?token=XXX  ->  HTTP 200
裸地址:   http://127.0.0.1:3099             ->  HTTP 401
```

所以**绝不能接管一个不是自己启动的服务**（拿不到它的 token，用户只会看到一个报错页）。
端口被占时必须询问用户，不能静默接管。

## 铁律五：WebView2 需要三个文件在 exe 旁边

缺任何一个，异常都发生在 **`Main` 执行之前**，表现为"进程启动即退出、没有任何日志"，
极难定位：

- `Microsoft.Web.WebView2.Core.dll`（托管）
- `Microsoft.Web.WebView2.WinForms.dll`（托管）
- `WebView2Loader.dll`（**原生**，x64 版）

托管 DLL 放在**子目录**里也不行 —— JIT 编译 `Main` 时解析类型就会失败。

## 铁律六：WebView2 控件只能在 UI 线程访问

环境检测和启动服务都跑在后台线程。从后台线程调 `webView.CoreWebView2` 会抛
`InvalidOperationException: CoreWebView2 can only be accessed from the UI thread`
**并且直接让进程崩溃**。必须 `BeginInvoke` 回 UI 线程（见 `MainForm.Send`）。

## 铁律七：绝不在 UI 线程上同步等待 WebView2 的异步方法

**这条最难查，因为它表现为"程序看起来完全正常，但界面冻住、日志断在半句话"。**

```csharp
// 死锁：界面永久卡住，进程"响应=True"，日志停在这一行之后再无输出
var cookies = webView.CoreWebView2.CookieManager.GetCookiesAsync(origin)
    .GetAwaiter().GetResult();
```

原因：`GetCookiesAsync` / `ExecuteScriptAsync` 的续体要**回到 UI 线程**才能完成，
而 UI 线程正阻塞着等它完成 —— 互相等待。

正确写法：`ContinueWith(..., TaskScheduler.FromCurrentSynchronizationContext())`，
用续体串起后续动作。见 `MainForm.ClearHarnessCookiesThen`。

**开发过程中为此浪费了 3 轮排查**，因为所有"正常"的信号都在：进程活着、`Responding=True`、
窗口标题正常、CDP 能连上。

## 铁律八：界面必须用本地 HTTP 服务，绝不能用 `file://`

`file://` 页面是 **opaque origin**，Chromium 把它的请求当作**跨站**处理。
harness 的会话 cookie 是 `SameSite=Strict`，于是：

- 从 `file://` 页面发起的导航 **不会带上 cookie** → harness 返回 **401**
- 从 `file://` 页面发起的 `fetch` **直接 `TypeError: Failed to fetch`**

**实测对比**（同样的 token URL，同样的 WebView2）：

| 页面来源 | 请求结果 | Cookie |
|---|---|---|
| `file:///.../app.html` | **401** | **未发送** |
| `http://127.0.0.1:31700/app.html` | **200** | **已发送** |

所以界面由 `src/UiServer.cs`（`HttpListener`，只绑定回环地址）托管。
这一个改动解决了整类问题。

⚠️ 表里的 **31700 只是当时实测的那个端口，不是承诺**。`UiServer.PickInitialPort`
优先用 31700-31719 里的第一个空闲端口，一旦这个范围被 Windows 的**排除端口范围**
占掉（见铁律二十四），origin 就会漂到 31701 甚至临时端口。写测试和排查时
**要以日志里的 `ui server listening on ...` 为准**，不要硬编码 31700。

## 铁律九：导航由宿主发起，不要依赖页面的 `location.href`

页面明确收到 `ready` 消息、`location.href` 赋值也返回成功，**但窗口就是不跳转**。
宿主自己持有 WebView2，就该自己导航：

```csharp
webView.CoreWebView2.Navigate(capturedUrl);   // 在 UI 线程上
```

同理，`ClearHarnessCookies` 之后必须**在它的续体里**再导航，否则会删掉
token 兑换刚种下的新 cookie。

## 铁律十：抓取子进程输出不要用管道

`dsh web` 的启动 banner（**唯一能拿到 token 的途径**）只在 stdout 是**文件**时打印。
用 `BeginOutputReadLine` 之类的管道读取时它**完全不输出**。

正确做法：把子进程 stdout/stderr 泵到日志文件，再读文件。见 `DshServer.Start`。

判断子进程是否就绪**不要**依赖解析文本这一条路：同时做端口探测作为兜底。

## 铁律十一：不要用模态对话框做诊断

`MessageBox.Show` 会阻塞主线程，写在它后面的日志代码**永远不会执行**。
开发过程中因此浪费了好几轮排查。诊断一律写日志文件，不要弹窗。

## 铁律十二：PowerShell 数组字面量里不要用 `+` 拼接字符串

```powershell
$arr = @(
  'aaa',
  'Key=' + $var,     # 危险！
  'bbb'
)
# 结果：4 个元素。'Key=' 和 $var 被拆成两个独立元素
```

在数组字面量中，元素开头的 `+` 被解析为**数组拼接**而非字符串拼接。
后果是 SED/DDF 这类配置文件多出两行，而 IExpress 只会返回**一个光秃秃的退出码 1，没有任何提示**。

**写法**：用可扩展字符串 `"Key=$var"`。`.Add()` 调用里的 `+` 是安全的
（那是真正的字符串拼接），只有 `@(...)` 字面量里才危险。

## 铁律十三：批处理文件不要同步删除自己所在的目录

`uninstall.cmd` 曾经直接 `rmdir` 掉自己所在的安装目录。**每条命令单独跑都干净，
整个脚本跑却会往 stderr 写 `The system cannot find the path specified` 并返回 1** ——
因为脚本把 cmd.exe 的立足点删掉之后，它没法继续执行余下的行。

**正确做法**（业界标准模式）：
1. 脚本先把"删除助手"复制到 `%TEMP%`，从那里 `start` 它
2. 助手 `ping -n 3 127.0.0.1` 等父进程退出
3. 助手再 `rmdir` 目标目录
4. 主脚本干净退出，用户看到的是完整、无报错的提示

## 铁律十四：批处理里不要用 `timeout`，且要静音 stderr

- **`timeout` 在 stdin 是管道时会直接报错退出**（`Input redirection is not supported`）。
  任何启动器、CI、自动化测试都会触发。要等待就用 `ping -n N 127.0.0.1 >nul`
- `taskkill` 在进程不存在时报 `ERROR: The process ... not found`；
  `del` 在文件不存在时报 `Could not Find ...`。这些都**不是真问题**，但要
  `>nul 2>nul` 静音，否则用户会看到吓人的红字

## 铁律十五：`.cmd` 里不要写非 ASCII

控制台代码页为 65001 时，**每一种"文件编码 × chcp"组合**都会让批处理里的
非 ASCII 文本乱码或被截断（实测 4 种组合全部失败）。批处理一律纯 ASCII，
中文说明写进 **UTF-8 with BOM 的 .txt**，让用户用记事本看 —— 记事本处理得很好。

## 铁律十六：混淆的两股流会让你追错目标

排查卸载脚本时，我把 `cmd` 的 stdout 和 stderr 混在一起看，结果 `timeout` 的报错
**出现在输出里 `rmdir` 的位置**，我因此一路误判成 `rmdir` 的问题，改错了三轮。

**做法**：捕获子进程输出时**永远把 stdout 和 stderr 分开**；先用
`@echo on` 让每条命令回显自己，再用**逐命令隔离**定位真凶。

**同一个错误换了个地方又犯了一次（2026-09-20）**：自用的 `D:\DSH-Tray`（**与本项目无关**，
不要动它的代码）把「子进程起来就崩」和「等不到服务」合并成同一句日志
`启动失败：等待服务就绪超时`，于是一次 bundle 解析失败、一次 `EADDRINUSE`
全被读成"超时"，排查方向从头错到尾。

**推广成一条通则**：**日志里的每一句话只能对应一种故障原因。**
「A 或 B 都走这条分支」的写法，等于把下一次排查的地基抽掉。

## 铁律十七：IExpress 会用隐藏标志启动你的程序

自解压包跑起来后进程活着，但**窗口永远不会出现** —— IExpress 用
`SW_HIDE` 启动 payload。WinForms 窗口被"创建"了却从未显示。

诊断方法（很有用，记下来）：枚举该进程的窗口树并读取每个子控件的文本。
当时输出清楚地显示窗口和所有控件都在，只是 `visible=False`。

**修法**：在 `Load` 事件里强制显示：

```csharp
Load += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront(); };
```

正常启动时这行是无害的（窗口本来就可见）。

## 铁律十八：构建产物目录 ≠ 分发目录

`dist\DSH-App\` 是**构建输出**（现在里面只剩 7 个运行时文件，源码与中间产物都挪到 `.cache\` 了），
但它**不等于可以分发**：没有安装器、没有卸载信息，也不该让小白自己去摆弄文件夹。

**做法**：`build\make-dist.ps1` 生成 `dist\release\`（只含运行时文件，并**自动断言没有任何
`.cs/.ps1/.ddf/.sed/.rsp` 泄漏**）；要发给别人则用 `dist\package\DSH-App-setup.exe`。

⚠️ `dist\release\` 是**按需生成**的，重新构建后它就悄悄过期了，而**过期副本比垃圾更危险**：
实测遇到过 `dist\release\DSH-App.exe` 还是旧版（51,712 字节、没图标没 DPI 修复）却看起来
像交付物的情况。**发之前重新生成，或者发完直接删掉。**

⚠️⚠️ **`dist\package\` 有同一个陷阱，而且更危险 —— 因为它就是发出去的那个文件。**
2026-09-20 实测：14:36 打了包，14:38–14:39 又改了 `installer\InstallSteps.cs` /
`installer\Uninstaller.cs` 并在 14:39:25 重新编译，**但没有人重新打包** —— 于是
`DSH-App-setup.exe` 里装的还是改动之前的安装器逻辑，而它从文件名到体积看起来一切正常。

**发包前的 3 秒判据**：

```powershell
(Get-Item dist\package\DSH-App-setup.exe).LastWriteTime -gt `
(Get-Item dist\DSH-App\DSH-App.exe).LastWriteTime      # 必须是 True
```

**顺序永远是**：改源码 → `build.ps1` → **`pack.ps1`**。
只跑 build 不跑 pack，等于把旧逻辑发给用户。

## 铁律十九：安装日志不要写在 exe 旁边

安装器原本把 `install.log` 写在自身目录。两种情况下都错：
从分发包目录运行时会在包里留垃圾；被 IExpress 拉起时那个目录是临时目录，
**日志会连同它一起被清掉 —— 恰好在你最需要它的时候**。

**做法**：写到 `%LOCALAPPDATA%\DSH-App\install.log`，写不进去才回退到 exe 目录。




## 铁律二十：进程必须自己声明 DPI 感知

**不声明不是"没事"，是"界面发虚"，而且症状会骗你。**

csc 的默认清单里**既没有 `dpiAware` 也没有 `supportedOS`**，所以进程是 DPI 不感知的：
显示缩放不是 100% 时 Windows 把整个窗口**位图拉伸**，WebView2 先按 96 DPI 渲染再被放大，
界面必然发虚 —— 页面做得再细也救不回来。

**极好用的判据**：DPI 不感知的进程会把 Win10/11 报成 `6.2.9200`
（`Environment.OSVersion`）。日志里看到这一条，就等于看到"这个程序没有清单"。

做法：`Program.cs` / `Installer.cs` 在**创建任何窗口之前**调用 `EnableDpiAwareness()`，
依次尝试 `SetProcessDpiAwarenessContext(-4/-3/-2)` → `SetProcessDpiAwareness(2)` →
`SetProcessDPIAware()`；**每个调用都要 try/catch**（这些导出在旧系统上不存在，
不能让它拖垮启动），命中的那个写进日志，不要靠猜。

配套三条，缺一个就会引入新毛病：

- **不要用 `Control.DeviceDpi` 判断缩放比例**：.NET Framework 4.8 在没有 app.config 的
  `DpiAwareness` 开关时，即使进程已经是 PMv2，它**照样返回虚拟化的 96**，于是缩放条件
  永远不成立。用 `GetDpiForWindow(Handle)`（Win10 1607+，try/catch 兜底回 `DeviceDpi`）
- 几何尺寸以 **96 DPI 为基准**写，`OnLoaded` 里按 **真实 DPI / 96** 等比放大
- "手写像素坐标 + **点数（point）** 字号"的窗体，DPI 感知后**必须**整体
  `Scale(SizeF(s, s))` 一次，否则字号跟着 DPI 变大、控件盒不变，字会撑破盒子

本机实测（150% 缩放，真实 DPI 144）应当看到这样一对数字，才算真的修好：

```
dpi 144 -> window scaled by 1.5 (1770x1200)
browser renders at "1.5|1166x763"   (devicePixelRatio|css px)
```

`devicePixelRatio` 是 WebView2 的 **Core 层自己按窗口 DPI 设好的**（WinForms 封装里根本
没有 `RasterizationScale`，grep 一下就知道），**所以不要再手动去设它或设 `ZoomFactor`** ——
那就是二次缩放（1.5 × 1.5 = 2.25 倍，界面会巨大）。

另一个同样好用的判据：**探针进程 DPI 不感知时 `Screen.Bounds` / `GetSystemMetrics`
会报出虚拟化后的尺寸**（2560x1600 的屏报成 1707x1067，正好是 ÷1.5）。

## 铁律二十一：Agent 的 `edit` 工具会静默吃掉 BOM

**改完任何 `src\*.cs`、`installer\*.cs` 或 `ui\*.html`，都要确认 BOM 还在。**

`@deepseek-ai/dsh-fs-local` 的读路径是 `new TextDecoder("utf-8", { fatal: true })`，
默认 `ignoreBOM = false` → **BOM 在读进来时就丢了**；写回是
`handle.writeFile(content, { encoding: "utf8" })`，**不会补**。
结论：**每编辑一次，文件的 BOM 就少一个。** 对别的项目无所谓，对本项目就是铁律一那颗雷
（无 BOM → csc 按 GBK 解码 → 中文乱码、甚至 `CS1010`）。

- **兜底**：`build.ps1` 编译前做字节级 BOM 归一化，跑一次就修好了
  ⚠️ **2026-09-20 实测：这个兜底原本是漏的** —— 它只列了 `src\*.cs` 和 `ui\*.html`，
  **没有 `installer\*.cs`**。而 `InstallSteps.cs` / `Uninstaller.cs` 里分别有 393 / 455
  个汉字，等于那两个文件当时**完全没有保护**，一次编辑就足以让编译产物变成乱码。
  已修：现在是 `src` + `ui` + `installer` 三处，跑一次 build 就能看出漏没漏
  （正常时这一步不打印任何 `+BOM`）。
- **当场修**：让替换结果**以 `U+FEFF` 开头**（例：把 `using System;` 换成
  `` `\uFEFFusing System;` ``）。因为读的时候 BOM 已经丢了，这样**永远只会有一个 BOM**，
  不可能写出双 BOM（双 BOM 会让 csc 报 `CS1056`）
- **别用工具去"检测" BOM**：`read` 和 `ripgrep` 都会把 BOM 吃掉，你看到的永远是"没有"。
  唯一可靠的检测是比字节，而没有 shell 就比不了
- ⚠️ **2026-09-22 补充：构建的兜底只管 `src`/`ui`/`installer` 三处**。
  根目录的 `AGENTS.md`、`进度.md` 和 `docs\*.md` 同样是中文文本，但**不在检查范围内**，
  改完没人会补 BOM —— 只能自己补。一行命令（先读字节，缺 BOM 才写回，避免双 BOM）：

  ```powershell
  $p = 'D:\dsh\DSH-App\进度.md'   # 项目当前位置（2026-09-22 从 D:\DSH-App 搬来）；换位置只改这一行
  $b = [System.IO.File]::ReadAllBytes($p)
  if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
      $t = (New-Object System.Text.UTF8Encoding($false)).GetString($b)
      [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($true)))
  }
  ```

  （用 `[System.IO.File]` 的**绝对路径**：.NET 不认 PowerShell 的 `Set-Location`。）

## 铁律二十二：可选依赖装不上，npm 不会报错

**这是"安装成功、一启动就崩"的经典来源。**

`sharp`（附件插件的依赖）把各平台二进制声明成 **optionalDependencies**
（`@img/sharp-win32-x64` 等 20 多个）。可选依赖下载失败时 **npm 照样返回成功**，于是树看起来是好的，
harness 一启动就抛：

```
Error: Could not load the "sharp" module using the win32-x64 runtime
```

**别被误导**：这与 Node 版本无关（sharp 自己要求 `>=20.9.0`，Node 24 完全没问题），也不是
"包损坏"，`npm cache clean` 治不了 —— 坏的是**已经装好的那棵树**。

本项目做法：`DshServer.ClassifyFailure` 认出这个签名 → 界面给「自动修复并重试」→
`EnvProbe.RepairOptionalDeps` 先在树里补装（用 `SharpBinaryPresent` 验证二进制真落地），
补不动就删掉整棵树让 npx 重拉。

## 铁律二十三：读"我们自己正在写"的文件，必须带 FileShare.ReadWrite

`File.ReadAllText` 内部请求的是 **`FileShare.Read`**。当同一个进程还持有该文件的**写句柄**
（我们用 `FileShare.ReadWrite` 打开它来泵子进程输出）时，Windows 会**拒绝**这次读：
共享模式不对称 → 抛 `IOException` → 一旦被 `catch` 吞掉，现象就是"**静默读到空字符串**"，
而文件里明明有内容。排查时极难看出，因为没有任何报错。

```csharp
using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
using (var sr = new StreamReader(fs, Encoding.UTF8)) { text = sr.ReadToEnd(); }
```

本项目真实踩过两次：`DshServer.DumpTail()` **一直是空操作**（"harness 退出的原因"从来没进过
`app.log`），以及新加的失败分类一开始永远读到空串。

## 铁律二十四：端口会被两个独立机制占住，而且两个都不能用

**现象**：界面服务一直好好地跑在 `127.0.0.1:31700`，`Get-NetTCPConnection` 却报
**`Listen` / `OwningProcess = 4`（内核）**，同一端口上**新进程 bind 必然失败**，
老进程的页面依然 `200`。`netsh http show urlacl` 又**查不到 31700**。

**实测**（`netsh int ipv4 show excludedportrange protocol=tcp` → `31700 - 31700`）：

| 试什么 | 结果 | 说明 |
|---|---|---|
| 裸 socket：`TcpListener(127.0.0.1, 31700)` | 失败：**"以访问权限不允许的方式访问套接字"** | 这是**排除端口范围**的特征 |
| `HttpListener` 前缀 `http://127.0.0.1:31700/` | 失败：**"与计算机上的现有注册冲突"** | 这是 **HTTP.sys 前缀注册**的特征 |
| 前缀 `http://localhost:31700/` / `http://+:31700/` | 失败：**"拒绝访问"** | 注册只覆盖 127.0.0.1 那一个前缀 |

**两个机制是独立的，别合并成一句话**（第一版交接就合并错了）：
排除范围（Hyper-V / WSL / Docker / WinNAT 类组件申请，`netsh add excludedportrange`
需要管理员）挡的是**裸 socket**；HTTP.sys 前缀注册挡的是**前缀**。
两者都表现为"这个端口不能用了"，但**报错文本不同**，看报错就知道该查哪一个。

老实例还能服务，是因为这两种东西**只挡新的 bind，不踢已经建立的监听 socket**
（HTTP.sys 在进程还活着时也持有那个前缀）——于是出现"僵尸端口"：
老实例照常 200、新实例绑不上、`netsh` 说端口是"排除"的。本机 31700 就是这样，
app 的界面 origin 早先被逼着漂到了 **31701**（WebView2 按 origin 存的东西会跟着失效，
比如用户 `Ctrl +/-` 设过的缩放）。

**为什么 TCP 探测不能换成 bind 探测**：

| 端口状态 | `TestPortOpen` | HttpListener bind |
|---|---|---|
| 有人听或已被占（31700） | **True** | **失败** |
| 真有人听（3099 harness） | **True** | 失败 |
| 空闲（31701 / 31719） | False | **OK** |

结论：**`TestPortOpen == True` 必须当作"不可用"**，现有 `PickInitialPort` 跳过 31700
的行为**是对的**。不要为了"语义一致"改成 bind 探测 —— 那只是把同一个判断换个地方失败。

排除范围是**动态**的（实测：同一台机器上 31700 在一次构建前后就变过一次），
所以这个端口号**会漂**。`UiServer` 因此在绑到的端口 ≠ 首选端口时写一行
`ui port 31700 was unusable, using N instead (the interface origin changed)` ——
**这是范围被逐步占满的唯一早期信号，不要删**。要真的固定端口，就挑 317xx 之外的范围。

## 关键路径

| 东西 | 位置 |
|---|---|
| 应用数据 / 配置 / 密钥 | `%LOCALAPPDATA%\DSH-App\` |
| 日志（含子进程输出） | `%LOCALAPPDATA%\DSH-App\logs\` |
| 服务端 stdout | `logs\server-<时间戳>.out.log` |
| 默认工作区 | `%USERPROFILE%\Documents\DSH-App-Workspace` |

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

产物在 `dist\DSH-App\`，可整个文件夹拷走运行。脚本会：
1. 按需下载 WebView2 SDK 到 `.cache\`（约 8 MB，只需一次）
2. 给源码和 HTML 补 UTF-8 BOM
3. 清理并重建 `dist`
4. 用系统 csc.exe 编译

构建时若 `dist\DSH-App.exe` 被占用，说明有实例在跑 —— 脚本会自动结束它（程序关闭窗口后会驻留托盘，这是设计行为）。

## 改代码时注意

- **只在这个仓库里写文件**（项目 2026-09-22 从 `D:\DSH-App` 搬到了 `D:\dsh\DSH-App`）。
  不要动 `D:\DSH-Tray`（那是用户自用且正在运行的工具）
- ⚠️ **不要在任何脚本里写死仓库的绝对路径。** 所有脚本都靠 `$PSScriptRoot` / `Split-Path -Parent`
  推路径（`build.ps1`、`pack.ps1`、`test\_lib.ps1` 都是），这是这个仓库搬走之后**什么都不用改**的原因。
  写死的路径不会有任何报错，只会安静地指向一个不存在的地方。
  唯一的例外是下面那条"补 BOM"的命令，它就是要能直接复制粘贴，所以留了一个显式路径（并且注明了换位置改哪一行）
- **不要碰 `%USERPROFILE%\.dsh\.credentials.yaml`** —— 那里面除了用户的 API Key 还有其它客户端的凭据记录，
  改坏了会破坏用户已有的登录。API Key 走**环境变量**传给子进程（`DEEPSEEK_API_KEY`），
  这是 DSH 官方支持的机制（启动环境优先级最高），见 `KeyStore` 注释
- 日志里**永远不要打印完整密钥**，用 `KeyStore.Describe`

## 铁律二十五：测试不许碰本机的真实状态

**测试只能在自己的沙箱里动手。安装目录之外的任何位置（快捷方式、注册表、用户配置）
都必须靠产品自带的测试钩子重定向，不许"反正测完会恢复"。**

这条是血的教训。2026-09-22 写 `t-uninstall-safety.ps1` 时，测试直接把安装器指向沙箱，
却忘了卸载程序会顺手做三件与安装目录无关的事：

1. 删 `%USERPROFILE%\Desktop\DSH-App.lnk` 和开始菜单里的同名快捷方式；
2. 删 `HKCU\...\Uninstall\DSH-App` —— 用户真实的"应用和功能"条目；
3. 而 `t-upgrade.ps1` 更隐蔽：它每次跑都把那条真实条目**改写成指向
   `%TEMP%\dsh-test-upgrade\app`**（那个目录跑完就删了）。

第 2、3 条一直没人发现，因为条目只在"设置 → 应用"里可见，不在任何日志里。
结果就是用户机器上的卸载条目指向一个不存在的目录 —— **卸载程序能修 bug，测试自己却在造 bug。**

现在产品里有一整套钩子，测试必须用：

| 钩子 | 作用 |
|---|---|
| `installer: /noreg` | 静默安装时不写"应用和功能"注册表项 |
| `uninstaller: /data=` `/dshhome=` | 重定向设置目录和 `~\.dsh` |
| `uninstaller: /desktop=` `/startmenu=` | 重定向两个快捷方式路径 |
| `uninstaller: /regkey=` | 重定向要删的 HKCU 子键（相对 HKCU） |

配套的断言也要有：`t-uninstall-safety.ps1` 跑完会比对**本机真实条目在测试前后完全一致**
（`reg query` 的原文比较），否则测试自己就是那个"看起来绿、实际在破坏"的东西。

**另一个坑：不要用 `reg query` 的输出来判断键存不存在。**
键不存在时 `reg.exe` 往 stderr 写 `ERROR: ...`，PowerShell 把它包成 error record，
`Out-String` 出来是 `reg.exe : ERROR: ...` —— 于是 `-like 'ERROR*'` **永远不匹配**，
断言永远"通过"或永远"失败"，取决于你怎么写。要判断存在性就用注册表提供程序：
`Test-Path 'HKCU:\Software\...\Whatever'`。

## 铁律二十六：页面 → 宿主的那条桥必须验来源，窗口必须限制能去哪

**WebView2 的 `chrome.webview.postMessage` 属于"窗口"，不属于某个页面。**
窗口里**任何**文档都能调它：聊天回答里用户点开的链接、模型输出里导航到的页面、以及那些
页面里的跨域 iframe。`MainForm.OnWebMessage` 处理的每一条消息都是特权操作（存 API Key、
改端口、重启/停止服务、退出程序、用系统浏览器打开网址），所以**必须先问 `e.Source` 是不是
我们自己那张控制面板**（`IsOwnUiMessage`）。没有这一步，用户点开的一个网页就能
`{"type":"saveKey","key":"sk-…"}`，把他之后所有请求换到别人的 Key 上。

配套的还有 `OnNavigationStarting`：窗口只允许停在**两个源**上——我们自己 UI 服务的，以及本
程序启动的 harness 的（token URL、401 恢复页、页内路由都是同源，实测不受影响）。其它一律
`e.Cancel = true` 并丢给系统浏览器（`OnNewWindowRequested` 同理，否则 WebView2 会另开一个
我们完全管不到的窗口）。这两件事要一起做：只验来源不改导航，用户点个链接窗口就跑到对方站点；
只改导航不验来源，本机其它服务的页面照样能发消息。

## 铁律二十七：往命令行里塞的字符串，要么是常量，要么先验过

这个程序有几处"把路径或网址拼进命令行"的地方，每一处都出过或差点出事：

- **生成的 `.cmd` 里每一行都是执行体。** 安装清单（`install.files`）在用户可写的目录里，
  行里的文件名会变成 `del /f /q "<安装目录>\<名字>"`。所以 `Uninstaller.IsSafeRelativeName`
  和 `CabReader.RequireSafeName`（两个 exe，两份实现，**必须同步**）只放行"普通相对路径"：
  不要引号、`%`、`!`、`&`、`|`、`<`、`>`、`^`、`*`、`?`，不要盘符、不要 `..`。
  另外清单是**构造**出来的（cab 里有什么 + 我们写了什么），不是列目录列出来的。
- **`cmd /c start "" "<url>"` 的 url 必须验。** 一个带 `"` 的地址会提前结束参数、把后面的内容
  当命令跑。`IsSafeExternalUrl` 只放行 http/https，并拒绝 `"`、`%` 和控制字符
  （`&`、`|` 在引号内是普通字符，查询串要用，故意不拦）。
- **路径要当参数传，不要拼进脚本文本。** 脚本文本里的 `%NAME%` 会被 cmd 展开：
  `C:\100% done\DSH-App` 会被替换掉一段。`%~1` / `%~2` 不会二次展开。
- **`%TEMP%` 里的固定文件名是共享资源。** 交给 cmd 执行的助手脚本必须用 GUID 命名，
  并且脚本自己要再验一次 `install.marker` 还在不在。

## 铁律二十八：测试钩子不能变成产品功能

`Uninstaller.exe` 的 `/data=` `/dshhome=` `/desktop=` `/startmenu=` `/regkey=` 是给自动化
测试用的，但它们凑在一起就是"递归删除任意目录 + 删除任意 HKCU 子键"。现在有两道门：
`DSH_APP_TEST_HOOKS=1` 环境变量（没有它，**只要出现钩子参数就拒绝运行**——不是忽略，
忽略会让忘了设变量的测试直接打到真实目录），以及参数本身的合理性检查（必须绝对路径、
不能是盘符根、不能有 `..`；`/regkey=` 必须是 Uninstall 分支下的子键）。
安装器的 `/noreg` 是同一类东西（静默安装时不写"应用和功能"注册表项）。

## 铁律二十九：构建缓存里的二进制，和"看得见的东西"一样要验

`.cache\wv2` 里的三个 DLL（`Microsoft.Web.WebView2.Core.dll`、`…WinForms.dll`、
`WebView2Loader.dll`）**会被拷进 dist、再进 cab、再到用户机器上**。以前只检查"文件在不在"，
而缓存会被之后每一次构建复用 —— 也就是说一个被换掉的缓存文件会悄悄进每一个包。
现在 `build.ps1` 比对三个文件钉死的 SHA-256，**并且**要求它们的 Authenticode 签名仍然有效、
签名者是 Microsoft；对不上就丢掉缓存重下一次，仍对不上就**构建失败**。
所以：**改 `$wv2Version` 就必须同时更新那三个哈希**（脚本的报错信息里写了这句）。

`pack.ps1` 结尾会打印所有产物的 SHA-256 —— 发版时把它贴出来，用户就能自己核对拿到的是不是
你发的那一份。

## 铁律三十：重定向环境变量**骗不过** `GetFolderPath`

`AppLogger` 和 `AppConfig` 的数据目录来自
`Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`。
**这个方法读的是注册表里的 shell folder，不看 `LOCALAPPDATA` 环境变量。**

后果：给子进程设 `LOCALAPPDATA=<沙箱>` 想把程序的数据/日志关进沙箱，**一点用都没有** ——
程序照样读写真实的 `%LOCALAPPDATA%\DSH-App`，而测试脚本却去沙箱里找日志，于是
**断言永远读到空文件、永远"通过"**。`test-no-node.ps1` 当年就是这么废掉的，
`test-oldnode.ps1` 2026-09-29 又踩了一次（改成读真实路径 + 只读本次新增的字节才修好）。

所以在这个项目里，**隔离数据目录只有两条正路**：

1. 改 `config.ini`（端口、工作区这类设置），并靠 `test\_lib.ps1` 的 `Set-TestConfig` /
   `Restore-Config` 在跑完还原 —— 这是测试套件用的办法；或者
2. 用产品自带的**测试钩子**把具体那个路径重定向掉（见铁律二十八）。

另外两条相关的：
- **不要用 `%LOCALAPPDATA%` 去影响 node / npm-cache 的查找**，同一个原因（`_npx` 也是按 shell
  folder 解析的）。
- **日志是共享文件**：跑一次就多一段，历史里可能已经有关键字（实测 `app.log` 里有 209 行
  `starting harness`）。任何"日志里出现了 X"的断言都必须**先记字节偏移、只看新增部分**，
  否则会拿上一次运行的结果当证据。

# DSH-App（DSH 启动器）

> **English** — DSH-App is a Windows desktop launcher that turns
> [DeepSeek Harness](https://www.npmjs.com/package/@deepseek-ai/dsh) — a Node-based AI agent that can
> read and write files and run commands — into a program you simply double-click. It detects or guides
> you through installing Node.js, downloads and starts the harness, discovers the tokenised access URL,
> and shows the UI in an embedded WebView2 window or your normal browser. Tray-resident, single
> instance, no telemetry, no admin rights needed.
>
> **This is an unofficial community launcher. It is not affiliated with or endorsed by DeepSeek.**
> The product UI and its documentation are in Chinese, which is who it is built for. Licensed MIT.

**把 DeepSeek Harness 变成双击就能用的桌面程序。**

---

## 它解决什么问题

DeepSeek Harness 是一个很强大的东西：它能读写文件、执行命令、连插件，把模型变成一个真正能替你干活的助手。

但要把它跑起来，你需要先装 Node.js，然后在终端里敲命令、等它下载两百多兆的运行文件、处理各种环境问题。**这一步挡住了绝大多数人。**

DSH-App 就是为这一步而生的：它把"装环境、起服务、连界面"这些事全部接过来，用户只需要**双击一个图标**。

> 一句话概括：**harness 是引擎，DSH-App 是把引擎接到轮子上的那套装置。**

---

## 它能做什么

| | |
|---|---|
| **双击就用** | 全程图形向导：自动检测环境 → 缺什么告诉你装什么 → 点一下就开始。不需要打开过命令行 |
| **会解释问题，而不是丢报错** | 缺 Node / Node 过旧 / 组件下载不完整 / 网络不通 / 端口被占用，都分辨得出来并用中文说明下一步 |
| **首次下载有真实进度** | 数字来自磁盘上真实的依赖包与体积，不是估算出来的假进度条 |
| **常驻托盘的控制面板** | 当前端口与进程号、一键打开界面（内置窗口或浏览器）、停止／重启服务、改端口、打开工作目录／日志目录 |
| **升级不动你的数据** | 只替换程序文件；对话、密钥、设置、插件都不受影响（有自动化测试逐字节验证） |
| **卸载干净且不过界** | 删除范围由你勾选；而且只删它自己装的文件 —— 目录里别人的东西原样保留 |
| **可审查** | 约 1 MB，只用 Windows 自带工具链构建（不需要 .NET SDK、不需要 NuGet、没有第三方运行时），不驻留后台服务、无遥测 |

---

## 下载与安装

1. 到 [Releases](../../releases) 下载 **`DSH-App-setup.exe`**（约 512 KB，单文件）。
2. （可选但推荐）核对哈希 —— 发布时会同时公布 SHA-256：

   ```bat
   certutil -hashfile DSH-App-setup.exe SHA256
   ```

3. 双击运行。Windows 可能提示「未知发布者」（本程序没有购买代码签名证书），选「更多信息 → 仍要运行」。
4. 安装位置默认 `%LOCALAPPDATA%\Programs\DSH-App`，**不需要管理员权限**。

**系统要求**：Windows 10 / 11（64 位）；WebView2 运行时（Win10/11 通常已内置，缺失时程序会提示）。
**首次运行必须联网**，需要下载约 200 MB 的运行文件（磁盘占用约 360 MB）；之后日常使用不依赖网络。

---

## 数据放在哪里

| 内容 | 位置 |
|---|---|
| 程序文件 | `%LOCALAPPDATA%\Programs\DSH-App` |
| 设置 | `%LOCALAPPDATA%\DSH-App\config.ini` |
| API 密钥 | `%LOCALAPPDATA%\DSH-App\apikey.txt` |
| 日志 | `%LOCALAPPDATA%\DSH-App\logs` |
| 对话 / 附件 / 插件 / 凭据 | `~/.dsh`（harness 自己的目录） |
| 工作区（助手可读写的文件夹） | 默认 `Documents\DSH-App-Workspace` |
| harness 运行文件 | `%LOCALAPPDATA%\npm-cache\_npx` |

详细的图文说明见 [docs/使用说明.txt](docs/使用说明.txt)（纯文本，随发布一起给人）和
[docs/介绍.txt](docs/介绍.txt)。

---

## 从源码构建

不需要 .NET SDK、不需要 NuGet：**只用到 Windows 自带的 `csc.exe`（.NET Framework 4.8）**，
加上打包用的 `makecab.exe` 和 IExpress。唯一的下载物是 WebView2 SDK（NuGet 上一次，缓存在 `.cache\`）。

```powershell
git clone <这个仓库>
cd DSH-App
powershell -ExecutionPolicy Bypass -File build\build.ps1     # -> dist\DSH-App\  （可直接运行的文件夹）
powershell -ExecutionPolicy Bypass -File build\pack.ps1      # -> dist\package\ （setup.exe 等，并打印 SHA-256）
```

构建脚本会做几件"看起来多余但都踩过坑"的事，改脚本前值得先读一下：

- **字节级检查中文编码**：所有含中文的 `.cs` / `.html` 必须是 UTF-8 **带 BOM**。无 BOM 时 csc 会按
  GBK 解码 —— 而且**编译仍然成功**，于是产品带着乱码发出去。纯 ASCII 文件缺 BOM 只是顺手补上。
  不是合法 UTF-8 的文件会被**拒绝并且不写入**（补 BOM 会把中文替换成 U+FFFD，等于毁掉源码）。
- **校验 WebView2 SDK**：`.cache\wv2` 里那三个 DLL 会被拷进 `dist` 并最终进用户的安装包，所以
  构建时比对**钉死的 SHA-256** 并验证 **Microsoft 的 Authenticode 签名**；对不上就丢掉缓存重下一次，
  仍对不上就让构建失败。因此**不要单独改 `$wv2Version`**，改了要同步改那三个哈希。
- **打包预检**：`pack.ps1` 要求 payload 里 7 个文件齐全（少一个就报错），因为曾经打出一个**没有卸载程序**的包。

> ⚠️ **构建不是逐字节可复现的**：同一份源码编译两次，`DSH-App.exe` / `Uninstaller.exe` 的字节不同
> （PE 头里有时间戳），`app.html` / `dsh.ico` 这类数据文件才完全一致。所以公布的 SHA-256
> **只对那一次打出来的包有效**，重新打包后要重新公布。这也是"包出了手就别改内容"的原因。

---

## 测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File test\run-all.ps1
```

13 个测试，约 5.5 分钟，全部离线（除了一个需要联网的、默认不跑的 `harness-bootstrap`）。
覆盖：冷启动到聊天界面、慢 banner 不误判失败、首次下载的真实进度、端口冲突／Windows 预留端口、
版本可见性、Node 探测、命令行参数、单实例、看门狗自愈、带运行实例的升级、**卸载安全**、
**页面→宿主消息桥只认自己的界面**。

每个测试自己建沙箱、跑完清理，并把输出写到 `test\last-<名字>.log`（失败时 run-all 会直接把失败断言打出来）。

写测试的两条硬规则（血泪换来，见 `AGENTS.md` 铁律二十五）：

- **测试不许碰本机的真实状态。** 安装目录之外的一切（快捷方式、注册表、用户配置）都必须靠产品自带的
  测试钩子重定向，并且断言"本机真实状态测试前后逐字节一致"。曾经有测试把用户真实的
  「应用和功能」条目删掉、还有测试把它改写成指向一个临时目录。
- **钩子要有门禁。** 卸载程序的 `/data=` `/regkey=` 之类的位置覆盖只在 `DSH_APP_TEST_HOOKS=1`
  时生效，没有这个环境变量就**拒绝运行**（不是忽略 —— 忽略会让忘了设变量的测试直接打到真实目录）。

---

## 安全性

- **不联网上报任何东西**：没有遥测、没有统计、没有后台服务。只有首次运行时按官方方式下载 harness。
- **API 密钥只在本机**：存在 `%LOCALAPPDATA%\DSH-App\apikey.txt`，通过**子进程环境变量**交给 harness
  （不经过命令行，其它程序无法从进程列表里读到它）。日志里只记录长度和几位前缀。
- **日志里有本机访问地址**，形如 `http://127.0.0.1:3080/?token=…`。这串 token 是**本机服务的钥匙，
  但只在这台机器上有效**，别人拿到也用不了。如果你要把日志发出去，稳妥起见先删掉 `token=` 后面那串
  （文档里也写了这句）。
- **本地界面只绑 `127.0.0.1`**，且只按文件名提供 `ui\` 里的静态文件，没有目录穿越、也没有任何改状态的接口。
- **页面→宿主的消息桥只认程序自己的界面**：WebView2 的 `chrome.webview.postMessage` 属于窗口而不是页面，
  所以任何被加载的页面都能调它。宿主会校验消息来源，并且只允许窗口停在两个源上（自己的界面、自己启动的 harness），
  其它链接交给系统浏览器打开 —— 否则聊天里点开的一个网页就能改掉你的 API Key。这一条有专门的测试。
- **卸载程序只删它自己安装的文件**（依据安装时写下的清单），目录不是空的就整体保留。

发现问题请开 Issue；如果是安全问题（比如你发现某条路径能被绕过），在 Issue 里说明即可，
不用贴任何密钥或日志原文。

---

## 已知边界（如实说明）

- **没有代码签名**：Windows 会提示「未知发布者」。发布时会公布 SHA-256，所以"文件有没有被换过"仍可自查。
- **不提供"检查新版本"功能**：升级由发布者手动分发安装包，程序不会联网询问版本。
- **不做无界面 / headless 模式**：本程序的存在理由是"让不会用终端的人也能用"。需要脚本化调用时请直接用 `dsh`。
- **首次运行必须联网**，约 200 MB，视网速可能要几分钟；这一步无法跳过。
- **只支持 Windows**：用了 WinForms + WebView2 + csc.exe。
- **非官方项目**，与 DeepSeek 无关；harness 本身是独立项目，本仓库不分发它的代码。

---

## 目录结构

```
src\              启动器本体（C#，WinForms + WebView2）
  Program.cs        入口、命令行参数、异常兜底
  MainForm.*.cs     窗口 / 控制面板 / 托盘 / 页面对话 / 环境检查（partial 拆分）
  DshServer.cs      harness 子进程的生命周期 + 从输出里抓 token 地址
  EnvProbe.cs       环境检测、端口三态、进程探测、命令执行
  UiServer.cs       本地回环 HTTP，托管自己的界面
  AppConfig.cs      设置文件 + API Key
  AppLogger.cs      日志（滚动、立即 flush）
installer\        安装程序 + 卸载程序（各自独立的 exe）
  Installer.cs      安装窗口与静默模式
  InstallSteps.cs   安装步骤（两个模式共用）
  Uninstaller.cs    卸载程序（勾选删除范围、按清单删文件）
  CabReader.cs      cab 解析兜底（expand.exe 不可用时）
ui\app.html       界面（检测 / 填 Key / 启动 / 端口冲突四步）
build\            build.ps1（构建）、pack.ps1（打包）、encoding.ps1（编码规则）、make-icon.ps1
test\             13 个测试 + run-all.ps1 + _lib.ps1
docs\             面向用户的介绍与使用说明（.md 与 .txt 两份）
AGENTS.md         项目交接说明：30 条"铁律"（构建规则、踩过的坑、为什么这么写）
进度.md           逐轮的开发记录：每个决定的原因、每个 bug 的根因
```

`AGENTS.md` 和 `进度.md` 是这个项目的**过程记录**，公开是有意的：里面写清了"为什么这么写"和
"哪些坑踩过"，比代码本身更能说明这套东西。它们同时也是给 AI 助手看的交接文档
（`AGENTS.md` 会被自动加载），所以行文是"给下一个改这份代码的人（或 agent）"的口气。

---

## 第三方组件

| 组件 | 用途 | 说明 |
|---|---|---|
| **Microsoft WebView2 SDK** | 内置浏览器内核（界面宿主） | 构建时从 NuGet 下载 `Microsoft.Web.WebView2 1.0.2210.55`，其三个 DLL 会随安装包分发。版权归 Microsoft，按其 SDK 许可（可再分发）使用 |
| **DeepSeek Harness**（`@deepseek-ai/dsh`） | 真正的"引擎" | **不随本仓库分发**；首次运行时由用户机器按官方方式下载 |
| Node.js | harness 的运行环境 | 不随本仓库分发；本程序只负责检测并引导安装 |

本仓库自身的代码以 **MIT** 协议发布，见 [LICENSE](LICENSE)。

---

## 协议

[MIT](LICENSE)。欢迎 Issue 和 PR；改代码前建议先看 `AGENTS.md` 里的铁律 —— 尤其是编码规则
（中文文件必须 UTF-8 带 BOM）和"不要写死仓库绝对路径"这两条。

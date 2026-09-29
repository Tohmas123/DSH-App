using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Minimal cabinet (.cab) reader used as a fallback when the system
/// expand.exe cannot be used.
///
/// Scope is deliberately narrow: it walks the cabinet's tables to recover the
/// original file names and their bytes, and understands the "none" and "MSZIP"
/// folder compressions. Anything else is reported rather than guessed at, so a
/// failure is diagnosable instead of silent.
/// </summary>
internal static class CabReader
{
    private const uint CabinetSignature = 0x4643534D; // "MSCF"
    private const int HeaderSize = 36;

    /// <summary>
    /// Ceiling on anything this reader will materialise in memory: one uncompressed block,
    /// and one folder's worth of them. The real payload is ~1 MB, so this is three orders of
    /// magnitude of headroom -- its only job is to stop a crafted header from asking for an
    /// allocation the machine cannot satisfy.
    /// </summary>
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>Cabinet folder compression types we can handle.</summary>
    private const ushort CompNone = 0;
    private const ushort CompMSZip = 1;

    private sealed class CfFile
    {
        public uint Offset;
        public uint Size;
        public ushort FolderIndex;
        public string Name;
    }

    private sealed class CfFolder
    {
        public uint DataOffset;
        public ushort NumBlocks;
        public ushort Compression;
    }

    /// <summary>
    /// The relative names of the files inside the cabinet, without extracting them.
    ///
    /// Used to write the install manifest: the uninstaller must know exactly what this
    /// product put on disk so it can remove precisely that, instead of removing the whole
    /// folder it happens to sit in.
    /// </summary>
    public static List<string> ListEntries(string cabPath)
    {
        List<CfFolder> folders;
        List<CfFile> files;
        ParseTables(File.ReadAllBytes(cabPath), cabPath, null, out folders, out files);
        var names = new List<string>();
        foreach (var f in files)
        {
            // Names are NOT trusted: they end up in a generated batch file (see
            // Uninstaller.IsSafeRelativeName, which applies the same rule). A name that is
            // not a simple relative path means the cabinet is not one of ours, and the
            // install is refused rather than carried out halfway.
            RequireSafeName(f.Name);
            names.Add(f.Name.Replace('/', Path.DirectorySeparatorChar));
        }
        return names;
    }

    /// <summary>
    /// A cabinet file name may be any byte string, including an absolute path, a "..\" walk
    /// or something with a quote in it. Writing such a name, or feeding it to a command line,
    /// is how a "cabinet" turns into an arbitrary file write. Every entry has to look like
    /// "ui\app.html" and nothing else.
    ///
    /// Kept in step by hand with Uninstaller.IsSafeRelativeName: the two live in different
    /// executables and cannot share code.
    /// </summary>
    internal static void RequireSafeName(string name)
    {
        string why = null;
        if (string.IsNullOrEmpty(name)) why = "空文件名";
        else if (name.Length > 200) why = "文件名过长";
        else if (name[0] == '\\' || name[0] == '/' || (name.Length > 1 && name[1] == ':')) why = "绝对路径";
        else if (name.IndexOf(':') >= 0) why = "包含冒号";
        else
        {
            foreach (char c in name)
            {
                if (c < 0x20 || c == 0x7f) { why = "包含控制字符"; break; }
                if (c == '"' || c == '%' || c == '!' || c == '&' || c == '|'
                    || c == '<' || c == '>' || c == '^' || c == '*' || c == '?')
                {
                    why = "包含命令行特殊字符";
                    break;
                }
            }
            if (why == null)
            {
                foreach (string segment in name.Split(new char[] { '\\', '/' }))
                {
                    if (segment == "..") { why = "包含 .."; break; }
                }
            }
        }
        if (why != null)
        {
            throw new InvalidDataException("安装数据里有一个不安全的文件名（" + why + "）：" + name);
        }
    }

    public static void ExtractAll(string cabPath, string targetDir, Action<string> log)
    {
        byte[] cab = File.ReadAllBytes(cabPath);
        List<CfFolder> folders;
        List<CfFile> files;
        uint coffFiles = ParseTables(cab, cabPath, log, out folders, out files);

        ushort flags = BitConverter.ToUInt16(cab, 30);
        byte cbCFData = 0;
        if ((flags & 0x0004) != 0)
        {
            byte cbCFFolder = cab[HeaderSize + 2];
            cbCFData = cab[HeaderSize + 3];
        }

        // ── folder data ─────────────────────────────────────────────────────
        // The table offset is checked in long arithmetic: the per-file term is derived from
        // names the cabinet supplies, so "int" could wrap and turn a bad cabinet into a read
        // from a negative offset.
        long cfdataLong = (long)coffFiles;
        for (int i = 0; i < files.Count; i++) cfdataLong += 16 + Encoding.GetEncoding(936).GetByteCount(files[i].Name) + 1;
        if (cfdataLong < 0 || cfdataLong > cab.Length)
            throw new InvalidDataException("安装数据文件已损坏（文件表越界）。");
        int cfdata = (int)cfdataLong;

        var folderData = new Dictionary<int, byte[]>();

        int dp = cfdata;
        foreach (var kv in BuildFolderOrder(folders.ToArray()))
        {
            int fi = kv.Key;
            var f = folders[fi];
            using (var ms = new MemoryStream())
            {
                for (int b = 0; b < f.NumBlocks; b++)
                {
                    if (dp < 0 || dp + 8 > cab.Length)
                        throw new InvalidDataException("安装数据文件已损坏（数据块越界）。");
                    uint cbData = BitConverter.ToUInt32(cab, dp);
                    uint cbUncomp = BitConverter.ToUInt32(cab, dp + 4);
                    // Each CFDATA entry carries its own reserved bytes, whose size
                    // is declared once in the optional header as cbCFData.
                    int header = 8 + cbCFData;
                    int dataStart = dp + header;

                    // Both the declared block size and its destination are checked against the
                    // bytes that actually exist. Without this a four-byte field in the header
                    // decides how much memory is allocated and how much is copied.
                    if (cbData > (uint)(cab.Length - dataStart))
                        throw new InvalidDataException("安装数据文件已损坏（数据块声明的大小超出文件）。");
                    if (cbUncomp > MaxEntryBytes)
                        throw new InvalidDataException("安装数据文件已损坏（解压后的一项过大）。");

                    byte[] block = new byte[cbData];
                    Buffer.BlockCopy(cab, dataStart, block, 0, (int)cbData);
                    byte[] plain = Decompress(block, (int)cbUncomp, f.Compression, log);
                    ms.Write(plain, 0, plain.Length);
                    if (ms.Length > MaxEntryBytes)
                        throw new InvalidDataException("安装数据文件已损坏（解压后的文件夹过大）。");
                    dp = dataStart + (int)cbData;
                }
                folderData[fi] = ms.ToArray();
            }
        }

        // ── write out ───────────────────────────────────────────────────────
        Directory.CreateDirectory(targetDir);
        foreach (var file in files)
        {
            if (!folderData.ContainsKey(file.FolderIndex))
                throw new InvalidDataException("文件 " + file.Name + " 指向了不存在的文件夹。");

            byte[] folder = folderData[file.FolderIndex];
            if ((long)file.Offset + file.Size > folder.Length)
                throw new InvalidDataException("文件 " + file.Name + " 的数据超出范围。");

            string safe = file.Name.Replace('/', Path.DirectorySeparatorChar);
            string outPath = Path.Combine(targetDir, safe);

            // Belt and braces on top of RequireSafeName: whatever the name was, the resolved
            // path has to end up inside the install folder. Path.Combine returns its second
            // argument unchanged when that one is rooted, and the filesystem resolves "..",
            // so the only trustworthy check is on the result.
            string rootFull = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
            string outFull = Path.GetFullPath(outPath);
            if (!outFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装数据里的文件名会写到安装目录之外：" + file.Name);

            Directory.CreateDirectory(Path.GetDirectoryName(outFull));

            using (var fs = new FileStream(outFull, FileMode.Create, FileAccess.Write))
            {
                fs.Write(folder, (int)file.Offset, (int)file.Size);
            }
            log("  " + safe + "  (" + file.Size + " 字节)");
        }
    }

    /// <summary>
    /// Read the cabinet header, folder table and file table. Shared by extraction and by
    /// the entry listing so the two can never disagree about what the cabinet holds.
    /// Returns the offset of the file table, which extraction needs to find the data.
    /// </summary>
    private static uint ParseTables(byte[] cab, string cabPath, Action<string> log,
                                    out List<CfFolder> folders, out List<CfFile> files)
    {
        if (cab.Length < HeaderSize + 4)
            throw new InvalidDataException("安装数据文件太小，不是有效的 cab。");

        uint sig = BitConverter.ToUInt32(cab, 0);
        if (sig != CabinetSignature)
            throw new InvalidDataException("安装数据文件不是 cab 格式。");

        uint cbCabinet = BitConverter.ToUInt32(cab, 8);
        uint coffFiles = BitConverter.ToUInt32(cab, 16);
        ushort cFolders = BitConverter.ToUInt16(cab, 26);
        ushort cFiles = BitConverter.ToUInt16(cab, 28);
        ushort flags = BitConverter.ToUInt16(cab, 30);

        bool prevCab = (flags & 0x0001) != 0;
        bool prevFolder = (flags & 0x0002) != 0;

        if (log != null) log("cab: " + cbCabinet + " 字节, " + cFolders + " 个文件夹, " + cFiles + " 个文件");
        if (prevCab) throw new InvalidDataException("这是多段 cab，内置解包不支持。");
        if (prevFolder) throw new InvalidDataException("cab 带有跨文件夹续接，内置解包不支持。");

        int pos;
        if ((flags & 0x0004) != 0)
        {
            ushort cbCFHeader = BitConverter.ToUInt16(cab, HeaderSize);
            byte cbCFFolder = cab[HeaderSize + 2];
            pos = HeaderSize + 4 + cbCFHeader + cbCFFolder * cFolders;
        }
        else
        {
            pos = HeaderSize;
        }

        folders = new List<CfFolder>();
        for (int i = 0; i < cFolders; i++)
        {
            var f = new CfFolder();
            f.DataOffset = BitConverter.ToUInt32(cab, pos);
            f.NumBlocks = BitConverter.ToUInt16(cab, pos + 4);
            f.Compression = BitConverter.ToUInt16(cab, pos + 6);
            pos += 8;
            folders.Add(f);
        }

        pos = (int)coffFiles;
        files = new List<CfFile>();
        for (int i = 0; i < cFiles; i++)
        {
            var file = new CfFile();
            file.Size = BitConverter.ToUInt32(cab, pos);
            file.Offset = BitConverter.ToUInt32(cab, pos + 4);
            file.FolderIndex = BitConverter.ToUInt16(cab, pos + 8);
            pos += 16;

            int end = pos;
            while (end < cab.Length && cab[end] != 0) end++;
            file.Name = Encoding.GetEncoding(936).GetString(cab, pos, end - pos);
            pos = end + 1;
            files.Add(file);
        }
        if (log != null) log("已解析 " + files.Count + " 个文件条目");

        if (files.Count == 0) throw new InvalidDataException("cab 里没有文件。");
        return coffFiles;
    }

    private static IEnumerable<KeyValuePair<int, CfFolder>> BuildFolderOrder(CfFolder[] folders)
    {
        for (int i = 0; i < folders.Length; i++)
            yield return new KeyValuePair<int, CfFolder>(i, folders[i]);
    }

    private static byte[] Decompress(byte[] block, int expectedSize, ushort compression, Action<string> log)
    {
        if (compression == CompNone) return block;

        if (compression == CompMSZip)
        {
            // MSZIP blocks start with "CK" then a raw deflate stream.
            if (block.Length >= 2 && block[0] == (byte)'C' && block[1] == (byte)'K')
            {
                using (var input = new MemoryStream(block, 2, block.Length - 2))
                using (var deflate = new System.IO.Compression.DeflateStream(
                           input, System.IO.Compression.CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            log("  MSZIP 块缺少 CK 标记，按原样处理");
            return block;
        }

        throw new InvalidDataException(
            "cab 使用了内置解包不支持的压缩方式 (" + compression + ")。请确认系统 expand.exe 可用。");
    }
}

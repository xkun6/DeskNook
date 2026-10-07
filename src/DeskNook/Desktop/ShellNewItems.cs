using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DeskNook.Native;
using DeskNook.Services;
using Microsoft.Win32;

namespace DeskNook.Desktop;

/// <summary>ShellNew 定义的处理方式：None = 不处理（Handler/Command 或找不到模板，交给 Explorer）。</summary>
internal enum ShellNewKind { None, NullFile, Data, FileName }

/// <summary>ShellNew 判定结果。TemplatePath 为模板完整路径；StandardTemplateName 非空表示模板位于标准模板目录，可只传文件名给 IFileOperation.NewItem。</summary>
internal readonly record struct ShellNewSpec(ShellNewKind Kind, byte[]? Data = null, string? TemplatePath = null, string? StandardTemplateName = null);

/// <summary>
/// 桌面“新建”子菜单里的文件夹与文件由 DeskNook 自己创建（像腾讯桌面整理那样读注册表 ShellNew）：
/// 交给 Explorer 执行时，DefView 会在隐藏的系统 ListView 上进入重命名，Explorer 桌面线程因此忙 1.4~6 秒。
/// Handler/Command 类（快捷方式、库等）仍交给 Explorer。全部在 UI(STA) 线程调用。
/// </summary>
internal static class ShellNewItems
{
    public const string FolderVerb = "NewFolder";

    private const string ClassesKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew";

    private const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int SHCNE_CREATE = 0x2, SHCNE_MKDIR = 0x8;
    private const uint SHCNF_PATHW = 0x5;

    /// <summary>该动词是否属于“新建”（NewFolder 或扩展名）。</summary>
    public static bool IsNewVerb(string? verb) =>
        !string.IsNullOrEmpty(verb) && (verb.Equals(FolderVerb, StringComparison.OrdinalIgnoreCase) || verb[0] == '.');

    // ------------------------------------------------------------ 枚举可自己处理的动词

    /// <summary>NewFolder + 能自己处理的扩展名。每次现读注册表；任何异常都至少返回 NewFolder。</summary>
    public static IReadOnlyList<string> InterceptVerbs()
    {
        var list = new List<string> { FolderVerb };
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(ClassesKey);
            if (k?.GetValue("Classes") is not string[] classes) return list;
            foreach (var ext in classes)
            {
                if (string.IsNullOrEmpty(ext) || ext[0] != '.') continue;
                if (Resolve(ext).Kind != ShellNewKind.None) list.Add(ext);
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取 ShellNew 扩展名列表失败，仅拦截新建文件夹", ex);
        }
        return list;
    }

    // ------------------------------------------------------------ ShellNew 判定

    /// <summary>读取扩展名的 ShellNew 定义并判定；读不到或不能处理返回 None。</summary>
    internal static ShellNewSpec Resolve(string ext)
    {
        try
        {
            using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
            if (extKey == null) return default;
            // 与 Explorer 一致：HKCR\<ext>\<ProgID>\ShellNew 优先，其次 HKCR\<ext>\ShellNew
            var progId = extKey.GetValue(null) as string;
            using var sn = (!string.IsNullOrEmpty(progId) ? extKey.OpenSubKey(progId + @"\ShellNew") : null) ?? extKey.OpenSubKey("ShellNew");
            if (sn == null) return default;
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in sn.GetValueNames()) values[n] = sn.GetValue(n);
            return Classify(values, TemplateDirs(), File.Exists);
        }
        catch (Exception ex)
        {
            Log.Error($"解析 {ext} 的 ShellNew 失败", ex);
            return default;
        }
    }

    private static IReadOnlyList<string> TemplateDirs() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Templates),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "ShellNew"),
    };

    /// <summary>
    /// 纯函数：由 ShellNew 键的“值名→值”判定处理方式。含 Command/Handler → None；否则按 Data &gt; FileName &gt; NullFile 取第一个。
    /// Data 为 byte[] 取原字节，字符串按系统 ANSI 代码页转字节；FileName 绝对路径要求文件存在，否则依次在 templateDirs 里找。
    /// </summary>
    internal static ShellNewSpec Classify(IReadOnlyDictionary<string, object?> values, IReadOnlyList<string> templateDirs, Func<string, bool> fileExists)
    {
        var v = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
        if (v.ContainsKey("Command") || v.ContainsKey("Handler")) return default;

        if (v.TryGetValue("Data", out var data))
        {
            var bytes = data switch { byte[] b => b, string s => Ansi().GetBytes(s), _ => null };
            if (bytes != null) return new ShellNewSpec(ShellNewKind.Data, Data: bytes);
        }

        if (v.TryGetValue("FileName", out var fn) && fn is string raw && raw.Length > 0)
        {
            var name = Environment.ExpandEnvironmentVariables(raw);
            if (Path.IsPathFullyQualified(name))
                return fileExists(name) ? new ShellNewSpec(ShellNewKind.FileName, TemplatePath: name) : default;
            foreach (var dir in templateDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var full = Path.Combine(dir, name);
                if (fileExists(full)) return new ShellNewSpec(ShellNewKind.FileName, TemplatePath: full, StandardTemplateName: name);
            }
            return default;
        }

        return v.ContainsKey("NullFile") ? new ShellNewSpec(ShellNewKind.NullFile) : default;
    }

    private static Encoding Ansi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { return Encoding.UTF8; }
    }

    // ------------------------------------------------------------ 命名

    /// <summary>去掉菜单标题里的加速键标记（&amp; 与 (&amp;X)）。</summary>
    internal static string CleanTitle(string text) => Regex.Replace(text ?? "", @"\(&.\)|&", "").Trim();

    /// <summary>
    /// 生成与 Explorer 一致的名字：父菜单标题 + 标题（“新建”+“文本文档”，“New”+“Text Document”），
    /// 边界两侧有 ASCII 字母时加一个空格；重名时追加 " (2)"、" (3)"…。title 为空时文件夹用“文件夹”，文件用扩展名去点。
    /// </summary>
    internal static string BuildName(string parent, string title, string ext, Func<string, bool> exists)
    {
        var prefix = (parent ?? "").Trim();
        var t = (title ?? "").Trim();
        if (t.Length == 0) t = ext.Length == 0 ? "文件夹" : ext.TrimStart('.');
        if (prefix.Length > 0 && (IsAscii(prefix[^1]) || IsAscii(t[0]))) prefix += " ";
        var baseName = prefix + t;
        var name = baseName + ext;
        for (var i = 2; exists(name); i++) name = $"{baseName} ({i}){ext}";
        return name;
    }

    private static bool IsAscii(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // ------------------------------------------------------------ 创建

    /// <summary>
    /// 在 folder 里新建文件夹或文件，返回完整路径，失败返回 null。优先 IFileOperation.NewItem（带撤销记录），
    /// 失败再直接创建并 SHChangeNotify。必须在 UI(STA) 线程调用。
    /// </summary>
    public static string? Create(string verb, string folder, string title, string parent, IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var isFolder = verb.Equals(FolderVerb, StringComparison.OrdinalIgnoreCase);
            var ext = isFolder ? "" : verb;
            var spec = isFolder ? new ShellNewSpec(ShellNewKind.None) : Resolve(ext);
            if (!isFolder && spec.Kind == ShellNewKind.None)
            {
                Log.Info($"新建失败：{verb} 没有可用的 ShellNew 定义");
                return null;
            }

            var name = BuildName(parent, CleanTitle(title), ext, n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n)));
            var path = Path.Combine(folder, name);
            var how = isFolder ? "Folder" : spec.Kind.ToString();

            var undo = TryFileOperation(folder, name, path, isFolder, spec, hwnd);
            if (!undo && !File.Exists(path) && !Directory.Exists(path))
            {
                CreateDirect(path, isFolder, spec);
                Log.Info("新建：IFileOperation 未成功，已直接创建（未写入撤销记录）");
            }
            else if (!undo)
            {
                Log.Info("新建：IFileOperation 未返回成功但项已存在（未写入撤销记录）");
            }
            Log.Info($"新建（DeskNook 执行）：{path} 方式={how} 撤销={(undo ? "是" : "否")} 耗时 {sw.ElapsedMilliseconds} ms");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"新建失败：{verb}", ex);
            return null;
        }
    }

    /// <summary>用 IFileOperation.NewItem 创建；成功（项已存在且无中止）返回 true。内容类模板在创建后补写。</summary>
    private static bool TryFileOperation(string folder, string name, string path, bool isFolder, ShellNewSpec spec, IntPtr hwnd)
    {
        IFileOperation? op = null;
        IShellItem? dest = null;
        try
        {
            op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            op.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI | FOFX_ADDUNDORECORD);
            op.SetOwnerWindow(hwnd);
            var hr = Win32.SHCreateItemFromParsingName(folder, IntPtr.Zero, new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), out dest);
            if (hr < 0) { Log.Info($"新建：SHCreateItemFromParsingName 失败 hr=0x{hr:X}"); return false; }
            hr = op.NewItem(dest, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, name, spec.StandardTemplateName, IntPtr.Zero);
            if (hr < 0) { Log.Info($"新建：NewItem 失败 hr=0x{hr:X}"); return false; }
            hr = op.PerformOperations();
            op.GetAnyOperationsAborted(out var aborted);
            if (hr < 0 || aborted) { Log.Info($"新建：PerformOperations hr=0x{hr:X} 中止={aborted}"); return false; }
            if (!(isFolder ? Directory.Exists(path) : File.Exists(path))) { Log.Info("新建：PerformOperations 成功但项不存在"); return false; }
            if (!isFolder) FillContent(path, spec);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("新建：IFileOperation 异常", ex);
            return false;
        }
        finally
        {
            if (dest != null) Marshal.ReleaseComObject(dest);
            if (op != null) Marshal.ReleaseComObject(op);
        }
    }

    /// <summary>NewItem 建的是空文件：Data 写入字节；绝对路径的模板复制覆盖（标准目录模板已由 NewItem 带内容）。</summary>
    private static void FillContent(string path, ShellNewSpec spec)
    {
        if (spec.Kind == ShellNewKind.Data && spec.Data is { Length: > 0 }) File.WriteAllBytes(path, spec.Data);
        else if (spec.Kind == ShellNewKind.FileName && spec.StandardTemplateName == null && spec.TemplatePath != null) File.Copy(spec.TemplatePath, path, true);
    }

    private static void CreateDirect(string path, bool isFolder, ShellNewSpec spec)
    {
        if (isFolder) Directory.CreateDirectory(path);
        else
        {
            if (spec.Kind == ShellNewKind.FileName && spec.TemplatePath != null) File.Copy(spec.TemplatePath, path, true);
            else File.WriteAllBytes(path, spec.Kind == ShellNewKind.Data && spec.Data != null ? spec.Data : Array.Empty<byte>());
        }
        var p = Marshal.StringToHGlobalUni(path);
        try { Win32.SHChangeNotify(isFolder ? SHCNE_MKDIR : SHCNE_CREATE, SHCNF_PATHW, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
    }
}

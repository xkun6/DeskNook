using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>
/// 注册/卸载 DeskNookShellExt.dll（HKCU\Software\Classes，无需管理员）。
/// 为避免 Explorer 占用输出目录里的 DLL 导致重新编译失败，启动时按内容哈希复制到
/// &lt;数据根&gt;\shellext\DeskNookShellExt.&lt;hash8&gt;.dll，并注册这份副本。
/// </summary>
internal static class ShellExtRegistrar
{
    public const string ClsidText = "{EA0A2ED4-03C2-402D-A461-E558E4D20973}";
    public const string DllName = "DeskNookShellExt.dll";
    public const string HandlerName = "DeskNook";

    // 旧版遗留注册（XkDesk、DeskNext 两套），一次性清理用；名称与 CLSID 必须保持原样
    private static readonly (string Handler, string Clsid, string RunValue)[] Legacy =
    {
        ("XkDesk", "{B6F5C3A1-7D2E-4E0B-9C48-5A1E3F7D2B90}", "XkDesk"),
        ("DeskNext", "{6DF5B90B-CDC6-4C1E-8D75-5DC321045F29}", "DeskNext"),
    };

    private static readonly string[] HandlerParents = { @"*", @"Directory", @"Directory\Background" };

    public static string ShellExtDir { get; } = AppPaths.ShellExtDir;

    /// <summary>当前已注册的 DLL 路径（供代理加载、钩子兜底使用）；未注册为 null。</summary>
    public static string? RegisteredDll { get; private set; }

    /// <summary>复制 DLL 并注册；返回是否成功。</summary>
    public static bool EnsureRegistered()
    {
        try
        {
            var src = Path.Combine(AppContext.BaseDirectory, DllName);
            if (!File.Exists(src)) { Log.Error($"找不到 {src}，Shell 扩展未注册"); return false; }

            var hash = HashPrefix(src);
            Directory.CreateDirectory(ShellExtDir);
            var dst = Path.Combine(ShellExtDir, $"DeskNookShellExt.{hash}.dll");
            if (!File.Exists(dst)) File.Copy(src, dst, overwrite: false);

            var changed = CleanLegacyRegistration(migrateAutostart: true);
            changed |= Write(dst);
            RegisteredDll = dst;
            CleanOld(dst);
            if (changed)
            {
                Win32.SHChangeNotify(Win32.SHCNE_ASSOCCHANGED, Win32.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                Log.Info($"Shell 扩展已注册：{dst}");
            }
            else Log.Info($"Shell 扩展注册已是最新：{dst}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("注册 Shell 扩展失败", ex);
            return false;
        }
    }

    /// <summary>删除全部注册项与副本（--unregister）。</summary>
    public static void Unregister()
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            foreach (var parent in HandlerParents)
                classes.DeleteSubKeyTree($@"{parent}\shellex\ContextMenuHandlers\{HandlerName}", throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree($@"CLSID\{ClsidText}", throwOnMissingSubKey: false);
            CleanLegacyRegistration(migrateAutostart: false);
            DeleteAutostart();
            Win32.SHChangeNotify(Win32.SHCNE_ASSOCCHANGED, Win32.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            Log.Info("Shell 扩展注册项已删除");
        }
        catch (Exception ex)
        {
            Log.Error("删除 Shell 扩展注册项失败", ex);
        }
        CleanOld(null);
        RegisteredDll = null;
    }

    /// <summary>--unregister：删除开机自启项（卸载后不应残留）。</summary>
    private static void DeleteAutostart()
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(AutoStart.RunSubKey);
            run.DeleteValue(AutoStart.ValueName, throwOnMissingValue: false);
            Log.Info("开机自启项已删除");
        }
        catch (Exception ex) { Log.Error("删除开机自启项失败", ex); }
    }

    /// <summary>删除旧版（XkDesk、DeskNext）的 Shell 扩展注册与开机自启值；migrateAutostart 为 true 时旧自启开着则改写为新值，false（卸载）只删除。返回是否有改动。</summary>
    private static bool CleanLegacyRegistration(bool migrateAutostart)
    {
        var changed = false;
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using var run = Registry.CurrentUser.CreateSubKey(AutoStart.RunSubKey);
            var hadRun = false;
            foreach (var (handler, clsid, runValue) in Legacy)
            {
                foreach (var parent in HandlerParents)
                {
                    var sub = $@"{parent}\shellex\ContextMenuHandlers\{handler}";
                    using (var k = classes.OpenSubKey(sub)) changed |= k != null;
                    classes.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                }
                using (var k = classes.OpenSubKey($@"CLSID\{clsid}")) changed |= k != null;
                classes.DeleteSubKeyTree($@"CLSID\{clsid}", throwOnMissingSubKey: false);

                if (run.GetValue(runValue) != null)
                {
                    run.DeleteValue(runValue, throwOnMissingValue: false);
                    hadRun = true;
                }
            }
            if (hadRun)
            {
                if (migrateAutostart) AutoStart.Default.SetEnabled(true);
                Log.Info(migrateAutostart ? "已清理旧版（XkDesk/DeskNext）自启项并改写为 DeskNook" : "已清理旧版（XkDesk/DeskNext）自启项");
            }
            if (changed) Log.Info("已清理旧版（XkDesk/DeskNext）Shell 扩展注册");
        }
        catch (Exception ex) { Log.Error("清理旧版注册失败", ex); }
        return changed;
    }

    private static bool Write(string dll)
    {
        var changed = false;
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using (var inproc = classes.CreateSubKey($@"CLSID\{ClsidText}\InprocServer32"))
        {
            changed |= SetIfDifferent(inproc, null, dll);
            changed |= SetIfDifferent(inproc, "ThreadingModel", "Apartment");
        }
        using (var clsid = classes.CreateSubKey($@"CLSID\{ClsidText}")) changed |= SetIfDifferent(clsid, null, "DeskNook Context Menu");
        foreach (var parent in HandlerParents)
        {
            using var k = classes.CreateSubKey($@"{parent}\shellex\ContextMenuHandlers\{HandlerName}");
            changed |= SetIfDifferent(k, null, ClsidText);
        }
        return changed;
    }

    private static bool SetIfDifferent(RegistryKey key, string? name, string value)
    {
        if (key.GetValue(name) is string cur && cur == value) return false;
        key.SetValue(name, value);
        return true;
    }

    private static string HashPrefix(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs))[..8].ToLowerInvariant();
    }

    /// <summary>删除旧哈希的副本（被 Explorer 占用的跳过）。keep 为 null 表示全删。</summary>
    private static void CleanOld(string? keep)
    {
        try
        {
            if (!Directory.Exists(ShellExtDir)) return;
            foreach (var f in Directory.GetFiles(ShellExtDir, "DeskNookShellExt.*.dll"))
            {
                if (keep != null && string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch { /* 被占用：下次再清理 */ }
            }
        }
        catch { /* 忽略 */ }
    }
}

using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using DeskNext.Native;
using DeskNext.Services;

namespace DeskNext.Desktop;

/// <summary>
/// 注册/卸载 DeskNextShellExt.dll（HKCU\Software\Classes，无需管理员）。
/// 为避免 Explorer 占用输出目录里的 DLL 导致重新编译失败，启动时按内容哈希复制到
/// &lt;数据根&gt;\shellext\DeskNextShellExt.&lt;hash8&gt;.dll，并注册这份副本。
/// </summary>
internal static class ShellExtRegistrar
{
    public const string ClsidText = "{6DF5B90B-CDC6-4C1E-8D75-5DC321045F29}";
    public const string DllName = "DeskNextShellExt.dll";
    public const string HandlerName = "DeskNext";

    // 旧版（XkDesk）遗留注册，一次性清理用
    private const string LegacyClsidText = "{B6F5C3A1-7D2E-4E0B-9C48-5A1E3F7D2B90}";
    private const string LegacyHandlerName = "XkDesk";
    private const string LegacyRunValue = "XkDesk";

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
            var dst = Path.Combine(ShellExtDir, $"DeskNextShellExt.{hash}.dll");
            if (!File.Exists(dst)) File.Copy(src, dst, overwrite: false);

            var changed = CleanLegacyRegistration();
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
            CleanLegacyRegistration();
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

    /// <summary>删除旧版 XkDesk 的 Shell 扩展注册与开机自启值；旧自启开着则改写为新值。返回是否有改动。</summary>
    private static bool CleanLegacyRegistration()
    {
        var changed = false;
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            foreach (var parent in HandlerParents)
            {
                var sub = $@"{parent}\shellex\ContextMenuHandlers\{LegacyHandlerName}";
                using (var k = classes.OpenSubKey(sub)) changed |= k != null;
                classes.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            }
            using (var k = classes.OpenSubKey($@"CLSID\{LegacyClsidText}")) changed |= k != null;
            classes.DeleteSubKeyTree($@"CLSID\{LegacyClsidText}", throwOnMissingSubKey: false);

            using var run = Registry.CurrentUser.CreateSubKey(AutoStart.RunSubKey);
            if (run.GetValue(LegacyRunValue) != null)
            {
                run.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
                AutoStart.Default.SetEnabled(true);
                Log.Info("已清理旧版 XkDesk 自启项并改写为 DeskNext");
            }
            if (changed) Log.Info("已清理旧版 XkDesk Shell 扩展注册");
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
        using (var clsid = classes.CreateSubKey($@"CLSID\{ClsidText}")) changed |= SetIfDifferent(clsid, null, "DeskNext Context Menu");
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
            foreach (var f in Directory.GetFiles(ShellExtDir, "DeskNextShellExt.*.dll"))
            {
                if (keep != null && string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch { /* 被占用：下次再清理 */ }
            }
        }
        catch { /* 忽略 */ }
    }
}

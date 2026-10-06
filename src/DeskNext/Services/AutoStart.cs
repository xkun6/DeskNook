using Microsoft.Win32;

namespace DeskNext.Services;

/// <summary>开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeskNext = "&lt;exe 路径&gt;"。状态一律以注册表为准。</summary>
internal sealed class AutoStart
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DeskNext";

    private readonly string _subKey;
    private readonly string _valueName;
    private readonly Func<string?> _exePath;

    /// <summary>真实注册表位置、当前进程 exe。</summary>
    public static AutoStart Default { get; } = new();

    /// <param name="subKey">HKCU 下的子键（测试时换成专用键）。</param>
    public AutoStart(string subKey = RunSubKey, string valueName = ValueName, Func<string?>? exePath = null)
    {
        _subKey = subKey;
        _valueName = valueName;
        _exePath = exePath ?? (() => Environment.ProcessPath);
    }

    /// <summary>注册表里是否存在自启项。</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_subKey);
                return key?.GetValue(_valueName) is string { Length: > 0 };
            }
            catch (Exception ex)
            {
                Log.Error("读取开机自启状态失败", ex);
                return false;
            }
        }
    }

    /// <summary>注册表里当前的值（无则 null）。</summary>
    public string? CurrentValue
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_subKey);
                return key?.GetValue(_valueName) as string;
            }
            catch { return null; }
        }
    }

    /// <summary>启用（写入带引号的 exe 路径）或禁用（删除值）。返回是否成功。</summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_subKey);
            if (enabled)
            {
                var exe = _exePath();
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(_valueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else key.DeleteValue(_valueName, throwOnMissingValue: false);
            Log.Info($"开机自启已{(enabled ? "开启" : "关闭")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }

    public bool Toggle() => SetEnabled(!IsEnabled);
}

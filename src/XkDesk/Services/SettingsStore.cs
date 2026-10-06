using System.IO;
using System.Text.Json;
using XkDesk.Model;

namespace XkDesk.Services;

/// <summary>设置持久化：%AppData%\XkDesk\settings.json，先写临时文件再替换；损坏时备份并回退默认。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XkDesk", "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                    ?? throw new JsonException("内容为 null");
            s.OrganizeRules = (s.OrganizeRules ?? AppSettings.DefaultRules())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new OrganizeRule(r.Name.Trim(), (r.Extensions ?? new()).Where(e => e != null).ToArray()))
                .ToList();
            return s;
        }
        catch (Exception ex)
        {
            Log.Error($"settings.json 损坏，已备份并回退默认：{FilePath}", ex);
            try { File.Move(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMddHHmmssfff}"); }
            catch (Exception ex2) { Log.Error("备份损坏的 settings.json 失败", ex2); }
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("保存 settings.json 失败", ex);
            return false;
        }
    }
}

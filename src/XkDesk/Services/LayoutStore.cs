using System.IO;
using System.Text.Json;
using XkDesk.Model;

namespace XkDesk.Services;

/// <summary>布局持久化：%AppData%\XkDesk\layout.json，先写临时文件再替换。</summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string FilePath { get; }

    public LayoutStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XkDesk", "layout.json");
    }

    public LayoutState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return new LayoutState();
            try
            {
                var s = JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(FilePath), Options)
                        ?? throw new JsonException("内容为 null");
                s.FreeIcons = s.FreeIcons == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, IconSlot>(s.FreeIcons.Where(kv => kv.Value != null), StringComparer.OrdinalIgnoreCase);
                s.Boxes ??= new();
                s.View ??= new();
                return s;
            }
            catch (Exception ex)
            {
                Log.Error($"layout.json 损坏，已备份并回退默认：{FilePath}", ex);
                try { File.Move(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMddHHmmssfff}"); }
                catch (Exception ex2) { Log.Error("备份损坏的 layout.json 失败", ex2); }
                return new LayoutState();
            }
        }
    }

    /// <summary>保存；失败只记日志并返回 false。</summary>
    public bool Save(LayoutState state)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("保存 layout.json 失败", ex);
                return false;
            }
        }
    }
}

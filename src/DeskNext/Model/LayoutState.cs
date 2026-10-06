using System.Text.Json.Serialization;

namespace DeskNext.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoxKind { Normal, Mapped }

/// <summary>格子矩形：相对所在显示器工作区左上角的 DIP（展开状态下的完整尺寸）。</summary>
public sealed class BoxRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public BoxRect() { }
    public BoxRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }

    [System.Text.Json.Serialization.JsonIgnore] public double Right => X + W;
    [System.Text.Json.Serialization.JsonIgnore] public double Bottom => Y + H;
    public BoxRect Clone() => new(X, Y, W, H);
}

/// <summary>桌面格子。Normal：ItemKeys 记录桌面项；Mapped：内容来自 MappedPath 目录（ItemKeys 不用）。</summary>
public sealed class BoxState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public BoxKind Kind { get; set; } = BoxKind.Normal;
    public string? MappedPath { get; set; }
    /// <summary>所在显示器设备名；显示器不存在时临时显示在主屏，但这里不改写。</summary>
    public string Monitor { get; set; } = "";
    public BoxRect Rect { get; set; } = new();
    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    /// <summary>"" = 手动顺序 | name | date | size | type</summary>
    public string SortMode { get; set; } = "";
    public List<string> ItemKeys { get; set; } = new();
    /// <summary>ItemKeys 中已从桌面消失的 key → 消失时间（7 天保留规则）。</summary>
    public Dictionary<string, DateTime> Gone { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>自由图标所在格子；LastSeenUtc 仅在项消失后记录消失时间。</summary>
public sealed class IconSlot
{
    public string Monitor { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

public sealed class ViewSettings
{
    public int IconSize { get; set; } = 48;
    /// <summary>"" | name | size | type | date</summary>
    public string SortKey { get; set; } = "";
    /// <summary>双击/菜单隐藏了图标与格子（重启后保持）。</summary>
    public bool IconsHidden { get; set; }
}

public sealed class LayoutState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, IconSlot> FreeIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BoxState> Boxes { get; set; } = new();
    public ViewSettings View { get; set; } = new();
    public bool SystemPositionsImported { get; set; }
}

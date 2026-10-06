using System.Text.Json.Serialization;

namespace XkDesk.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoxKind { Normal, Mapped }

/// <summary>格子（阶段 2 使用，当前仅占位）。</summary>
public sealed class BoxState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public BoxKind Kind { get; set; } = BoxKind.Normal;
    public string? MappedPath { get; set; }
    public string Monitor { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool Collapsed { get; set; }
    public List<string> ItemKeys { get; set; } = new();
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
}

public sealed class LayoutState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, IconSlot> FreeIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BoxState> Boxes { get; set; } = new();
    public ViewSettings View { get; set; } = new();
    public bool SystemPositionsImported { get; set; }
}

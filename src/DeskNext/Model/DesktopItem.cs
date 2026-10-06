namespace DeskNext.Model;

/// <summary>桌面项（来自桌面根 IShellFolder 枚举）。</summary>
public sealed class DesktopItem
{
    /// <summary>唯一键：桌面项为解析名；映射格子内的项带「格子Id + 分隔符」前缀。</summary>
    public string Key { get; init; } = "";
    /// <summary>所属来源：空 = 桌面；否则为映射格子 Id（同一 Container 的项共用同一父文件夹）。</summary>
    public string Container { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string EditName { get; init; } = "";
    /// <summary>绝对 PIDL（从桌面根起）的完整字节拷贝；桌面项即相对桌面根的子 PIDL。</summary>
    public byte[] Pidl { get; init; } = Array.Empty<byte>();
    public uint Attributes { get; init; }
    public string? FilePath { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    /// <summary>小写含点，无则空。</summary>
    public string Extension { get; init; } = "";

    public bool IsFolder => (Attributes & 0x20000000) != 0;
    public bool IsLink => (Attributes & 0x00010000) != 0;
    public bool IsHidden => (Attributes & 0x00080000) != 0;
    public bool IsGhosted => (Attributes & 0x00008000) != 0;
    public bool IsVirtual => FilePath == null;

    /// <summary>用于判定“更新”的指纹。</summary>
    public string Fingerprint => $"{DisplayName}|{Attributes}|{Size}|{Modified.Ticks}";
}

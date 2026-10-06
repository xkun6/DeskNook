namespace XkDesk.Model;

/// <summary>桌面项（来自桌面根 IShellFolder 枚举）。</summary>
public sealed class DesktopItem
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string EditName { get; init; } = "";
    /// <summary>相对桌面根的子 PIDL 完整字节拷贝。</summary>
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

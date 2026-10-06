namespace XkDesk.Model;

/// <summary>一键整理规则：分类名 + 扩展名列表（不含点、小写）。特殊项："&lt;dir&gt;" = 文件夹，"*" = 其余所有项。</summary>
public sealed class OrganizeRule
{
    public const string FolderToken = "<dir>";
    public const string AnyToken = "*";

    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();

    public OrganizeRule() { }
    public OrganizeRule(string name, params string[] extensions) { Name = name; Extensions = extensions.ToList(); }

    public OrganizeRule Clone() => new(Name, Extensions.ToArray());
}

/// <summary>应用设置：data\settings.json。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    /// <summary>双击桌面空白处隐藏/显示图标与格子（默认开启）。</summary>
    public bool DoubleClickToggle { get; set; } = true;
    /// <summary>图标大小：system（跟随系统桌面）/ small(32) / medium(48) / large(96)。</summary>
    public string IconSizeMode { get; set; } = "system";
    /// <summary>格子背景不透明度 0.2~1.0（默认 0.7）。</summary>
    public double BoxOpacity { get; set; } = DefaultBoxOpacity;

    public const double DefaultBoxOpacity = 0.7;
    public const double MinBoxOpacity = 0.2;

    /// <summary>图标大小模式对应的像素；system 返回 null。</summary>
    public static int? IconSizeOf(string? mode) => mode switch { "small" => 32, "medium" => 48, "large" => 96, _ => null };

    /// <summary>规整化：未知模式回到 system，透明度夹到范围内（旧 settings.json 缺字段时取默认）。</summary>
    public void Normalize()
    {
        if (IconSizeOf(IconSizeMode) == null) IconSizeMode = "system";
        if (double.IsNaN(BoxOpacity)) BoxOpacity = DefaultBoxOpacity;
        BoxOpacity = Math.Clamp(BoxOpacity, MinBoxOpacity, 1.0);
    }
    /// <summary>规则顺序即优先级：先匹配到的分类生效。</summary>
    public List<OrganizeRule> OrganizeRules { get; set; } = DefaultRules();

    public static List<OrganizeRule> DefaultRules() => new()
    {
        new("文件夹", OrganizeRule.FolderToken),
        new("快捷方式与程序", "lnk", "url", "exe", "msi", "msix", "appx", "bat", "cmd", "appref-ms", "com", "ps1", "vbs", "jar"),
        new("文档", "doc", "docx", "dot", "dotx", "xls", "xlsx", "xlsm", "ppt", "pptx", "pdf", "txt", "md", "wps", "et", "dps", "csv", "rtf", "odt", "ods", "odp", "epub"),
        new("图片", "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "heic", "avif", "psd", "raw"),
        new("视频", "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "rmvb", "3gp"),
        new("音频", "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "ape", "opus", "mid"),
        new("压缩包", "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "tgz", "iso", "cab"),
        new("其他", OrganizeRule.AnyToken),
    };
}

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

/// <summary>应用设置：%AppData%\XkDesk\settings.json。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
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

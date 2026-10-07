using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>桌面“新建”子菜单自建项：命名与 ShellNew 判定的纯函数。</summary>
public class ShellNewItemsTests
{
    private static readonly string[] Dirs = { @"C:\T\Templates", @"C:\T\Common", @"C:\Windows\ShellNew" };

    private static ShellNewSpec Classify(Dictionary<string, object?> v, Func<string, bool>? exists = null) =>
        ShellNewItems.Classify(v, Dirs, exists ?? (_ => false));

    [Theory]
    [InlineData("新建", "文本文档", ".txt", "新建文本文档.txt")]
    [InlineData("新建", "文件夹", "", "新建文件夹")]
    [InlineData("New", "Text Document", ".txt", "New Text Document.txt")]
    [InlineData("New", "Folder", "", "New Folder")]
    [InlineData("新建", "Microsoft Word 文档", ".docx", "新建 Microsoft Word 文档.docx")]
    public void BuildName_前缀拼接(string parent, string title, string ext, string expected) =>
        Assert.Equal(expected, ShellNewItems.BuildName(parent, title, ext, _ => false));

    [Fact]
    public void BuildName_重名递增()
    {
        var taken = new HashSet<string> { "新建文本文档.txt", "新建文本文档 (2).txt" };
        Assert.Equal("新建文本文档 (3).txt", ShellNewItems.BuildName("新建", "文本文档", ".txt", taken.Contains));
    }

    [Fact]
    public void BuildName_文件夹重名()
    {
        var taken = new HashSet<string> { "新建文件夹" };
        Assert.Equal("新建文件夹 (2)", ShellNewItems.BuildName("新建", "文件夹", "", taken.Contains));
    }

    [Fact]
    public void BuildName_空标题()
    {
        Assert.Equal("新建文件夹", ShellNewItems.BuildName("新建", "", "", _ => false));
        Assert.Equal("新建 txt.txt", ShellNewItems.BuildName("新建", "  ", ".txt", _ => false));
    }

    [Fact]
    public void BuildName_前缀首尾空白被去掉() =>
        Assert.Equal("新建文本文档.txt", ShellNewItems.BuildName(" 新建 ", "文本文档", ".txt", _ => false));

    [Fact]
    public void CleanTitle_去掉加速键() =>
        Assert.Equal("文本文档", ShellNewItems.CleanTitle("文本文档(&T)"));

    [Theory]
    [InlineData("Handler")]
    [InlineData("Command")]
    public void Classify_Handler或Command不处理(string name)
    {
        var spec = Classify(new() { [name] = "x", ["NullFile"] = "" });
        Assert.Equal(ShellNewKind.None, spec.Kind);
    }

    [Fact]
    public void Classify_NullFile() =>
        Assert.Equal(ShellNewKind.NullFile, Classify(new() { ["ItemName"] = "x", ["NullFile"] = "" }).Kind);

    [Fact]
    public void Classify_Data优先于NullFile()
    {
        var spec = Classify(new() { ["NullFile"] = "", ["Data"] = new byte[] { 1, 2, 3 } });
        Assert.Equal(ShellNewKind.Data, spec.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, spec.Data);
    }

    [Fact]
    public void Classify_字符串Data按ANSI转字节()
    {
        var spec = Classify(new() { ["Data"] = "{\rtf1}" });
        Assert.Equal(ShellNewKind.Data, spec.Kind);
        Assert.Equal("{\rtf1}"u8.ToArray(), spec.Data);
    }

    [Fact]
    public void Classify_FileName找不到模板不处理() =>
        Assert.Equal(ShellNewKind.None, Classify(new() { ["FileName"] = "nope.xls" }).Kind);

    [Fact]
    public void Classify_FileName在标准目录找到()
    {
        var hit = Path.Combine(Dirs[1], "a.xls");
        var spec = Classify(new() { ["FileName"] = "a.xls" }, p => p == hit);
        Assert.Equal(ShellNewKind.FileName, spec.Kind);
        Assert.Equal(hit, spec.TemplatePath);
        Assert.Equal("a.xls", spec.StandardTemplateName);
    }

    [Fact]
    public void Classify_FileName绝对路径要求存在()
    {
        const string abs = @"D:\tpl\a.xls";
        Assert.Equal(ShellNewKind.None, Classify(new() { ["FileName"] = abs }).Kind);
        var spec = Classify(new() { ["FileName"] = abs }, p => p == abs);
        Assert.Equal(ShellNewKind.FileName, spec.Kind);
        Assert.Equal(abs, spec.TemplatePath);
        Assert.Null(spec.StandardTemplateName);
    }

    [Theory]
    [InlineData("NewFolder", true)]
    [InlineData("newfolder", true)]
    [InlineData(".txt", true)]
    [InlineData("rename", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNewVerb(string? verb, bool expected) => Assert.Equal(expected, ShellNewItems.IsNewVerb(verb));
}

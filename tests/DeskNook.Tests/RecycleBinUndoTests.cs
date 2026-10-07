using System.Text;
using DeskNook.Desktop;

namespace DeskNook.Tests;

/// <summary>删除撤销：$I 解析与匹配的纯函数（不触碰真实回收站与桌面）。</summary>
public class RecycleBinUndoTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private static byte[] V1(string path, DateTime deleted)
    {
        var b = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(b, 0);
        BitConverter.GetBytes(1234L).CopyTo(b, 8);
        BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(b, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(b, 24);
        return b;
    }

    private static byte[] V2(string path, DateTime deleted)
    {
        var chars = path.Length + 1;
        var b = new byte[28 + chars * 2];
        BitConverter.GetBytes(2L).CopyTo(b, 0);
        BitConverter.GetBytes(1234L).CopyTo(b, 8);
        BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(b, 16);
        BitConverter.GetBytes(chars).CopyTo(b, 24);
        Encoding.Unicode.GetBytes(path).CopyTo(b, 28);
        return b;
    }

    [Fact]
    public void ParseInfo_v1()
    {
        var r = RecycleBinUndo.ParseInfo(V1(@"C:\Users\x\Desktop\a.txt", T0));
        Assert.NotNull(r);
        Assert.Equal(@"C:\Users\x\Desktop\a.txt", r!.Value.OriginalPath);
        Assert.Equal(T0, r.Value.DeletedUtc);
    }

    [Fact]
    public void ParseInfo_v2()
    {
        var r = RecycleBinUndo.ParseInfo(V2(@"D:\桌面\新建 文件.txt", T0));
        Assert.NotNull(r);
        Assert.Equal(@"D:\桌面\新建 文件.txt", r!.Value.OriginalPath);
        Assert.Equal(T0, r.Value.DeletedUtc);
    }

    [Fact]
    public void ParseInfo_非法数据返回null()
    {
        Assert.Null(RecycleBinUndo.ParseInfo(Array.Empty<byte>()));
        Assert.Null(RecycleBinUndo.ParseInfo(new byte[20]));
        Assert.Null(RecycleBinUndo.ParseInfo(V1(@"C:\a.txt", T0)[..300]));      // v1 截断
        Assert.Null(RecycleBinUndo.ParseInfo(V2(@"C:\a.txt", T0)[..30]));       // v2 截断
        var badCount = V2(@"C:\a.txt", T0);
        BitConverter.GetBytes(9999).CopyTo(badCount, 24);                        // 字符数超出长度
        Assert.Null(RecycleBinUndo.ParseInfo(badCount));
        BitConverter.GetBytes(0).CopyTo(badCount, 24);                           // 字符数为 0
        Assert.Null(RecycleBinUndo.ParseInfo(badCount));
        var badVer = V1(@"C:\a.txt", T0);
        BitConverter.GetBytes(3L).CopyTo(badVer, 0);                             // 未知版本
        Assert.Null(RecycleBinUndo.ParseInfo(badVer));
    }

    [Fact]
    public void Match_选删除时间最新的并换成R路径()
    {
        var entries = new[]
        {
            (@"C:\$Recycle.Bin\S\$IAAA.txt", @"C:\Desk\a.txt", T0),
            (@"C:\$Recycle.Bin\S\$IBBB.txt", @"C:\Desk\a.txt", T0.AddMinutes(1)),
            (@"C:\$Recycle.Bin\S\$ICCC.txt", @"C:\Desk\b.txt", T0),
        };
        var r = RecycleBinUndo.Match(entries, new[] { @"C:\Desk\a.txt" }, T0);
        var m = Assert.Single(r);
        Assert.Equal(@"C:\$Recycle.Bin\S\$IBBB.txt", m.InfoPath);
        Assert.Equal(@"C:\$Recycle.Bin\S\$RBBB.txt", m.DataPath);
        Assert.Equal(@"C:\Desk\a.txt", m.Original);
    }

    [Fact]
    public void Match_忽略早于since的条目()
    {
        var entries = new[]
        {
            (@"C:\$Recycle.Bin\S\$IOLD.txt", @"C:\Desk\a.txt", T0.AddHours(-1)),
        };
        Assert.Empty(RecycleBinUndo.Match(entries, new[] { @"C:\Desk\a.txt" }, T0));
    }

    [Fact]
    public void Match_路径不区分大小写()
    {
        var entries = new[] { (@"C:\$Recycle.Bin\S\$IXYZ", @"c:\desk\A.TXT", T0) };
        var r = RecycleBinUndo.Match(entries, new[] { @"C:\Desk\a.txt" }, T0);
        var m = Assert.Single(r);
        Assert.Equal(@"C:\$Recycle.Bin\S\$RXYZ", m.DataPath);
        Assert.Equal(@"c:\desk\A.TXT", m.Original);
    }

    [Fact]
    public void Match_多个记录路径各自匹配()
    {
        var entries = new[]
        {
            (@"C:\$Recycle.Bin\S\$I111.txt", @"C:\Desk\a.txt", T0),
            (@"C:\$Recycle.Bin\S\$I222.txt", @"C:\Desk\b.txt", T0),
        };
        var r = RecycleBinUndo.Match(entries, new[] { @"C:\Desk\b.txt", @"C:\Desk\a.txt", @"C:\Desk\none.txt" }, T0);
        Assert.Equal(2, r.Count);
        Assert.Equal(@"C:\$Recycle.Bin\S\$R222.txt", r[0].DataPath);
        Assert.Equal(@"C:\$Recycle.Bin\S\$R111.txt", r[1].DataPath);
    }
}

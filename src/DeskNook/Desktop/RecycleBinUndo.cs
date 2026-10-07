using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>
/// 桌面整理发起的删除，Ctrl+Z 时直接从回收站把文件移回原位：
/// Shell 自带的撤销/还原会让 Explorer 桌面线程为每个文件在系统 ListView 里找空位（comctl32!CLVSlotsManager::FindFreeSlot），
/// 约 260 图标时每项约 2 秒；解析 $I 记录后把 $R 直接 Move 回去，几毫秒完成（思路同腾讯桌面整理的 UndoDeleteManager）。
/// </summary>
internal static class RecycleBinUndo
{
    private const int SHCNE_CREATE = 0x2, SHCNE_MKDIR = 0x8;
    private const uint SHCNF_PATHW = 0x5;

    /// <summary>解析回收站 $I 文件：v1（固定 520 字节路径）与 v2（带字符数的变长路径）。格式不对返回 null。</summary>
    internal static (string OriginalPath, DateTime DeletedUtc)? ParseInfo(byte[] data)
    {
        if (data == null || data.Length < 28) return null;
        var version = BitConverter.ToInt64(data, 0);
        var ft = BitConverter.ToInt64(data, 16);
        string path;
        if (version == 1)
        {
            if (data.Length < 24 + 520) return null;
            path = Encoding.Unicode.GetString(data, 24, 520);
        }
        else if (version == 2)
        {
            var chars = BitConverter.ToInt32(data, 24);
            if (chars < 1 || (long)28 + (long)chars * 2 > data.Length) return null;
            path = Encoding.Unicode.GetString(data, 28, chars * 2);
        }
        else return null;

        var nul = path.IndexOf('\0');
        if (nul >= 0) path = path[..nul];
        if (path.Length == 0) return null;
        DateTime deleted;
        try { deleted = DateTime.FromFileTimeUtc(ft); }
        catch (ArgumentOutOfRangeException) { return null; }
        return (path, deleted);
    }

    /// <summary>对每个记录路径（不区分大小写）在删除时间不早于 sinceUtc 的条目里取最新的一条；$I 同目录下的 $R 即数据文件。</summary>
    internal static List<(string InfoPath, string DataPath, string Original)> Match(
        IEnumerable<(string InfoPath, string Original, DateTime DeletedUtc)> entries,
        IReadOnlyCollection<string> recordedPaths, DateTime sinceUtc)
    {
        var list = entries.Where(e => e.DeletedUtc >= sinceUtc).ToList();
        var result = new List<(string, string, string)>();
        foreach (var p in recordedPaths)
        {
            (string InfoPath, string Original, DateTime DeletedUtc)? best = null;
            foreach (var e in list)
            {
                if (!string.Equals(e.Original, p, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || e.DeletedUtc > best.Value.DeletedUtc) best = e;
            }
            if (best is not { } b) continue;
            var dir = Path.GetDirectoryName(b.InfoPath) ?? "";
            var name = Path.GetFileName(b.InfoPath);
            if (name.Length < 2 || !name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add((b.InfoPath, Path.Combine(dir, "$R" + name[2..]), b.Original));
        }
        return result;
    }

    /// <summary>把 paths 里在 sinceUtc 之后被删进回收站的项移回原位；返回成功数。每项独立 try/catch，不外抛。</summary>
    internal static int Restore(IReadOnlyCollection<string> paths, DateTime sinceUtc)
    {
        var sw = Stopwatch.StartNew();
        var done = 0;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) { Log.Info("删除撤销：取不到当前用户 SID"); return 0; }
            foreach (var group in paths.Where(p => !string.IsNullOrEmpty(p)).GroupBy(p => Path.GetPathRoot(p) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0) continue;
                try
                {
                    var bin = Path.Combine(group.Key, "$Recycle.Bin", sid);
                    if (!Directory.Exists(bin)) { Log.Info($"删除撤销：{group.Key} 没有回收站目录，跳过"); continue; }
                    var entries = new List<(string InfoPath, string Original, DateTime DeletedUtc)>();
                    foreach (var f in Directory.EnumerateFiles(bin, "$I*"))
                    {
                        try
                        {
                            if (ParseInfo(File.ReadAllBytes(f)) is { } info) entries.Add((f, info.OriginalPath, info.DeletedUtc));
                        }
                        catch (Exception ex) { Log.Info($"删除撤销：读取记录失败 {Path.GetFileName(f)}：{ex.Message}"); }
                    }
                    foreach (var m in Match(entries, group.ToList(), sinceUtc))
                        if (RestoreOne(m.InfoPath, m.DataPath, m.Original)) done++;
                    var missing = group.Count(p => !entries.Any(e => e.DeletedUtc >= sinceUtc && string.Equals(e.Original, p, StringComparison.OrdinalIgnoreCase)));
                    if (missing > 0) Log.Info($"删除撤销：{missing} 项在回收站里没有对应记录（可能已永久删除或清空）");
                }
                catch (Exception ex) { Log.Error($"删除撤销：处理卷 {group.Key} 失败", ex); }
            }
        }
        catch (Exception ex) { Log.Error("删除撤销异常", ex); }
        Log.Info($"删除撤销：还原 {done}/{paths.Count} 项，耗时 {sw.ElapsedMilliseconds} ms");
        return done;
    }

    private static bool RestoreOne(string infoPath, string dataPath, string original)
    {
        try
        {
            if (File.Exists(original) || Directory.Exists(original)) { Log.Info($"删除撤销：跳过，原路径已存在 {original}"); return false; }
            var parent = Path.GetDirectoryName(original);
            if (parent == null || !Directory.Exists(parent)) { Log.Info($"删除撤销：跳过，原父目录不存在 {original}"); return false; }

            bool isDir;
            if (Directory.Exists(dataPath)) { Directory.Move(dataPath, original); isDir = true; }
            else if (File.Exists(dataPath)) { File.Move(dataPath, original); isDir = false; }
            else { Log.Info($"删除撤销：跳过，回收站数据文件不存在 {Path.GetFileName(dataPath)}"); return false; }

            try { File.Delete(infoPath); } catch (Exception ex) { Log.Info($"删除撤销：删除记录失败 {Path.GetFileName(infoPath)}：{ex.Message}"); }
            var p = Marshal.StringToHGlobalUni(original);
            try { Win32.SHChangeNotify(isDir ? SHCNE_MKDIR : SHCNE_CREATE, SHCNF_PATHW, p, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(p); }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"删除撤销：还原失败 {original}", ex);
            return false;
        }
    }
}

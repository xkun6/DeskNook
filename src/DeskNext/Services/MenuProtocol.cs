using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskNext.Services;

/// <summary>DeskNext 返回给 Shell 扩展、由扩展插入 Explorer 菜单的一项（叶子带 Id；有 Children 的是子菜单；Sep 为分隔线）。</summary>
public sealed class WireMenuItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("icon"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Icon { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("checked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Checked { get; set; }
    [JsonPropertyName("radio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Radio { get; set; }
    [JsonPropertyName("sep"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Sep { get; set; }
    /// <summary>插入位置：top / bottom / afterOpen（默认 bottom）。</summary>
    [JsonPropertyName("pos")] public string Pos { get; set; } = "bottom";
    [JsonPropertyName("children"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<WireMenuItem>? Children { get; set; }
}

/// <summary>Shell 扩展 → DeskNext 的查询：当前菜单的上下文。</summary>
public sealed class MenuQuery
{
    /// <summary>item / background。</summary>
    public string Kind { get; set; } = "item";
    public string Folder { get; set; } = "";
    public List<string> Items { get; set; } = new();
    public bool Shift { get; set; }
    public string Proc { get; set; } = "";
    public int Pid { get; set; }
    /// <summary>DeskNext 发起的代理请求 Id；普通资源管理器窗口里的右键为空。</summary>
    public string Req { get; set; } = "";
}

/// <summary>代理（Explorer 内）→ DeskNext 的事件。</summary>
public sealed class ProxyEvent
{
    /// <summary>closed（菜单关闭）/ pick（用户选了命令，执行前）/ verb（被拦截的动词）/ invoked（已执行的命令）。</summary>
    public string Type { get; set; } = "";
    public string Req { get; set; } = "";
    public string Verb { get; set; } = "";
    public string Title { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Picked { get; set; }
    public bool DefView { get; set; }
    /// <summary>被选命令所在的父菜单标题（如“新建”“排序方式”“查看”）；顶层为空。</summary>
    public string Parent { get; set; } = "";
    public int Hr { get; set; }
}

/// <summary>DeskNext → 代理（WM_COPYDATA）的菜单请求。</summary>
public sealed class ProxyRequest
{
    [JsonPropertyName("t")] public string T { get; set; } = "menu";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>item / background。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "item";
    /// <summary>所在文件夹解析名；桌面为 ::desktop。</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "::desktop";
    [JsonPropertyName("items")] public List<string> Items { get; set; } = new();
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("shift")] public bool Shift { get; set; }
    [JsonPropertyName("iconsVisible")] public bool IconsVisible { get; set; } = true;
    [JsonPropertyName("interceptVerbs")] public List<string> InterceptVerbs { get; set; } = new();
}

/// <summary>管道与 WM_COPYDATA 的 JSON 编解码（C++ 侧是手写的极简解析，字段名必须与这里一致）。</summary>
public static class MenuProtocol
{
    public const string PipeName = "DeskNext.Menu";
    public const string ProxyWindowClass = "DeskNext.MenuProxy";
    public const long CopyDataMagic = 0x4B584D31;

    private static readonly JsonSerializerOptions Opts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeQueryResult(long queryId, IReadOnlyList<WireMenuItem> items) =>
        JsonSerializer.Serialize(new QueryResultDto { Q = queryId, Items = items }, Opts);

    public static string SerializeRequest(ProxyRequest r) => JsonSerializer.Serialize(r, Opts);

    public static IReadOnlyList<WireMenuItem> ParseQueryResult(string json, out long queryId)
    {
        var dto = JsonSerializer.Deserialize<QueryResultDto>(json, Opts);
        queryId = dto?.Q ?? 0;
        return dto?.Items ?? new List<WireMenuItem>();
    }

    /// <summary>解析管道上收到的一行：返回消息类型 query / invoke / closed / verb / invoked，其余为 null。</summary>
    public static string? ParseMessageType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("t", out var t) ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static MenuQuery? ParseQuery(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var q = new MenuQuery
            {
                Kind = Str(r, "kind"), Folder = Str(r, "folder"), Proc = Str(r, "proc"), Req = Str(r, "req"),
                Shift = r.TryGetProperty("shift", out var s) && s.ValueKind == JsonValueKind.True,
                Pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pi) ? pi : 0,
            };
            if (r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) q.Items.Add(e.GetString() ?? "");
            return q;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>invoke 消息：返回 (查询 Id, 项 Id)。</summary>
    public static (long Query, int Item)? ParseInvoke(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("q", out var q) || !q.TryGetInt64(out var qi)) return null;
            if (!r.TryGetProperty("id", out var i) || !i.TryGetInt32(out var ii)) return null;
            return (qi, ii);
        }
        catch (JsonException) { return null; }
    }

    public static ProxyEvent? ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return new ProxyEvent
            {
                Type = Str(r, "t"), Req = Str(r, "req"), Verb = Str(r, "verb"), Title = Str(r, "title"), Error = Str(r, "error"),
                Picked = Bool(r, "picked"), DefView = Bool(r, "defView"), Parent = Str(r, "parent"),
                Hr = r.TryGetProperty("hr", out var hr) && hr.TryGetInt32(out var hri) ? hri : 0,
            };
        }
        catch (JsonException) { return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private sealed class QueryResultDto
    {
        [JsonPropertyName("q")] public long Q { get; set; }
        [JsonPropertyName("items")] public IReadOnlyList<WireMenuItem>? Items { get; set; }
    }
}

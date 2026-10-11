using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// ntfs-meta [drive] [--port N]  —  read-only NTFS metadata server (REST at /api, MCP at /mcp)
string? d = null;
int port = 7801;
bool ok = true;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--port") ok &= ++i < args.Length && int.TryParse(args[i], out port) && port is >= 1 and <= 65535;
    else if (args[i].StartsWith("--") || d != null) ok = false;
    else d = args[i];
var drive = (d ?? "C").TrimEnd(':', '\\').ToUpperInvariant();
if (!ok || drive.Length != 1 || !char.IsAsciiLetter(drive[0])) { Console.Error.WriteLine("usage: ntfs-meta [drive] [--port N]"); return 1; }

Index index;
var sw = Stopwatch.StartNew();
try { index = new Index(new Volume(drive)); index.Load(); }
catch (Exception e) { Console.Error.WriteLine(e is UnauthorizedAccessException ? "Access denied: run as administrator." : e.Message); return 1; }
Console.WriteLine($"Indexed {index.Count:N0} MFT records on {drive}: in {sw.Elapsed.TotalSeconds:0.0}s");
new Thread(index.Watch) { IsBackground = true }.Start();

var json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
Tool[] tools =
[
    new("summary", "Volume capacity, used/free space, file and directory counts.",
        [], a => index.Summary()),
    new("list", "Immediate children of a directory with recursive on-disk size, file count and newest modification inside, largest first. The main tool for drilling down.",
        [P.Path, P.Limit], a => index.List(a.Str("path"), a.Limit(50))),
    new("largest", "Largest files under a path. Filter by extension and/or by files not modified in N days (cold-storage candidates). Also reports total size of all matches.",
        [P.Path, P.Limit, new("ext", "string", "Extension filter, e.g. 'mp4'"), new("older_than_days", "integer", "Only files last modified more than N days ago")],
        a => index.Largest(a.Str("path"), a.Limit(50), a.StrOrNull("ext"), a.Int("older_than_days", 0))),
    new("extensions", "On-disk size and file count grouped by file extension under a path.",
        [P.Path, P.Limit], a => index.Extensions(a.Str("path"), a.Limit(30))),
    new("duplicates", "Likely duplicate files under a path: same name and same size (contents are not compared). Sorted by reclaimable space.",
        [P.Path, P.Limit, new("min_mb", "integer", "Ignore files smaller than this (default 10)")],
        a => index.Duplicates(a.Str("path"), a.Int("min_mb", 10) * 1024L * 1024, a.Limit(30))),
    new("find", "Files and directories whose name matches a wildcard pattern (* and ?, case-insensitive), largest first.",
        [new("pattern", "string", "Wildcard pattern, e.g. '*.iso' or 'node_modules'", true), P.Path, P.Limit],
        a => index.Find(a.Str("pattern"), a.Str("path"), a.Limit(100))),
];

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1 << 20);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

// DNS-rebinding protection (required by the MCP spec): Host must be a loopback name, and any Origin must be loopback too.
app.Use(async (ctx, next) =>
{
    string origin = ctx.Request.Headers.Origin.ToString();
    bool hostOk = ctx.Request.Host.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    if (!hostOk || origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback)) ctx.Response.StatusCode = 403;
    else await next();
});

app.MapGet("/api", () => Results.Json(tools.Select(t => new { t.Name, t.Description, parameters = t.Params.Select(p => p.Name) })));
app.MapGet("/api/{name}", (string name, HttpRequest req) =>
{
    var tool = tools.FirstOrDefault(t => t.Name == name);
    if (tool == null) return Results.NotFound(new { error = $"Unknown tool: {name}" });
    var a = new JsonObject();
    foreach (var (k, v) in req.Query) a[k] = v.ToString();
    try { return Results.Json(tool.Call(a), json); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (Exception e) { return Results.Json(new { error = e.Message }, statusCode: 500); }
});

// MCP streamable-HTTP transport: one JSON-RPC message per POST, plain JSON responses, no sessions.
app.MapPost("/mcp", async (HttpRequest req) =>
{
    using var body = new StreamReader(req.Body);
    var reply = Mcp.Handle(await body.ReadToEndAsync(), tools, json,
        $"Read-only, live metadata for volume {drive}:. Sizes are on-disk (allocated) bytes. Start with summary, then drill down with list.");
    return reply == null ? Results.Accepted() : Results.Json(reply);
});
app.MapGet("/mcp", () => Results.StatusCode(405)); // no server-initiated stream

Console.WriteLine($"Serving http://127.0.0.1:{port}/api and /mcp");
app.Run();
return 0;

/// JSON-RPC 2.0 handling for MCP. Returns null when no reply is due (notifications and responses).
static class Mcp
{
    static JsonObject Err(JsonNode? id, int code, string msg) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = msg },
    };

    static readonly string Version = typeof(Mcp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Mcp).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public static JsonObject? Handle(string body, Tool[] tools, JsonSerializerOptions json, string instructions)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { return Err(null, -32700, "Parse error"); }
        if (node is not JsonObject msg) return Err(null, -32600, "Invalid Request");

        var id = msg["id"]?.DeepClone();
        if (id is not (null or JsonValue)) return Err(null, -32600, "Invalid Request");
        if (!msg.ContainsKey("method")) return msg.ContainsKey("result") || msg.ContainsKey("error") ? null : Err(id, -32600, "Invalid Request");
        if (msg["method"] is not JsonValue mv || !mv.TryGetValue(out string? method)) return Err(id, -32600, "Invalid Request");
        if (!msg.ContainsKey("id")) return null; // notification: the id key is absent (id:null is still a request)

        var p = msg["params"];
        if (p is not (null or JsonObject)) return Err(id, -32602, "Invalid params");
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        switch (method)
        {
            case "initialize":
                reply["result"] = new JsonObject
                {
                    ["protocolVersion"] = p?["protocolVersion"] is JsonValue pv && pv.TryGetValue(out string? ver) ? ver : "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "ntfs-meta", ["version"] = Version },
                    ["instructions"] = instructions,
                };
                break;
            case "ping":
                reply["result"] = new JsonObject();
                break;
            case "tools/list":
                reply["result"] = new JsonObject { ["tools"] = new JsonArray(tools.Select(t => t.Schema()).ToArray()) };
                break;
            case "tools/call":
                if (p == null || p["arguments"] is not (null or JsonObject)) return Err(id, -32602, "Invalid params");
                string? name = p["name"] is JsonValue nv && nv.TryGetValue(out string? n) ? n : null;
                if (tools.FirstOrDefault(t => t.Name == name) is not { } tool) return Err(id, -32602, $"Unknown tool: {name}");
                string text;
                bool error = false;
                try { text = JsonSerializer.Serialize(tool.Call(p["arguments"] as JsonObject ?? []), json); }
                catch (Exception e) { (text, error) = (e.Message, true); }
                reply["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = error,
                };
                break;
            default:
                return Err(id, -32601, $"Method not found: {method}");
        }
        return reply;
    }
}

record Param(string Name, string Type, string Description, bool Required = false);

static class P
{
    public static readonly Param Path = new("path", "string", @"Directory to examine, e.g. 'C:\Users' (default: volume root)");
    public static readonly Param Limit = new("limit", "integer", "Maximum number of results");
}

record Tool(string Name, string Description, Param[] Params, Func<Args, object> Run)
{
    /// Validates required params, then runs. Throws ArgumentException on bad input.
    public object Call(JsonObject o)
    {
        var a = new Args(o);
        foreach (var p in Params)
            if (p.Required && a.StrOrNull(p.Name) == null) throw new ArgumentException($"{p.Name} is required");
        return Run(a);
    }

    public JsonObject Schema() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(Params.Select(p => KeyValuePair.Create(p.Name,
                (JsonNode?)new JsonObject { ["type"] = p.Type, ["description"] = p.Description }))),
            ["required"] = new JsonArray(Params.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        },
    };
}

/// Tool arguments from either MCP (typed JSON) or REST (query strings).
class Args(JsonObject o)
{
    JsonValue? Scalar(string k, string kind) => o[k] switch { null => null, JsonValue v => v, _ => throw new ArgumentException($"{k} must be {kind}") };
    public string? StrOrNull(string k) => Scalar(k, "a string")?.ToString() is { Length: > 0 } s ? s : null;
    public string Str(string k) => StrOrNull(k) ?? "";
    public int Int(string k, int d) => Scalar(k, "an integer") is { } v ? int.TryParse(v.ToString(), out int x) ? x : throw new ArgumentException($"{k} must be an integer") : d;
    public int Limit(int d) => Math.Clamp(Int("limit", d), 1, 10_000);
}

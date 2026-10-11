using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// ntfs-meta [drive] [--port N]  —  read-only NTFS metadata server (REST at /api, MCP at /mcp)
var drive = (args.FirstOrDefault(a => !a.StartsWith("--")) ?? "C").TrimEnd(':', '\\').ToUpperInvariant();
int port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).FirstOrDefault(7801);

Volume vol;
try { vol = new Volume(drive); }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Access denied: run as administrator."); return 1; }

var index = new Index(vol);
var sw = Stopwatch.StartNew();
index.Load();
Console.WriteLine($"Indexed {index.Count:N0} MFT records on {drive}: in {sw.Elapsed.TotalSeconds:0.0}s");
new Thread(index.Watch) { IsBackground = true }.Start();

var json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
Tool[] tools =
[
    new("summary", "Volume capacity, used/free space, file and directory counts.",
        [], a => index.Summary()),
    new("list", "Immediate children of a directory with recursive on-disk size, file count and newest modification inside, largest first. The main tool for drilling down.",
        [P.Path, P.Limit], a => index.List(a.Str("path"), a.Int("limit", 50))),
    new("largest", "Largest files under a path. Filter by extension and/or by files not modified in N days (cold-storage candidates). Also reports total size of all matches.",
        [P.Path, P.Limit, new("ext", "string", "Extension filter, e.g. 'mp4'"), new("older_than_days", "integer", "Only files last modified more than N days ago")],
        a => index.Largest(a.Str("path"), a.Int("limit", 50), a.StrOrNull("ext"), a.Int("older_than_days", 0))),
    new("extensions", "On-disk size and file count grouped by file extension under a path.",
        [P.Path, P.Limit], a => index.Extensions(a.Str("path"), a.Int("limit", 30))),
    new("duplicates", "Likely duplicate files under a path: same name and same size (contents are not compared). Sorted by reclaimable space.",
        [P.Path, P.Limit, new("min_mb", "integer", "Ignore files smaller than this (default 10)")],
        a => index.Duplicates(a.Str("path"), a.Int("min_mb", 10) * 1024L * 1024, a.Int("limit", 30))),
    new("find", "Files and directories whose name matches a wildcard pattern (* and ?, case-insensitive), largest first.",
        [new("pattern", "string", "Wildcard pattern, e.g. '*.iso' or 'node_modules'", true), P.Path, P.Limit],
        a => index.Find(a.Str("pattern"), a.Str("path"), a.Int("limit", 100))),
];

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

// Reject browser cross-origin requests (DNS-rebinding protection required by the MCP spec).
app.Use(async (ctx, next) =>
{
    string origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback)) ctx.Response.StatusCode = 403;
    else await next();
});

app.MapGet("/api", () => Results.Json(tools.Select(t => new { t.Name, t.Description, parameters = t.Params.Select(p => p.Name) })));
app.MapGet("/api/{name}", (string name, HttpRequest req) =>
{
    var tool = tools.FirstOrDefault(t => t.Name == name);
    if (tool == null) return Results.NotFound(new { error = $"Unknown tool: {name}" });
    var a = new JsonObject();
    foreach (var (k, v) in req.Query) a[k] = v.ToString();
    try { return Results.Json(tool.Run(new Args(a)), json); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

// MCP streamable-HTTP transport: one JSON-RPC message per POST, plain JSON responses, no sessions.
app.MapPost("/mcp", async (HttpRequest req) =>
{
    var msg = await JsonNode.ParseAsync(req.Body);
    var id = msg?["id"]?.DeepClone();
    if (id == null) return Results.Accepted(); // notification
    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
    var p = msg!["params"];
    switch ((string?)msg["method"])
    {
        case "initialize":
            reply["result"] = new JsonObject
            {
                ["protocolVersion"] = (string?)p?["protocolVersion"] ?? "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "ntfs-meta", ["version"] = "1.0.0" },
                ["instructions"] = $"Read-only, live metadata for volume {drive}:. Sizes are on-disk (allocated) bytes. Start with summary, then drill down with list.",
            };
            break;
        case "ping":
            reply["result"] = new JsonObject();
            break;
        case "tools/list":
            reply["result"] = new JsonObject { ["tools"] = new JsonArray(tools.Select(t => t.Schema()).ToArray()) };
            break;
        case "tools/call":
            var tool = tools.FirstOrDefault(t => t.Name == (string?)p?["name"]);
            string text;
            bool error = false;
            try { text = tool == null ? $"Unknown tool: {p?["name"]}" : JsonSerializer.Serialize(tool.Run(new Args(p?["arguments"] as JsonObject ?? [])), json); error = tool == null; }
            catch (ArgumentException e) { (text, error) = (e.Message, true); }
            reply["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = error,
            };
            break;
        default:
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {msg["method"]}" };
            break;
    }
    return Results.Json(reply);
});
app.MapGet("/mcp", () => Results.StatusCode(405)); // no server-initiated stream

Console.WriteLine($"Serving http://127.0.0.1:{port}/api and /mcp");
app.Run();
return 0;

record Param(string Name, string Type, string Description, bool Required = false);

static class P
{
    public static readonly Param Path = new("path", "string", @"Directory to examine, e.g. 'C:\Users' (default: volume root)");
    public static readonly Param Limit = new("limit", "integer", "Maximum number of results");
}

record Tool(string Name, string Description, Param[] Params, Func<Args, object> Run)
{
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
    public string? StrOrNull(string k) => o[k] is JsonValue v && v.ToString() is { Length: > 0 } s ? s : null;
    public string Str(string k) => StrOrNull(k) ?? "";
    public int Int(string k, int d) => o[k] is JsonValue v ? int.TryParse(v.ToString(), out int x) ? x : throw new ArgumentException($"{k} must be an integer") : d;
}

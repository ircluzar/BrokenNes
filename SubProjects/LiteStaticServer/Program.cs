// LiteStaticServer <publish dir> [port]
// Serves a Blazor WebAssembly publish folder (its wwwroot) for the BrokenNes Lite AOT build:
// correct MIME types (.wasm), the pre-compressed .br files when the browser accepts them, and an SPA
// fallback to index.html for routes like /nes. Default port 5090 (the "brokennes-lite-maxspeed" launch entry).
// `dotnet run` starts the app in this project's folder, so a relative <publish dir> resolves from here
// (the launch entry passes ../../WebLite/bin/Release-Publish).
using Microsoft.AspNetCore.StaticFiles;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
if (Directory.Exists(Path.Combine(root, "wwwroot"))) root = Path.Combine(root, "wwwroot");
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5090;

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var types = new FileExtensionContentTypeProvider();
types.Mappings[".wasm"] = "application/wasm";
foreach (var ext in new[] { ".dat", ".blat", ".dll", ".pdb", ".webcil", ".nes", ".sfc", ".smc", ".gb", ".gbc", ".zip", ".onnx", ".sf2" })
    types.Mappings[ext] = "application/octet-stream";

app.Run(async ctx =>
{
    string rel = Uri.UnescapeDataString(ctx.Request.Path.Value ?? "/").TrimStart('/');
    if (rel.Length == 0) rel = "index.html";
    string full = Path.GetFullPath(Path.Combine(root, rel));
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 404; return; }
    if (!File.Exists(full))
    {
        if (Path.HasExtension(rel)) { ctx.Response.StatusCode = 404; return; }
        full = Path.Combine(root, "index.html");   // SPA route
    }
    ctx.Response.ContentType = types.TryGetContentType(full, out var ct) ? ct : "application/octet-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    if (ctx.Request.Headers.AcceptEncoding.ToString().Contains("br") && File.Exists(full + ".br"))
    {
        ctx.Response.Headers.ContentEncoding = "br";
        ctx.Response.Headers.Vary = "Accept-Encoding";
        await ctx.Response.SendFileAsync(full + ".br");
        return;
    }
    await ctx.Response.SendFileAsync(full);
});

Console.WriteLine($"LiteStaticServer: {root} on http://localhost:{port}");
app.Run();

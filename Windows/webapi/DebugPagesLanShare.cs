using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// The read-only debug pages on the LAN (BrokenDev D8 case 2, docs/projects/brokendev-debugging-plan.md "D8 spec"): Emulator > Debug Windows >
    /// Share Read-Only Pages on the LAN. Off by default; switched on it opens a SECOND listener on the machine's LAN address and a free port that
    /// answers only the two pages (/debug/debugger.html, /debug/viewers.html, embedded in the app) and the read-only GET debug routes, which it
    /// forwards to the app's own loopback API. Every request must carry the token made at switch-on (?t= or the X-BrokenNes-Token header):
    /// missing or wrong is 401; any method but GET is 405; any other path is 404. So control routes, pokes, input, saves and progression cannot
    /// be reached from the LAN, whatever enableDebuggerControl says. Switched off, the listener closes at once; nothing about it is saved.
    /// It never touches firewall settings (Windows asks its own question the first time).
    /// </summary>
    public sealed class DebugPagesLanShare : IDisposable
    {
        /// <summary>Tests only: bind this address instead of the LAN one (a loopback alias such as 127.0.0.2, so no firewall prompt is raised).</summary>
        public const string AddressOverrideVariable = "BROKENNES_DEBUG_LAN_ADDRESS";

        /// <summary>The pages, by the path the LAN serves them at, and the embedded resource that holds each.</summary>
        private static readonly Dictionary<string, string> Pages = new(StringComparer.Ordinal)
        {
            ["/debug/debugger.html"] = "debugpages/debugger.html",
            ["/debug/viewers.html"] = "debugpages/viewers.html",
        };

        /// <summary>The read-only GET routes the pages read (D8 spec), forwarded to the loopback API as they are.</summary>
        public static readonly IReadOnlyList<string> Routes = new[]
        {
            "/api/debug/state", "/api/debug/registers", "/api/debug/disasm", "/api/debug/callstack", "/api/debug/memory",
            "/api/debug/palettes", "/api/debug/tiles", "/api/debug/map", "/api/debug/sprites", "/api/debug/sound", "/api/debug/events",
            "/api/memory/domains", "/api/memory/peek-range",
        };

        private static readonly HashSet<string> RouteSet = new(Routes, StringComparer.Ordinal);

        private readonly Func<string?> _loopbackBaseUrl;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private WebApplication? _app;
        private byte[] _token = Array.Empty<byte>();

        public DebugPagesLanShare(Func<string?> loopbackBaseUrl) { _loopbackBaseUrl = loopbackBaseUrl; }

        public bool IsOn => _app != null;
        public string? Address { get; private set; }
        public int Port { get; private set; }
        public string? Token { get; private set; }
        /// <summary>The URL to type on the phone (with the token), null while off.</summary>
        public string? Url => IsOn ? $"http://{Address}:{Port}/debug/viewers.html?t={Token}" : null;
        public string? DebuggerUrl => IsOn ? $"http://{Address}:{Port}/debug/debugger.html?t={Token}" : null;

        /// <summary>The machine's LAN IPv4 address: an interface that is up, not loopback or a tunnel, with a gateway; null when there is none.</summary>
        public static IPAddress? FindLanAddress(out string why)
        {
            var env = Environment.GetEnvironmentVariable(AddressOverrideVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (IPAddress.TryParse(env.Trim(), out var forced)) { why = $"{AddressOverrideVariable}={forced}"; return forced; }
                why = $"{AddressOverrideVariable} is not an IP address: {env}"; return null;
            }
            IPAddress? fallback = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;   // link-local: no DHCP answer
                        if (gateway) { why = $"{ni.Name}"; return a; }
                        fallback ??= a;
                    }
                }
            }
            catch (NetworkInformationException ex) { why = "the network interfaces could not be read: " + ex.Message; return null; }
            why = fallback != null ? "an interface without a gateway" : "no network: this machine has no LAN IPv4 address";
            return fallback;
        }

        /// <summary>Switches the share on: a new token, a free port on the LAN address. Returns null on success, else why it could not.</summary>
        public async Task<string?> StartAsync()
        {
            if (IsOn) return null;
            var address = FindLanAddress(out var why);
            if (address == null) return why;
            var token = new byte[18];
            RandomNumberGenerator.Fill(token);
            string tokenText = Convert.ToHexString(token).ToLowerInvariant();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppDomain.CurrentDomain.BaseDirectory });
            builder.WebHost.ConfigureKestrel(o => o.Listen(address, 0));
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var app = builder.Build();
            _token = Encoding.ASCII.GetBytes(tokenText);
            app.Run(HandleAsync);
            try { await app.StartAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                _token = Array.Empty<byte>();
                return $"could not listen on {address}: {ex.Message}";
            }
            int port = 0;
            var bound = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
            foreach (var u in bound ?? Array.Empty<string>())
                if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) { port = uri.Port; break; }
            _app = app; Address = address.ToString(); Port = port; Token = tokenText;
            Console.WriteLine($"[lan share] on: http://{Address}:{Port}/debug/ (token required; read-only pages and GET routes only)");
            return null;
        }

        /// <summary>Switches the share off: the listener closes now and the token is forgotten.</summary>
        public async Task StopAsync()
        {
            var app = _app;
            _app = null; _token = Array.Empty<byte>(); Token = null; Address = null; Port = 0;
            if (app == null) return;
            try { await app.StopAsync().ConfigureAwait(false); } catch { }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
            Console.WriteLine("[lan share] off: the listener is closed");
        }

        private bool TokenOk(HttpContext ctx)
        {
            string? given = ctx.Request.Query["t"].FirstOrDefault();
            if (string.IsNullOrEmpty(given)) given = ctx.Request.Headers["X-BrokenNes-Token"].FirstOrDefault();
            var want = _token;
            if (string.IsNullOrEmpty(given) || want.Length == 0) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), want);
        }

        private async Task HandleAsync(HttpContext ctx)
        {
            var res = ctx.Response;
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            if (!TokenOk(ctx)) { await Plain(res, 401, "a token is required: open the URL BrokenNes shows (Emulator > Debug Windows > Share Read-Only Pages on the LAN)"); return; }
            if (!HttpMethods.IsGet(ctx.Request.Method)) { res.Headers["Allow"] = "GET"; await Plain(res, 405, "read only: GET is the only method here"); return; }
            string path = ctx.Request.Path.Value ?? "";
            if (Pages.TryGetValue(path, out var resource))
            {
                using var s = typeof(DebugPagesLanShare).Assembly.GetManifestResourceStream(resource);
                if (s == null) { await Plain(res, 404, "the page is not in this build"); return; }
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                await s.CopyToAsync(res.Body);
                return;
            }
            if (!RouteSet.Contains(path)) { await Plain(res, 404, "not shared on the LAN: only the read-only debug pages and their GET routes are"); return; }
            var baseUrl = _loopbackBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) { await Plain(res, 503, "the app's API is not listening"); return; }
            var query = string.Join("&", ctx.Request.Query.Where(kv => kv.Key != "t")
                .SelectMany(kv => kv.Value.Select(v => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(v ?? ""))));
            try
            {
                using var r = await _http.GetAsync(baseUrl + path + (query.Length > 0 ? "?" + query : ""), ctx.RequestAborted);
                res.StatusCode = (int)r.StatusCode;
                res.ContentType = r.Content.Headers.ContentType?.ToString() ?? "application/json";
                await r.Content.CopyToAsync(res.Body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Plain(res, 502, "the app's API did not answer: " + ex.Message);
            }
        }

        private static Task Plain(HttpResponse res, int status, string text)
        {
            res.StatusCode = status;
            res.ContentType = "text/plain; charset=utf-8";
            return res.WriteAsync(text);
        }

        public void Dispose()
        {
            var app = _app; _app = null; _token = Array.Empty<byte>(); Token = null;
            try { app?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { (app as IDisposable)?.Dispose(); } catch { }
            _http.Dispose();
        }
    }
}

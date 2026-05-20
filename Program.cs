using System.Net;
using System.Net.Sockets;
using ImageDownloader.Hubs;
using ImageDownloader.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Allow config from environment variables (e.g. PORT on Railway/Render/Fly)
builder.Configuration.AddEnvironmentVariables();

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(opts =>
{
    opts.MaximumReceiveMessageSize = 512 * 1024;               // 512 KB
    opts.ClientTimeoutInterval     = TimeSpan.FromMinutes(30); // survive long crawl jobs
    opts.KeepAliveInterval         = TimeSpan.FromSeconds(15);
    opts.EnableDetailedErrors      = !builder.Environment.IsProduction();
});

builder.Services.AddSingleton<DownloadJobManager>();

builder.Services.AddCors(opts =>
    opts.AddPolicy("SignalRPolicy", p =>
        p.SetIsOriginAllowed(_ => true)
         .AllowAnyMethod()
         .AllowAnyHeader()
         .AllowCredentials()));

// ── Port resolution ───────────────────────────────────────────────────────────
// Priority:
//   1. PORT env var  — set automatically by Render / Railway / Fly / Cloud Run
//   2. ASPNETCORE_URLS env var — standard ASP.NET Core override
//   3. Preferred port (default 8080) — used locally or when nothing is set
//   4. Auto-assigned free port   — fallback if preferred port is already in use
//
// On hosted platforms (Render, Railway, Fly.io, etc.) the PORT env var is
// always set and the process owns that port exclusively, so the fallback path
// is never reached.  The fallback only fires locally when e.g. another copy
// of the app (or any other process) is already bound to 8080.

// If ASPNETCORE_URLS is already set, honour it completely and let Kestrel do
// its normal thing — don't override it.
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    int port = ResolvePort(builder.Configuration);
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
    Console.WriteLine($"[Startup] Listening on http://0.0.0.0:{port}");
}

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("SignalRPolicy");

// ── SignalR hub ───────────────────────────────────────────────────────────────
app.MapHub<DownloadHub>("/hubs/download");

// ── REST API ──────────────────────────────────────────────────────────────────

// Job status (polling fallback)
app.MapGet("/api/jobs/{jobId}", (string jobId, DownloadJobManager mgr) =>
{
    var status = mgr.GetStatus(jobId);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

// Cancel job via REST
app.MapDelete("/api/jobs/{jobId}", (string jobId, DownloadJobManager mgr, HttpContext ctx) =>
{
    mgr.CancelJob(jobId, ctx.Connection.Id ?? "");
    return Results.Ok();
});

// Download ZIP — streams to client, then deletes only that user's job folder
app.MapGet("/api/jobs/{jobId}/zip", async (string jobId, DownloadJobManager mgr, HttpContext ctx) =>
{
    var (stream, zipName) = mgr.GetZipStream(jobId);
    if (stream is null)
        return Results.NotFound(new { error = "Job not found or no images saved." });

    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";

    bool streamedOk = false;
    try
    {
        await using (stream)
            await stream.CopyToAsync(ctx.Response.Body);
        streamedOk = true;
    }
    catch
    {
        // Client disconnected mid-download — don't delete so they can retry
    }

    if (streamedOk)
        mgr.DeleteJobData(jobId);

    return Results.Empty;
});

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>
/// Returns the port the server should bind to.
///
/// 1. If PORT env var is set (hosted platform), use it — no fallback.
/// 2. Otherwise try the preferred port (default 8080, or override via
///    appsettings / command line).
/// 3. If the preferred port is already in use locally, bind to OS port 0 to
///    get a free one and print it so the developer can open the browser.
/// </summary>
static int ResolvePort(IConfiguration config)
{
    // Hosted platform: trust PORT completely
    string? envPort = config["PORT"];
    if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out int hosted))
        return hosted;

    // Preferred local port
    int preferred = 8080;
    string? cfgPort = config["AppPort"];          // optional override in appsettings
    if (!string.IsNullOrWhiteSpace(cfgPort) && int.TryParse(cfgPort, out int p))
        preferred = p;

    if (IsPortFree(preferred))
        return preferred;

    // Preferred port is taken — find a free one
    int free = GetFreePort();
    Console.WriteLine($"[Startup] Port {preferred} is in use — using port {free} instead.");
    Console.WriteLine($"[Startup] Open http://localhost:{free} in your browser.");
    return free;
}

/// <summary>Returns true if no process is currently listening on the port.</summary>
static bool IsPortFree(int port)
{
    try
    {
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

/// <summary>
/// Asks the OS for an ephemeral free port by binding to port 0,
/// reading back the assigned port, then immediately releasing it.
/// There is a tiny TOCTOU window between release and Kestrel's bind,
/// but in practice it's never an issue on a developer machine.
/// </summary>
static int GetFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

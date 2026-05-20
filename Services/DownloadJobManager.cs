using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using ImageDownloader.Hubs;
using ImageDownloader.Models;
using Microsoft.AspNetCore.SignalR;

namespace ImageDownloader.Services;

/// <summary>
/// Manages the lifecycle of all concurrent download jobs.
///
/// v12 changes:
///   • BuildHttpClient now installs a custom ServerCertificateCustomValidationCallback
///     that accepts all TLS certificates — expired, self-signed, mismatched CN, etc.
///     This ensures every HTTPS site is reachable regardless of certificate state.
///   • SocketsHttpHandler.SslOptions added to disable peer verification at the
///     socket level as well (belt-and-suspenders for .NET 8+).
///   • MaxConnectionsPerServer raised to 20 (from default 2) to support high
///     concurrency against CDN-backed sites without connection queuing.
///
/// v9 history:
///   • Creates a BrowserFingerprint per job.
///   • Passes the fingerprint to PageFetcher and ImageDownloaderService.
///   • Each user connection gets its own isolated job, HttpClient, stats, CTS,
///     and output folder.
///   • Old completed jobs pruned after 1 hour.
/// </summary>
public sealed class DownloadJobManager : IDisposable
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly IHubContext<DownloadHub>                _hub;
    private readonly string                                  _outputRoot;
    private readonly Timer                                   _cleanupTimer;

    private static readonly TimeSpan JobRetention = TimeSpan.FromHours(1);

    public DownloadJobManager(IHubContext<DownloadHub> hub, IConfiguration config)
    {
        _hub = hub;

        _outputRoot = config["OutputRoot"] is { Length: > 0 } root
                      ? root
                      : Path.Combine(Path.GetTempPath(), "imgdl_downloads");
        Directory.CreateDirectory(_outputRoot);

        _cleanupTimer = new Timer(PurgeExpiredJobs, null,
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    public string StartJob(string connectionId, StartJobRequest req)
    {
        var jobId  = Guid.NewGuid().ToString("N");
        var stats  = new DownloadStats();
        var cts    = new CancellationTokenSource();

        string outputFolder = Path.Combine(_outputRoot, jobId);
        Directory.CreateDirectory(outputFolder);

        string zipName = DeriveZipName(req.FolderName, req.Url, jobId);

        var state = new JobState
        {
            JobId        = jobId,
            ConnectionId = connectionId,
            Status       = JobStatus.Running,
            Stats        = stats,
            Cts          = cts,
            OutputFolder = outputFolder,
            ZipName      = zipName,
        };

        _jobs[jobId] = state;

        var reporter   = new SignalRProgressReporter(_hub, connectionId, stats);
        var http       = BuildHttpClient();

        // ── One fingerprint per job ───────────────────────────────────────────
        // Randomises Chrome version, OS, Accept-Language, timezone, viewport,
        // and two spoofed public IPv4 addresses. All services in this job share
        // the same fingerprint so page + image requests look like one consistent
        // browser session.
        var fingerprint = new BrowserFingerprint();
        reporter.Info($"  🆔 Session fingerprint: {fingerprint.UserAgent.Split('(')[1].TrimEnd(')')} | IP {fingerprint.ForwardedIp}");

        var fetcher    = new PageFetcher(http, reporter, fingerprint);
        var downloader = new ImageDownloaderService(http, stats, reporter, req, fingerprint);
        var crawler    = new Crawler(fetcher, downloader, stats, reporter, req, outputFolder);

        _ = Task.Run(async () =>
        {
            try
            {
                await crawler.StartAsync(req.Url, cts.Token);
                state.Status = cts.IsCancellationRequested
                    ? JobStatus.Cancelled
                    : JobStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                state.Status = JobStatus.Cancelled;
            }
            catch (Exception ex)
            {
                state.Status = JobStatus.Failed;
                reporter.Error($"Fatal: {ex.Message}");
            }
            finally
            {
                state.EndTime = DateTime.UtcNow;
                await fetcher.DisposeAsync();
                downloader.Dispose();
                http.Dispose();
                cts.Dispose();

                await _hub.Clients.Client(connectionId).SendAsync("jobFinished", new
                {
                    jobId,
                    status      = state.Status.ToString().ToLowerInvariant(),
                    pages       = stats.Pages,
                    saved       = stats.Saved,
                    skipped     = stats.Skipped,
                    failed      = stats.Failed,
                    canDownload = stats.Saved > 0,
                    zipName
                });
            }
        });

        return jobId;
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public void CancelJob(string jobId, string connectionId)
    {
        if (_jobs.TryGetValue(jobId, out var state)
            && state.ConnectionId == connectionId
            && state.Status == JobStatus.Running)
        {
            state.Cts.Cancel();
        }
    }

    public void CancelJobsForConnection(string connectionId)
    {
        foreach (var s in _jobs.Values
                     .Where(j => j.ConnectionId == connectionId
                              && j.Status       == JobStatus.Running))
        {
            s.Cts.Cancel();
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public JobStatusDto? GetStatus(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var s)) return null;
        return new JobStatusDto
        {
            JobId       = jobId,
            Status      = s.Status.ToString(),
            Pages       = s.Stats.Pages,
            Saved       = s.Stats.Saved,
            Skipped     = s.Stats.Skipped,
            Failed      = s.Stats.Failed,
            CanDownload = s.Stats.Saved > 0 && s.Status != JobStatus.Running
        };
    }

    // ── ZIP download ──────────────────────────────────────────────────────────

    public (Stream? stream, string zipName) GetZipStream(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var s))  return (null, "");
        if (!Directory.Exists(s.OutputFolder))     return (null, "");

        bool hasFiles = Directory
            .EnumerateFiles(s.OutputFolder, "*", SearchOption.AllDirectories)
            .Any();
        if (!hasFiles) return (null, "");

        string tempPath = Path.Combine(Path.GetTempPath(), $"imgdl_{jobId}.zip");
        if (File.Exists(tempPath)) File.Delete(tempPath);

        ZipFile.CreateFromDirectory(
            s.OutputFolder, tempPath,
            CompressionLevel.Fastest,
            includeBaseDirectory: false);

        var fs = new FileStream(
            tempPath, FileMode.Open, FileAccess.Read,
            FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        return (fs, s.ZipName);
    }

    // ── Explicit cleanup ──────────────────────────────────────────────────────

    public void DeleteJobData(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out JobState? state)) return;
        TryDeleteFolder(state.OutputFolder);
    }

    // ── Periodic purge ────────────────────────────────────────────────────────

    private void PurgeExpiredJobs(object? _)
    {
        var cutoff = DateTime.UtcNow - JobRetention;
        foreach (var kvp in _jobs)
        {
            var state = kvp.Value;
            if (state.Status != JobStatus.Running
                && state.EndTime.HasValue
                && state.EndTime.Value < cutoff)
            {
                if (_jobs.TryRemove(kvp.Key, out JobState? _))
                    TryDeleteFolder(state.OutputFolder);
            }
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── HttpClient ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a per-job HttpClient with SocketsHttpHandler.
    ///
    /// v12: SSL validation is disabled so any HTTPS site is reachable —
    /// expired certs, self-signed certs, mismatched CNs, and non-public
    /// CAs are all accepted.  This is intentional for a scraping tool where
    /// the operator controls what URLs are fetched.
    ///
    /// DefaultRequestHeaders intentionally MINIMAL — all per-request headers
    /// (UA, IP, Sec-CH-*, etc.) are set by PageFetcher and ImageDownloaderService
    /// on each HttpRequestMessage. Setting them both here and per-request creates
    /// duplicates on Linux SocketsHttpHandler, causing 400s or silent bot blocks.
    /// </summary>
    private static HttpClient BuildHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect              = true,
            MaxAutomaticRedirections       = 15,
            MaxConnectionsPerServer        = 20,  // v12: higher for CDN concurrency
            UseCookies                     = true,
            CookieContainer                = new CookieContainer(),
            AutomaticDecompression         = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime       = TimeSpan.FromMinutes(5),
            ConnectTimeout                 = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout           = TimeSpan.FromSeconds(20),
            KeepAlivePingDelay             = TimeSpan.FromSeconds(30),

            // v12: Accept any TLS certificate — expired, self-signed,
            // mismatched CN — so every HTTPS site is reachable.
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)  // v12: raised for slow sites
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DeriveZipName(string? folderName, string url, string jobId)
    {
        if (!string.IsNullOrWhiteSpace(folderName))
            return FileHelper.Sanitize(folderName.Trim()) + ".zip";

        try
        {
            string host = new Uri(url).Host.Replace("www.", "");
            return FileHelper.Sanitize(host) + ".zip";
        }
        catch
        {
            return $"images-{jobId[..8]}.zip";
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}

using ImageDownloader.Hubs;
using ImageDownloader.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ImageDownloader.Services;

/// <summary>
/// Implements IProgressReporter by pushing SignalR messages to the
/// specific browser connection that owns this job.
/// Each job gets its own reporter instance — fully isolated per user.
/// </summary>
public sealed class SignalRProgressReporter : IProgressReporter
{
    private readonly IHubContext<DownloadHub> _hub;
    private readonly string                  _connectionId;
    private readonly DownloadStats           _stats;

    public SignalRProgressReporter(
        IHubContext<DownloadHub> hub,
        string connectionId,
        DownloadStats stats)
    {
        _hub          = hub;
        _connectionId = connectionId;
        _stats        = stats;
    }

    // ── Core events ───────────────────────────────────────────────────────────

    public void PageHeader(int pageNum, int depth, string url)
    {
        Push("pageVisited", new { pageNum, depth, url });
        PushStats();
    }

    public void ImageSaved(string filename, long bytes)
    {
        Push("imageSaved", new
        {
            filename,
            bytes,
            size = FileHelper.FormatSize(bytes)
        });
        PushStats();
    }

    public void ImageSkipped(string filename)
    {
        Push("imageSkipped", new { filename });
        PushStats();
    }

    public void ImageRetry(string filename, int attempt, int max) =>
        Push("imageRetry", new { filename, attempt, max });

    public void ImageFailed(string filename, string reason)
    {
        Push("imageFailed", new { filename, reason });
        PushStats();
    }

    // ── Log lines ─────────────────────────────────────────────────────────────

    public void Info(string text)    => Log("info",    text);
    public void Dim(string text)     => Log("dim",     text);
    public void Warning(string text) => Log("warning", text);
    public void Error(string text)   => Log("error",   text);

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Log(string level, string text) =>
        Push("log", new { level, text });

    private void PushStats() =>
        Push("statsUpdate", new
        {
            Pages   = _stats.Pages,
            Saved   = _stats.Saved,
            Skipped = _stats.Skipped,
            Failed  = _stats.Failed
        });

    /// <summary>
    /// Fire-and-forget SignalR send. Uses Task.Run to avoid blocking the
    /// crawler thread on network I/O. Errors are swallowed — occasional
    /// progress loss is acceptable vs crashing the background crawler.
    /// </summary>
    private void Push(string method, object payload) =>
        _ = _hub.Clients.Client(_connectionId)
                .SendAsync(method, payload)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        // Connection dropped — not fatal for the crawl itself
                        _ = t.Exception; // observe the exception
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
}

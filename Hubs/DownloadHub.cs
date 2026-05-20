using ImageDownloader.Models;
using ImageDownloader.Services;
using Microsoft.AspNetCore.SignalR;

namespace ImageDownloader.Hubs;

/// <summary>
/// SignalR hub that brokers communication between browser clients
/// and the download job manager. Each connection is fully isolated —
/// jobs from one user never bleed into another user's session.
/// </summary>
public sealed class DownloadHub : Hub
{
    private readonly DownloadJobManager _manager;

    public DownloadHub(DownloadJobManager manager) => _manager = manager;

    // ── Inbound calls (browser → server) ─────────────────────────────────────

    /// <summary>Start a new download job. Emits "jobStarted" with the jobId.</summary>
    public async Task StartJob(StartJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            await Clients.Caller.SendAsync("log", new { level = "error", text = "URL is required." });
            return;
        }

        var jobId = _manager.StartJob(Context.ConnectionId, request);
        await Clients.Caller.SendAsync("jobStarted", jobId);
    }

    /// <summary>Cancel a running job owned by this connection.</summary>
    public Task CancelJob(string jobId)
    {
        _manager.CancelJob(jobId, Context.ConnectionId);
        return Task.CompletedTask;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Automatically cancel running jobs when the user closes the tab
        _manager.CancelJobsForConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

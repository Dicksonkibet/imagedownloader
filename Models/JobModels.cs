using ImageDownloader.Services;

namespace ImageDownloader.Models;

// ── Request sent from the browser via SignalR ─────────────────────────────────
// Must be a class (not positional record) so System.Text.Json can deserialize
// it from the SignalR payload without a [JsonConstructor].
public class StartJobRequest
{
    public string  Url                    { get; set; } = string.Empty;
    public string? FolderName             { get; set; }
    public int     MaxDepth               { get; set; } = 2;
    public int     RequestDelayMs         { get; set; } = 1200;
    public int     MaxConcurrentDownloads { get; set; } = 4;
    public int     MaxRetries             { get; set; } = 3;
    public string[]? AllowedExtensions    { get; set; }
    public int     MinFileSizeBytes       { get; set; } = 2048;

    public string[] EffectiveExtensions =>
        AllowedExtensions is { Length: > 0 }
            ? AllowedExtensions
            : [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp"];
}

// ── Internal job state ────────────────────────────────────────────────────────
public enum JobStatus { Running, Completed, Cancelled, Failed }

public class JobState
{
    public required string     JobId        { get; init; }
    public required string     ConnectionId { get; init; }
    public JobStatus           Status       { get; set;  } = JobStatus.Running;
    public required DownloadStats Stats     { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required string     OutputFolder { get; init; }
    public required string     ZipName      { get; init; }
    public DateTime            StartTime    { get; init; } = DateTime.UtcNow;
    public DateTime?           EndTime      { get; set;  }
}

// ── DTO returned to the REST status endpoint ──────────────────────────────────
public class JobStatusDto
{
    public required string JobId       { get; init; }
    public required string Status      { get; init; }
    public int    Pages                { get; init; }
    public int    Saved                { get; init; }
    public int    Skipped              { get; init; }
    public int    Failed               { get; init; }
    public bool   CanDownload          { get; init; }
}

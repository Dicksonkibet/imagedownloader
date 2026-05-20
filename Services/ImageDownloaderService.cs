using ImageDownloader.Interfaces;
using ImageDownloader.Models;

namespace ImageDownloader.Services;

/// <summary>
/// Downloads individual images to disk with retry, concurrency throttle,
/// and minimum-size filter. One instance per job — dispose after job ends.
///
/// v12 changes:
///   • Per-request IP rotation: FetchImageBytesAsync now calls
///     BrowserFingerprint.GenerateFreshIpPair() for every image fetch so
///     every download request carries a distinct spoofed source IP.
///
/// v11 changes:
///   • Content-Type sniffing: even if a URL has no image extension, we save
///     the file when the server responds with image/* Content-Type. This is
///     critical for CDN URLs (Cloudinary, imgix, Unsplash, Shopify, etc.)
///     that serve images without file extensions.
///   • Extension inference from Content-Type when saving extensionless images.
///   • FileHelper.BuildUniqueFilename now receives an optional override
///     extension so CDN files get the right suffix (.jpg, .webp, etc.).
///   • HTTP 429 / 503 rate-limit handling (unchanged from v10).
///   • BrowserFingerprint shared headers (unchanged from v10).
/// </summary>
public sealed class ImageDownloaderService : IDisposable
{
    private readonly HttpClient         _http;
    private readonly DownloadStats      _stats;
    private readonly IProgressReporter  _reporter;
    private readonly SemaphoreSlim      _throttle;
    private readonly StartJobRequest    _config;
    private readonly BrowserFingerprint _fp;

    // HTTP status codes that should trigger a backoff-retry rather than fail immediately
    private static readonly HashSet<int> BackoffStatuses = [429, 503];

    // Maps Content-Type → file extension (for extensionless CDN responses)
    private static readonly Dictionary<string, string> ContentTypeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"]    = ".jpg",
            ["image/jpg"]     = ".jpg",
            ["image/png"]     = ".png",
            ["image/webp"]    = ".webp",
            ["image/gif"]     = ".gif",
            ["image/avif"]    = ".avif",
            ["image/bmp"]     = ".bmp",
            ["image/tiff"]    = ".tif",
            ["image/svg+xml"] = ".svg",
            ["image/x-icon"]  = ".ico",
        };

    public ImageDownloaderService(
        HttpClient http, DownloadStats stats,
        IProgressReporter reporter, StartJobRequest config,
        BrowserFingerprint fp)
    {
        _http     = http;
        _stats    = stats;
        _reporter = reporter;
        _config   = config;
        _fp       = fp;
        _throttle = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentDownloads));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task DownloadAllAsync(
        IReadOnlyList<string> imageUrls,
        string targetFolder,
        string pageUrl,
        CancellationToken ct)
    {
        var tasks = imageUrls.Select(url =>
            DownloadOneThrottledAsync(url, targetFolder, pageUrl, ct));
        await Task.WhenAll(tasks);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task DownloadOneThrottledAsync(
        string imageUrl, string folder, string pageUrl, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try   { await DownloadWithRetryAsync(imageUrl, folder, pageUrl, ct); }
        finally { _throttle.Release(); }
    }

    private async Task DownloadWithRetryAsync(
        string imageUrl, string folder, string pageUrl, CancellationToken ct)
    {
        // Build a tentative filename; may be updated after we know Content-Type
        string tentativeName = FileHelper.BuildUniqueFilename(
            imageUrl, folder, _config.EffectiveExtensions);

        for (int attempt = 1; attempt <= _config.MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var (bytes, inferredExt, shouldRetry, retryDelayMs) =
                    await FetchImageBytesAsync(imageUrl, pageUrl, ct);

                if (shouldRetry && attempt < _config.MaxRetries)
                {
                    _reporter.ImageRetry(tentativeName, attempt, _config.MaxRetries);
                    await SafeDelay(retryDelayMs, ct);
                    continue;
                }

                if (bytes.Length == 0)
                {
                    _reporter.ImageFailed(tentativeName, "empty response (possibly rate-limited)");
                    _stats.IncrementFailed();
                    return;
                }

                if (bytes.Length < _config.MinFileSizeBytes)
                {
                    _reporter.Dim($"    -  Ignored  {tentativeName}  (too small: {bytes.Length} B)");
                    return;
                }

                // Resolve final filename — if we got a Content-Type ext, use it
                string filename = FileHelper.BuildUniqueFilename(
                    imageUrl, folder, _config.EffectiveExtensions, inferredExt);
                string destPath = Path.Combine(folder, filename);

                if (File.Exists(destPath))
                {
                    _reporter.ImageSkipped(filename);
                    _stats.IncrementSkipped();
                    return;
                }

                FileHelper.EnsureDirectory(folder);
                await File.WriteAllBytesAsync(destPath, bytes, ct);

                _reporter.ImageSaved(filename, bytes.Length);
                _stats.IncrementSaved();
                return;
            }
            catch (TaskCanceledException) { return; }
            catch (Exception ex)
            {
                if (attempt < _config.MaxRetries)
                {
                    _reporter.ImageRetry(tentativeName, attempt, _config.MaxRetries);
                    int delay = BrowserFingerprint.JitteredDelay(_config.RequestDelayMs * attempt);
                    await SafeDelay(delay, ct);
                }
                else
                {
                    _reporter.ImageFailed(tentativeName, ex.Message);
                    _stats.IncrementFailed();
                }
            }
        }
    }

    /// <summary>
    /// Fetches image bytes using a fully randomised browser request.
    /// Returns (bytes, inferredExtension, shouldRetry, retryDelayMs).
    ///
    /// inferredExtension is non-null when the URL has no image extension but the
    /// server returned an image/* Content-Type — e.g. ".jpg" for image/jpeg.
    /// </summary>
    private async Task<(byte[] Bytes, string? InferredExt, bool ShouldRetry, int RetryDelayMs)>
        FetchImageBytesAsync(string imageUrl, string pageUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);

        // ── Randomised browser identity (shared fingerprint) ──────────────────
        req.Headers.TryAddWithoutValidation("User-Agent",         _fp.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language",    _fp.AcceptLanguage);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA",          _fp.SecChUa);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile",   _fp.SecChUaMobile);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", _fp.SecChUaPlatform);

        // ── Standard image sub-resource headers ───────────────────────────────
        req.Headers.TryAddWithoutValidation("Accept",
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        req.Headers.TryAddWithoutValidation("Cache-Control",   "no-cache");
        req.Headers.TryAddWithoutValidation("Pragma",          "no-cache");
        req.Headers.TryAddWithoutValidation("DNT",             "1");
        req.Headers.TryAddWithoutValidation("Connection",      "keep-alive");

        // ── Sec-Fetch hints: image sub-resource ───────────────────────────────
        string secFetchSite = DetermineSecFetchSite(imageUrl, pageUrl);
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", secFetchSite);

        // ── Referer: the actual page containing this image ────────────────────
        string referer = !string.IsNullOrWhiteSpace(pageUrl)
            ? pageUrl
            : UrlHelper.GetRoot(imageUrl) + "/";
        req.Headers.TryAddWithoutValidation("Referer", referer);

        // ── Spoofed originating-IP headers (v12: fresh pair per request) ──────
        var (ip1, ip2) = BrowserFingerprint.GenerateFreshIpPair();
        req.Headers.TryAddWithoutValidation("X-Forwarded-For",    $"{ip1}, {ip2}");
        req.Headers.TryAddWithoutValidation("X-Real-IP",           ip1);
        req.Headers.TryAddWithoutValidation("CF-Connecting-IP",    ip1);
        req.Headers.TryAddWithoutValidation("True-Client-IP",      ip1);
        req.Headers.TryAddWithoutValidation("X-Client-IP",         ip1);
        req.Headers.TryAddWithoutValidation("X-Cluster-Client-IP", ip1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var res = await _http.SendAsync(
            req, HttpCompletionOption.ResponseContentRead, cts.Token);

        int code = (int)res.StatusCode;

        if (BackoffStatuses.Contains(code))
        {
            int retryMs = 0;
            if (res.Headers.TryGetValues("Retry-After", out var vals) &&
                int.TryParse(vals.First(), out int retryAfterSec))
                retryMs = retryAfterSec * 1000;
            else
                retryMs = BrowserFingerprint.JitteredDelay(3000);

            return ([], null, true, retryMs);
        }

        res.EnsureSuccessStatusCode();

        // ── Content-Type sniffing ─────────────────────────────────────────────
        // If the URL has no image extension, check the response Content-Type.
        // This makes CDN images (Cloudinary, imgix, Unsplash, etc.) work
        // even when the URL path has no extension.
        string? inferredExt = null;
        string? contentType = res.Content.Headers.ContentType?.MediaType;

        if (contentType != null)
        {
            // Reject non-image responses (e.g. HTML error pages, JSON errors)
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _reporter.Dim($"    ⚠  Skipped non-image Content-Type: {contentType}");
                return ([], null, false, 0);
            }

            // Check if the URL path already has a recognised extension
            bool hasExtension = _config.EffectiveExtensions.Any(ext =>
                new Uri(imageUrl).AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

            if (!hasExtension && ContentTypeExtensions.TryGetValue(contentType, out string? ext))
                inferredExt = ext;
        }

        byte[] bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
        return (bytes, inferredExt, false, 0);
    }

    private static string DetermineSecFetchSite(string imageUrl, string pageUrl)
    {
        try
        {
            var imgUri  = new Uri(imageUrl);
            var pageUri = new Uri(pageUrl);

            if (imgUri.Scheme == pageUri.Scheme &&
                imgUri.Host   == pageUri.Host   &&
                imgUri.Port   == pageUri.Port)
                return "same-origin";

            string imgBase  = GetRegistrableDomain(imgUri.Host);
            string pageBase = GetRegistrableDomain(pageUri.Host);

            return string.Equals(imgBase, pageBase, StringComparison.OrdinalIgnoreCase)
                ? "same-site"
                : "cross-site";
        }
        catch
        {
            return "cross-site";
        }
    }

    private static string GetRegistrableDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length >= 2
            ? string.Join(".", parts[^2], parts[^1])
            : host;
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (TaskCanceledException) { }
    }

    public void Dispose() => _throttle.Dispose();
}

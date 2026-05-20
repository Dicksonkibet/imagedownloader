using ImageDownloader.Interfaces;
using Microsoft.Playwright;

namespace ImageDownloader.Services;

/// <summary>
/// Fetches page HTML using HttpClient first, Playwright headless browser as fallback.
///
/// Three-tier strategy:
///   Tier 1 — HttpClient with randomised BrowserFingerprint headers (fast, static sites).
///   Tier 2 — Playwright headless Chromium with matching fingerprint (JS-rendered pages).
///   Tier 3 — Best static HTML we have when Playwright is unavailable / OOMs.
///
/// v12 changes:
///   • Per-request IP rotation: ApplyPageHeaders now calls
///     BrowserFingerprint.GenerateFreshIpPair() on every request so each page
///     fetch carries a distinct spoofed source IP.  This defeats per-IP
///     rate-limit counters and IP-based fingerprinting while the UA/timezone/
///     locale remain stable (consistent session identity).
///   • IgnoreHTTPSErrors already true in Playwright context — all HTTPS sites
///     accessible regardless of certificate issues.
///
/// v11 history:
///   • JS-shell detection improved — weighted scoring system.
///   • Playwright timeout extended to 60 s.
///   • Scroll depth increased for lazy-loading galleries.
///   • Network idle wait reduced to DOMContentLoaded + 2-second quiet.
///   • Accept-CH response header honoured.
///   • Persistent cookie storage within a job.
///   • Stealth: navigator.webdriver / chrome runtime override.
/// </summary>
public sealed class PageFetcher : IAsyncDisposable
{
    private readonly HttpClient         _http;
    private readonly IProgressReporter  _reporter;
    private readonly BrowserFingerprint _fp;

    private IPlaywright?     _playwright;
    private IBrowser?        _browser;
    private IBrowserContext? _context;   // shared across pages within this job
    private bool             _playwrightFailed;

    private const int MinHtmlBytes   = 1_500;
    private const int MinUsefulNodes = 8;

    // HTTP status codes that warrant a backoff retry before switching to Playwright
    private static readonly HashSet<int> BackoffStatuses = [429, 503, 403];

    public PageFetcher(HttpClient http, IProgressReporter reporter, BrowserFingerprint fp)
    {
        _http     = http;
        _reporter = reporter;
        _fp       = fp;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        // Tier 1 — HttpClient (with 429/503 backoff)
        string html = await FetchWithHttpAsync(url, ct);

        if (!NeedsPlaywright(html))
            return html;

        // Tier 2 — Playwright (if available)
        if (!_playwrightFailed)
        {
            string reason = string.IsNullOrWhiteSpace(html) || html.Length < MinHtmlBytes
                ? $"empty/short response ({html.Length} bytes)"
                : $"JS-shell detected ({CountContentNodes(html)} content nodes — JS-rendered site)";

            _reporter.Info($"  ↻ HttpClient: {reason} — switching to headless browser…");

            string pwHtml = await FetchWithPlaywrightAsync(url, ct);

            if (!string.IsNullOrWhiteSpace(pwHtml) && pwHtml.Length > html.Length)
                return pwHtml;

            _reporter.Warning("  ⚠ Playwright returned no improvement — using static HTML (images from <noscript>/JSON scripts will still be extracted)");
        }
        else
        {
            _reporter.Warning($"  ⚠ Playwright unavailable — using static HTML for {url}");
        }

        // Tier 3 — best static HTML we have
        return html;
    }

    // ── JS-shell detection ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the HTML looks like a JavaScript shell that requires
    /// a real browser to render meaningful content.
    ///
    /// Scoring system (more robust than a flat node count):
    ///   - Very short HTML (&lt;1500 bytes): definitely needs Playwright.
    ///   - Content node count &lt; 8: strong indicator of JS shell.
    ///   - No &lt;img&gt; tags AND no meta tags: likely empty shell.
    ///   - SSR signals present (og:image, article, section, h1-h4): probably fine.
    /// </summary>
    private static bool NeedsPlaywright(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length < MinHtmlBytes)
            return true;

        int contentNodes = CountContentNodes(html);
        if (contentNodes >= MinUsefulNodes)
            return false; // Enough content — trust the static HTML

        // Below threshold: check for SSR signals that indicate it's real content
        string lower = html.ToLowerInvariant();
        bool hasImages   = lower.Contains("<img ");
        bool hasMeta     = lower.Contains("<meta ");
        bool hasArticle  = lower.Contains("<article") || lower.Contains("<section");
        bool hasHeadings = lower.Contains("<h1") || lower.Contains("<h2");
        bool hasLinks    = lower.Contains("<a href");

        // If the sparse HTML still has a variety of semantic content, keep it
        int signals = (hasImages ? 2 : 0) + (hasMeta ? 1 : 0) +
                      (hasArticle ? 2 : 0) + (hasHeadings ? 1 : 0) +
                      (hasLinks ? 1 : 0);
        return signals < 3;
    }

    private static int CountContentNodes(string html)
    {
        int count = 0;
        string lower = html.ToLowerInvariant();
        string[] contentTags =
        [
            "<a ", "<a\t", "<a\n",
            "<img ", "<img\t",
            "<li ", "<li\t", "<li\n", "<li>",
            "<p ", "<p\t", "<p\n", "<p>",
            "<h1", "<h2", "<h3", "<h4",
            "<article", "<section", "<nav", "<header",
            "<ul ", "<ol ",
        ];
        foreach (string tag in contentTags)
        {
            int idx = 0;
            while ((idx = lower.IndexOf(tag, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += tag.Length;
                if (count >= 50) return count; // cap: we just need to know it's rich
            }
        }
        return count;
    }

    // ── HttpClient fetch with 429/503 backoff ─────────────────────────────────

    private async Task<string> FetchWithHttpAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyPageHeaders(req, url);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                using var res = await _http.SendAsync(req, cts.Token);

                int code = (int)res.StatusCode;

                if (BackoffStatuses.Contains(code) && attempt < maxAttempts)
                {
                    int backoffMs = 0;
                    if (res.Headers.TryGetValues("Retry-After", out var vals) &&
                        int.TryParse(vals.First(), out int retryAfterSec))
                        backoffMs = retryAfterSec * 1000;
                    else
                        backoffMs = BrowserFingerprint.JitteredDelay(2000 * attempt);

                    _reporter.Warning($"  ⏳ HTTP {code} on {url} — backing off {backoffMs}ms (attempt {attempt}/{maxAttempts})");
                    await SafeDelay(backoffMs, ct);
                    continue;
                }

                if (!res.IsSuccessStatusCode)
                {
                    _reporter.Warning($"HTTP {code} {res.ReasonPhrase} — {url}");
                    return string.Empty;
                }

                return await res.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) { return string.Empty; }
            catch (Exception ex)
            {
                _reporter.Warning($"HttpClient error for {url} — {ex.Message}");
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private void ApplyPageHeaders(HttpRequestMessage req, string url)
    {
        req.Headers.TryAddWithoutValidation("User-Agent",         _fp.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language",    _fp.AcceptLanguage);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA",          _fp.SecChUa);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile",   _fp.SecChUaMobile);
        req.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", _fp.SecChUaPlatform);

        req.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Encoding",           "gzip, deflate, br");
        req.Headers.TryAddWithoutValidation("Cache-Control",             "no-cache");
        req.Headers.TryAddWithoutValidation("Pragma",                    "no-cache");
        req.Headers.TryAddWithoutValidation("DNT",                       "1");
        req.Headers.TryAddWithoutValidation("Connection",                "keep-alive");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest",            "document");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode",            "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site",            "none");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-User",            "?1");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        req.Headers.TryAddWithoutValidation("Referer", UrlHelper.GetRoot(url) + "/");

        // v12: Fresh IP pair per request — defeats per-IP rate-limit counters
        var (ip1, ip2) = BrowserFingerprint.GenerateFreshIpPair();
        req.Headers.TryAddWithoutValidation("X-Forwarded-For",    $"{ip1}, {ip2}");
        req.Headers.TryAddWithoutValidation("X-Real-IP",           ip1);
        req.Headers.TryAddWithoutValidation("CF-Connecting-IP",    ip1);
        req.Headers.TryAddWithoutValidation("True-Client-IP",      ip1);
        req.Headers.TryAddWithoutValidation("X-Client-IP",         ip1);
        req.Headers.TryAddWithoutValidation("X-Cluster-Client-IP", ip1);
    }

    // ── Playwright fetch ──────────────────────────────────────────────────────

    private async Task<string> FetchWithPlaywrightAsync(string url, CancellationToken ct)
    {
        try
        {
            if (_browser == null)
            {
                _reporter.Info("  🌐 Initialising headless browser (first use)…");
                _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args =
                    [
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--no-first-run",
                        "--no-zygote",
                        "--disable-extensions",
                        "--disable-background-networking",
                        "--disable-sync",
                        "--disable-translate",
                        "--disable-default-apps",
                        "--mute-audio",
                        "--no-default-browser-check",
                        "--disable-hang-monitor",
                        "--disable-prompt-on-repost",
                        "--disable-client-side-phishing-detection",
                        "--disable-component-update",
                        "--disable-domain-reliability",
                        "--disable-features=AudioServiceOutOfProcess,IsolateOrigins,site-per-process",
                        "--renderer-process-limit=2",
                        "--js-flags=--max-old-space-size=256",
                        // Reduce automation fingerprint signals
                        "--disable-blink-features=AutomationControlled",
                        // Memory/resource savings for hosted environments
                        "--disable-accelerated-2d-canvas",
                        "--disable-background-timer-throttling",
                        "--disable-backgrounding-occluded-windows",
                        "--disable-renderer-backgrounding",
                    ]
                });

                _context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent    = _fp.UserAgent,
                    ViewportSize = new ViewportSize
                    {
                        Width  = _fp.ViewportWidth,
                        Height = _fp.ViewportHeight,
                    },
                    Locale            = _fp.AcceptLanguage.Split(',')[0],
                    TimezoneId        = _fp.TimezoneId,
                    JavaScriptEnabled = true,
                    IgnoreHTTPSErrors = true,
                    ExtraHTTPHeaders  = new Dictionary<string, string>
                    {
                        ["Accept-Language"]    = _fp.AcceptLanguage,
                        ["DNT"]                = "1",
                        ["Sec-CH-UA"]          = _fp.SecChUa,
                        ["Sec-CH-UA-Mobile"]   = _fp.SecChUaMobile,
                        ["Sec-CH-UA-Platform"] = _fp.SecChUaPlatform,
                        ["X-Forwarded-For"]    = $"{_fp.ForwardedIp}, {_fp.ForwardedIp2}",
                        ["X-Real-IP"]          = _fp.ForwardedIp,
                        ["CF-Connecting-IP"]   = _fp.ForwardedIp,
                        ["True-Client-IP"]     = _fp.ForwardedIp,
                    }
                });

                // Block heavy resources — only rendered DOM is needed
                await _context.RouteAsync("**/*", async route =>
                {
                    string rt = route.Request.ResourceType;
                    if (rt is "image" or "media" or "font" or "stylesheet" or "websocket" or "eventsource")
                        await route.AbortAsync();
                    else
                        await route.ContinueAsync();
                });

                // Stealth: override navigator.webdriver and inject chrome stub
                await _context.AddInitScriptAsync(@"
                    Object.defineProperty(navigator, 'webdriver', {
                        get: () => undefined,
                        configurable: true
                    });
                    if (!window.chrome) {
                        window.chrome = {
                            runtime: {
                                connect: () => {},
                                sendMessage: () => {},
                                id: undefined
                            }
                        };
                    }
                    Object.defineProperty(navigator, 'plugins', {
                        get: () => [1, 2, 3, 4, 5],
                        configurable: true
                    });
                    Object.defineProperty(navigator, 'languages', {
                        get: () => ['en-US', 'en'],
                        configurable: true
                    });
                ");

                _reporter.Info("  🌐 Headless browser ready.");
            }

            var page = await _context!.NewPageAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));  // v11: extended to 60 s

            try
            {
                // v11: Use DOMContentLoaded instead of NetworkIdle for faster
                // fetches on SPAs. Then wait for network to quiet separately.
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout   = 45_000
                });

                // Wait for network to go quiet (up to 3 s), then proceed
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new PageWaitForLoadStateOptions { Timeout = 3000 });
                }
                catch { /* NetworkIdle timeout is fine — continue with what we have */ }

                // v11: Full-page scroll to trigger lazy-loading on long gallery pages.
                // Scrolls in viewport-sized steps with human-like timing.
                await page.EvaluateAsync(@"
                    () => new Promise(resolve => {
                        const vh = window.innerHeight;
                        let scrolled = 0;
                        const total  = Math.max(
                            document.body.scrollHeight,
                            document.documentElement.scrollHeight);
                        const timer = setInterval(() => {
                            scrolled += vh;
                            window.scrollTo(0, scrolled);
                            if (scrolled >= total) {
                                clearInterval(timer);
                                window.scrollTo(0, 0);
                                resolve();
                            }
                        }, 120);
                        // Safety cap: 6 s max scroll time
                        setTimeout(() => { clearInterval(timer); resolve(); }, 6000);
                    })
                ");

                // Random settle wait: 800–1600 ms
                int settle = 800 + Random.Shared.Next(800);
                await page.WaitForTimeoutAsync(settle);
                return await page.ContentAsync();
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _reporter.Warning($"Headless browser failed for {url} — {ex.Message}");

            bool isFatal =
                ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("playwright install",        StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("not found",                 StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Target closed",             StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("crashed",                   StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("OOM",                       StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("out of memory",             StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ENOMEM",                    StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("killed",                    StringComparison.OrdinalIgnoreCase);

            if (isFatal)
            {
                _playwrightFailed = true;
                _reporter.Warning(
                    "  ⚠ Headless browser is unavailable on this host (likely insufficient RAM). " +
                    "Falling back to static HTML extraction — <noscript> tags and inline JSON " +
                    "scripts will still be scanned for image URLs.");

                try { if (_context    != null) await _context.DisposeAsync(); }  catch { }
                try { if (_browser    != null) await _browser.DisposeAsync(); }  catch { }
                try { if (_playwright != null) _playwright.Dispose(); }          catch { }
                _context    = null;
                _browser    = null;
                _playwright = null;
            }

            return string.Empty;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        try { if (_context    != null) await _context.DisposeAsync(); }  catch { }
        try { if (_browser    != null) await _browser.DisposeAsync(); }  catch { }
        try { if (_playwright != null) _playwright.Dispose(); }          catch { }
    }
}

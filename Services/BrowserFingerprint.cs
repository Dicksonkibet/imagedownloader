namespace ImageDownloader.Services;

/// <summary>
/// Generates randomised but internally consistent browser fingerprints.
///
/// v12 changes:
///   • Per-request IP rotation: GenerateFreshIpPair() produces a new
///     (primary, secondary) IPv4 pair on every call. PageFetcher and
///     ImageDownloaderService now call this per-request instead of reusing
///     the job-level ForwardedIp / ForwardedIp2. This means each HTTP
///     request appears to originate from a distinct IP, defeating
///     per-IP rate-limit counters and IP-based bot fingerprinting.
///   • ForwardedIp / ForwardedIp2 still exist for Playwright context
///     initialisation (browser context headers are set once at launch).
///   • Chrome 137 added to Chrome build pool.
///   • Edge 136 added to Edge build pool.
///
/// v10/v11 history:
///   • Chrome builds updated to 132–136 (latest stable as of mid-2025).
///   • Added Edge 132–135 User-Agents.
///   • Windows 11 UA strings added alongside Windows 10.
///   • Sec-CH-UA "Not.A/Brand";v="99" token.
///   • Expanded timezone pool (30 entries) and viewport pool (10 entries).
///   • BrandFamily property exposed so PageFetcher can adjust Accept-CH hints.
///
/// One fingerprint is created per job so all requests inside a single crawl
/// share a consistent UA/timezone/locale identity, but every individual
/// HTTP request now gets fresh spoofed IPs.
/// </summary>
public sealed class BrowserFingerprint
{
    // ── Public properties ─────────────────────────────────────────────────────

    public string UserAgent       { get; }
    public string SecChUa         { get; }
    public string SecChUaPlatform { get; }
    public string SecChUaMobile   { get; }

    /// <summary>"Chrome" or "Edge" — for downstream Playwright context use.</summary>
    public string BrandFamily     { get; }

    /// <summary>Randomised public IPv4 for X-Forwarded-For / X-Real-IP headers.</summary>
    public string ForwardedIp     { get; }

    /// <summary>Secondary hop IP for a realistic two-hop XFF chain.</summary>
    public string ForwardedIp2    { get; }

    public string TimezoneId      { get; }
    public string AcceptLanguage  { get; }
    public int    ViewportWidth   { get; }
    public int    ViewportHeight  { get; }

    // ── Known Chrome builds ───────────────────────────────────────────────────

    private sealed record ChromeBuild(
        string Version,
        string WinUA, string Win11UA, string MacUA, string LinuxUA,
        string SecChUa);

    private static readonly ChromeBuild[] ChromeBuilds =
    [
        new("137",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"137\", \"Chromium\";v=\"137\", \"Not.A/Brand\";v=\"99\""),
        new("136",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"136\", \"Chromium\";v=\"136\", \"Not.A/Brand\";v=\"99\""),
        new("135",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"135\", \"Chromium\";v=\"135\", \"Not.A/Brand\";v=\"99\""),
        new("134",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"134\", \"Chromium\";v=\"134\", \"Not.A/Brand\";v=\"99\""),
        new("133",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\", \"Not.A/Brand\";v=\"99\""),
        new("132",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"132\", \"Chromium\";v=\"132\", \"Not.A/Brand\";v=\"99\""),
    ];

    // ── Known Edge builds ─────────────────────────────────────────────────────

    private sealed record EdgeBuild(
        string Version,
        string WinUA, string Win11UA, string MacUA,
        string SecChUa);

    private static readonly EdgeBuild[] EdgeBuilds =
    [
        new("136",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0",
            "\"Microsoft Edge\";v=\"136\", \"Chromium\";v=\"136\", \"Not.A/Brand\";v=\"99\""),
        new("135",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0",
            "\"Microsoft Edge\";v=\"135\", \"Chromium\";v=\"135\", \"Not.A/Brand\";v=\"99\""),
        new("134",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36 Edg/134.0.0.0",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36 Edg/134.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36 Edg/134.0.0.0",
            "\"Microsoft Edge\";v=\"134\", \"Chromium\";v=\"134\", \"Not.A/Brand\";v=\"99\""),
        new("132",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0",
            "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0",
            "\"Microsoft Edge\";v=\"132\", \"Chromium\";v=\"132\", \"Not.A/Brand\";v=\"99\""),
    ];

    // ── OS platforms ──────────────────────────────────────────────────────────

    private enum Platform { Windows10, Windows11, Mac, Linux }

    private static readonly (string TimezoneId, string Lang)[] TimezonePool =
    [
        ("America/New_York",     "en-US,en;q=0.9"),
        ("America/Chicago",      "en-US,en;q=0.9"),
        ("America/Los_Angeles",  "en-US,en;q=0.9"),
        ("America/Denver",       "en-US,en;q=0.9"),
        ("America/Phoenix",      "en-US,en;q=0.9"),
        ("America/Toronto",      "en-CA,en;q=0.9"),
        ("America/Vancouver",    "en-CA,en;q=0.9"),
        ("Europe/London",        "en-GB,en;q=0.9"),
        ("Europe/Dublin",        "en-IE,en;q=0.9"),
        ("Europe/Paris",         "fr-FR,fr;q=0.9,en;q=0.8"),
        ("Europe/Berlin",        "de-DE,de;q=0.9,en;q=0.8"),
        ("Europe/Amsterdam",     "nl-NL,nl;q=0.9,en;q=0.8"),
        ("Europe/Madrid",        "es-ES,es;q=0.9,en;q=0.8"),
        ("Europe/Rome",          "it-IT,it;q=0.9,en;q=0.8"),
        ("Europe/Warsaw",        "pl-PL,pl;q=0.9,en;q=0.8"),
        ("Europe/Stockholm",     "sv-SE,sv;q=0.9,en;q=0.8"),
        ("Asia/Tokyo",           "ja-JP,ja;q=0.9,en;q=0.8"),
        ("Asia/Seoul",           "ko-KR,ko;q=0.9,en;q=0.8"),
        ("Asia/Shanghai",        "zh-CN,zh;q=0.9,en;q=0.8"),
        ("Asia/Singapore",       "en-SG,en;q=0.9"),
        ("Asia/Kolkata",         "en-IN,en;q=0.9,hi;q=0.8"),
        ("Asia/Dubai",           "en-AE,en;q=0.9,ar;q=0.8"),
        ("Asia/Bangkok",         "th-TH,th;q=0.9,en;q=0.8"),
        ("Asia/Jakarta",         "id-ID,id;q=0.9,en;q=0.8"),
        ("Australia/Sydney",     "en-AU,en;q=0.9"),
        ("Australia/Melbourne",  "en-AU,en;q=0.9"),
        ("Pacific/Auckland",     "en-NZ,en;q=0.9"),
        ("America/Sao_Paulo",    "pt-BR,pt;q=0.9,en;q=0.8"),
        ("America/Mexico_City",  "es-MX,es;q=0.9,en;q=0.8"),
        ("Africa/Johannesburg",  "en-ZA,en;q=0.9"),
    ];

    private static readonly (int W, int H)[] ViewportPool =
    [
        (1920, 1080), (1440, 900),  (1366, 768),
        (1536, 864),  (1280, 800),  (1600, 900),
        (1280, 720),  (1920, 1200), (2560, 1440),
        (1680, 1050),
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public BrowserFingerprint()
    {
        var rng = Random.Shared;
        var tz  = TimezonePool[rng.Next(TimezonePool.Length)];
        var vp  = ViewportPool[rng.Next(ViewportPool.Length)];

        // ~75% Chrome, ~25% Edge — mirrors real-world market share
        bool useEdge = rng.Next(4) == 0;

        if (useEdge)
        {
            BrandFamily = "Edge";
            var build    = EdgeBuilds[rng.Next(EdgeBuilds.Length)];
            var platform = (Platform)rng.Next(3); // Win10, Win11, Mac only (no Linux Edge)

            UserAgent = platform switch
            {
                Platform.Windows11 => build.Win11UA,
                Platform.Mac       => build.MacUA,
                _                  => build.WinUA,
            };
            SecChUa = build.SecChUa;
            SecChUaPlatform = platform == Platform.Mac ? "\"macOS\"" : "\"Windows\"";
        }
        else
        {
            BrandFamily = "Chrome";
            var build    = ChromeBuilds[rng.Next(ChromeBuilds.Length)];
            var platform = (Platform)rng.Next(4);

            UserAgent = platform switch
            {
                Platform.Windows11 => build.Win11UA,
                Platform.Mac       => build.MacUA,
                Platform.Linux     => build.LinuxUA,
                _                  => build.WinUA,
            };
            SecChUa = build.SecChUa;
            SecChUaPlatform = platform switch
            {
                Platform.Mac   => "\"macOS\"",
                Platform.Linux => "\"Linux\"",
                _              => "\"Windows\"",
            };
        }

        SecChUaMobile = "?0";
        ForwardedIp   = GeneratePublicIpv4(rng);
        ForwardedIp2  = GeneratePublicIpv4(rng);
        TimezoneId    = tz.TimezoneId;
        AcceptLanguage = tz.Lang;
        ViewportWidth  = vp.W;
        ViewportHeight = vp.H;
    }

    // ── Per-request IP rotation ───────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh pair of public IPv4 addresses for use in a single
    /// HTTP request.  Call this per-request (not once per job) so each
    /// request appears to originate from a different IP.
    /// Returns (primary, secondary) — primary goes in X-Forwarded-For /
    /// X-Real-IP / CF-Connecting-IP; secondary is the second XFF hop.
    /// </summary>
    public static (string Primary, string Secondary) GenerateFreshIpPair()
        => (GeneratePublicIpv4(Random.Shared), GeneratePublicIpv4(Random.Shared));

    // ── IP generation ─────────────────────────────────────────────────────────

    private static string GeneratePublicIpv4(Random rng)
    {
        while (true)
        {
            int a = rng.Next(1,   224);
            int b = rng.Next(0,   256);
            int c = rng.Next(0,   256);
            int d = rng.Next(1,   255);

            if (a == 10)                          continue;
            if (a == 127)                         continue;
            if (a == 169 && b == 254)             continue;
            if (a == 172 && b is >= 16 and <= 31) continue;
            if (a == 192 && b == 168)             continue;
            if (a >= 224)                         continue;

            return $"{a}.{b}.{c}.{d}";
        }
    }

    // ── Human-like delay ──────────────────────────────────────────────────────

    public static int JitteredDelay(int baseMs)
    {
        double jitter = 1.0 + (Random.Shared.NextDouble() * 0.8 - 0.4);
        return Math.Max(80, (int)(baseMs * jitter));
    }
}

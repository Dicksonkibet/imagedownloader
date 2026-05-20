using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ImageDownloader.Services;

/// <summary>
/// Parses HTML to extract image URLs and navigable links.
/// </summary>
public static class HtmlParser
{
    private static readonly string[] ImgSrcAttrs =
    [
        "data-src", "data-lazy-src", "data-lazy", "data-original",
        "data-full-src", "data-img-src", "data-hi-res-src", "data-url",
        "data-image", "data-large", "data-full", "data-echo",
        "data-lazyload", "data-load", "data-delayed-url",
        "data-bg", "data-background", "data-background-image",
        "data-thumb", "data-thumbnail", "data-poster",
        "data-lazy-loaded", "data-ll-status",
        "data-large_image", "data-zoom-image", "data-src-retina",
        "data-full-size-url", "data-zoom", "data-hi-res",
        "data-original-src", "data-2x",
        "data-lazy-original", "data-lazy-src-retina",
        "src",
    ];

    private static readonly string[] SrcsetAttrs =
    [
        "srcset", "data-srcset", "data-lazy-srcset", "data-responsive-image",
        "data-responsive-sizes", "data-sizes"
    ];

    // FIX: all regex strings must use @"..." verbatim literals
    private static readonly Regex ScriptAbsoluteUrlRegex = new(
        @"""((?:https?:)?(?:\\?/\\?/|//)[^""\\ ]{4,}\.(?:jpg|jpeg|png|webp|gif|avif|bmp)(?:[?#][^""\s]*)?)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex ScriptRelativeUrlRegex = new(
        @"""((?:/[^""?\s]{1,300}\.(?:jpg|jpeg|png|webp|gif|avif|bmp))(?:[?#][^""\s]*)?)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex CssUrlRegex = new(
        @"url\(\s*['""]?((?:https?:)?//[^'""\)\s]+\.(?:jpg|jpeg|png|webp|gif|avif|bmp)[^'""\)\s]*)['""]?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(3));

    private static readonly Regex CssRelUrlRegex = new(
        @"url\(\s*['""]?((?:/[^'""\)\s]{1,300}\.(?:jpg|jpeg|png|webp|gif|avif|bmp))[^'""\)\s]*)['""]?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(3));

    // ── Page title ────────────────────────────────────────────────────────────

    public static string? ExtractTitle(HtmlDocument doc) =>
        doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();

    // ── Image URL extraction ──────────────────────────────────────────────────

    public static List<string> ExtractImageUrls(
        HtmlDocument doc, string pageUrl, string[] allowedExtensions)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 1 & 2. img / source / picture + lazy-load data-* attributes ──────
        var imgNodes = doc.DocumentNode
            .SelectNodes("//img | //source | //picture")
            ?? Enumerable.Empty<HtmlNode>();

        var lazyNodes = doc.DocumentNode.SelectNodes(
            "//*[@data-src or @data-lazy or @data-original or @data-lazy-src " +
            "or @data-bg or @data-background or @data-background-image " +
            "or @data-thumb or @data-thumbnail or @data-large_image " +
            "or @data-zoom-image or @data-zoom or @data-hi-res]")
            ?? Enumerable.Empty<HtmlNode>();

        foreach (var node in imgNodes.Concat(lazyNodes).Distinct())
        {
            foreach (string attr in ImgSrcAttrs)
                TryAdd(node.GetAttributeValue(attr, null), pageUrl, allowedExtensions, results);

            foreach (string attr in SrcsetAttrs)
            {
                string? srcset = node.GetAttributeValue(attr, null);
                if (string.IsNullOrWhiteSpace(srcset)) continue;
                foreach (string entry in srcset.Split(','))
                {
                    string part = entry.Trim().Split(' ')[0].Trim();
                    TryAdd(part, pageUrl, allowedExtensions, results);
                }
            }
        }

        // ── 3. <noscript> inner HTML ──────────────────────────────────────────
        foreach (var ns in doc.DocumentNode
                     .SelectNodes("//noscript") ?? Enumerable.Empty<HtmlNode>())
        {
            string inner = ns.InnerHtml;
            if (string.IsNullOrWhiteSpace(inner)) continue;

            var nsDoc = new HtmlDocument();
            nsDoc.LoadHtml(inner);

            foreach (var node in nsDoc.DocumentNode
                         .SelectNodes("//img | //source") ?? Enumerable.Empty<HtmlNode>())
            {
                foreach (string attr in ImgSrcAttrs)
                    TryAdd(node.GetAttributeValue(attr, null), pageUrl, allowedExtensions, results);

                foreach (string attr in SrcsetAttrs)
                {
                    string? srcset = node.GetAttributeValue(attr, null);
                    if (string.IsNullOrWhiteSpace(srcset)) continue;
                    foreach (string entry in srcset.Split(','))
                    {
                        string part = entry.Trim().Split(' ')[0].Trim();
                        TryAdd(part, pageUrl, allowedExtensions, results);
                    }
                }
            }
        }

        // ── 4. <a href> pointing directly to image files ─────────────────────
        foreach (var a in doc.DocumentNode
                     .SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            TryAdd(a.GetAttributeValue("href", ""), pageUrl, allowedExtensions, results);

        // ── 5. Open Graph / Twitter Card / thumbnail meta tags ────────────────
        foreach (var meta in doc.DocumentNode.SelectNodes(
                     "//meta[@property='og:image' or @name='twitter:image' " +
                     "or @property='og:image:url' or @name='og:image' " +
                     "or @property='og:image:secure_url' " +
                     "or @name='thumbnail' or @name='image']")
                 ?? Enumerable.Empty<HtmlNode>())
        {
            TryAdd(meta.GetAttributeValue("content", null), pageUrl, allowedExtensions, results);
        }

        // ── 5b. Schema.org itemprop="image" ───────────────────────────────────
        foreach (var node in doc.DocumentNode
                     .SelectNodes("//*[@itemprop='image']") ?? Enumerable.Empty<HtmlNode>())
        {
            string? content = node.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(content))
                TryAdd(content, pageUrl, allowedExtensions, results);

            TryAdd(node.GetAttributeValue("src", null), pageUrl, allowedExtensions, results);
        }

        // ── 6. Inline style background-image / background shorthand ──────────
        foreach (var node in doc.DocumentNode
                     .SelectNodes("//*[@style]") ?? Enumerable.Empty<HtmlNode>())
        {
            string style = node.GetAttributeValue("style", "");
            if (string.IsNullOrWhiteSpace(style)) continue;
            try
            {
                foreach (Match m in CssUrlRegex.Matches(style))
                    TryAdd(m.Groups[1].Value, pageUrl, allowedExtensions, results);
            }
            catch (RegexMatchTimeoutException) { }
        }

        // ── 6b. Inline <style> block CSS ─────────────────────────────────────
        foreach (var styleTag in doc.DocumentNode
                     .SelectNodes("//style[not(@src)]") ?? Enumerable.Empty<HtmlNode>())
        {
            string css = styleTag.InnerText;
            if (string.IsNullOrWhiteSpace(css) || css.Length < 10) continue;
            try
            {
                foreach (Match m in CssUrlRegex.Matches(css))
                    TryAdd(m.Groups[1].Value, pageUrl, allowedExtensions, results);
                foreach (Match m in CssRelUrlRegex.Matches(css))
                    TryAdd(m.Groups[1].Value, pageUrl, allowedExtensions, results);
            }
            catch (RegexMatchTimeoutException) { }
        }

        // ── 7 & 9. Inline <script> JSON blobs ────────────────────────────────
        foreach (var script in doc.DocumentNode
                     .SelectNodes("//script[not(@src)]") ?? Enumerable.Empty<HtmlNode>())
        {
            string text = script.InnerText;
            if (string.IsNullOrWhiteSpace(text) || text.Length < 20) continue;
            try
            {
                foreach (Match m in ScriptAbsoluteUrlRegex.Matches(text))
                {
                    string raw = m.Groups[1].Value.Replace("\\/", "/");
                    if (raw.StartsWith("//")) raw = "https:" + raw;
                    TryAdd(raw, pageUrl, allowedExtensions, results);
                }
                foreach (Match m in ScriptRelativeUrlRegex.Matches(text))
                    TryAdd(m.Groups[1].Value, pageUrl, allowedExtensions, results);
            }
            catch (RegexMatchTimeoutException) { }
        }

        // ── 10. <video poster> thumbnails ─────────────────────────────────────
        foreach (var video in doc.DocumentNode
                     .SelectNodes("//video[@poster]") ?? Enumerable.Empty<HtmlNode>())
            TryAdd(video.GetAttributeValue("poster", null), pageUrl, allowedExtensions, results);

        // ── 12. <link rel="preload" as="image"> ───────────────────────────────
        foreach (var link in doc.DocumentNode.SelectNodes(
                     "//link[@rel='preload' and @as='image' and @href]")
                 ?? Enumerable.Empty<HtmlNode>())
        {
            TryAdd(link.GetAttributeValue("href", null), pageUrl, allowedExtensions, results);

            string? srcset = link.GetAttributeValue("imagesrcset", null);
            if (!string.IsNullOrWhiteSpace(srcset))
            {
                foreach (string entry in srcset.Split(','))
                {
                    string part = entry.Trim().Split(' ')[0].Trim();
                    TryAdd(part, pageUrl, allowedExtensions, results);
                }
            }
        }

        // ── 15. Next.js /_next/image?url= — decode the real image URL ─────────
        var nextJsUrls = results
            .Where(u => u.Contains("/_next/image", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string nextUrl in nextJsUrls)
        {
            results.Remove(nextUrl);
            string? realUrl = UrlHelper.ExtractNextJsImageUrl(nextUrl, pageUrl);
            if (realUrl != null)
                TryAdd(realUrl, pageUrl, allowedExtensions, results);
        }

        return [.. results];
    }

    // ── Page link extraction ──────────────────────────────────────────────────

    public static List<string> ExtractLinks(
        HtmlDocument doc,
        string pageUrl,
        string rootUrl,
        HashSet<string> visited)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in doc.DocumentNode
                     .SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            string href = node.GetAttributeValue("href", "");
            string? resolved = UrlHelper.Resolve(href, pageUrl);
            if (resolved == null) continue;
            if (!resolved.StartsWith(rootUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (resolved.Contains('#') || visited.Contains(resolved)) continue;

            string lc = resolved.ToLowerInvariant();
            if (lc.EndsWith(".jpg") || lc.EndsWith(".jpeg") ||
                lc.EndsWith(".png") || lc.EndsWith(".webp") ||
                lc.EndsWith(".gif") || lc.EndsWith(".avif") ||
                lc.EndsWith(".bmp")) continue;

            links.Add(resolved);
        }

        return [.. links];
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static void TryAdd(
        string? raw, string pageUrl,
        string[] allowedExtensions,
        HashSet<string> results)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string? resolved = UrlHelper.Resolve(raw.Trim(), pageUrl);
        if (resolved != null && UrlHelper.IsImageUrl(resolved, allowedExtensions))
            results.Add(resolved);
    }
}
namespace ImageDownloader.Services;

/// <summary>
/// URL resolution, validation, and image-detection helpers.
///
/// v11 changes:
///   • CDN domain pattern recognition — Cloudinary, imgix, Unsplash, Shopify,
///     AWS CloudFront image paths, Akamai, Fastly image transforms, and more
///     are now detected as image URLs even when they carry no file extension.
///   • Extended query-param detection: fm=, f=, format=, ext=, type=, as=image.
///   • Next.js /_next/image?url= decoded so the real image URL is extracted.
///   • IsLikelyCdnImageUrl exposed for ImageDownloaderService to consult when
///     deciding whether to attempt a download of an extensionless URL.
/// </summary>
public static class UrlHelper
{
    // ── Known CDN hostname patterns that always serve images ─────────────────
    // These domains serve images even when the path has no file extension.
    private static readonly string[] CdnImageHosts =
    [
        // Cloudinary
        "res.cloudinary.com",
        "images.cloudinary.com",
        // imgix
        ".imgix.net",
        // Unsplash
        "images.unsplash.com",
        "plus.unsplash.com",
        // Shopify
        "cdn.shopify.com",
        "cdn.shopifycloud.com",
        // AWS CloudFront image-specific paths handled below
        // Akamai / Fastly generic image paths
        "images.akamai.com",
        // Contentful
        "images.ctfassets.net",
        // Storyblok
        "img2.storyblok.com",
        "a.storyblok.com",
        // Sanity.io
        "cdn.sanity.io",
        // Prismic
        "images.prismic.io",
        // Webflow
        "uploads-ssl.webflow.com",
        "assets-global.website-files.com",
        // Squarespace
        "images.squarespace-cdn.com",
        // Wix
        "static.wixstatic.com",
        // WordPress VIP / Jetpack
        "i0.wp.com", "i1.wp.com", "i2.wp.com",
        // Tumblr
        "64.media.tumblr.com",
        "78.media.tumblr.com",
        // Discord CDN
        "cdn.discordapp.com",
        "media.discordapp.net",
        // Imgur
        "i.imgur.com",
        // GitHub raw
        "raw.githubusercontent.com",
        "avatars.githubusercontent.com",
        // Google user content
        "lh1.googleusercontent.com", "lh2.googleusercontent.com",
        "lh3.googleusercontent.com", "lh4.googleusercontent.com",
        "lh5.googleusercontent.com", "lh6.googleusercontent.com",
        // Twitter / X media
        "pbs.twimg.com",
        "ton.twimg.com",
        // Instagram CDN
        "scontent.cdninstagram.com",
        // Flickr
        "live.staticflickr.com",
        "farm1.staticflickr.com", "farm2.staticflickr.com",
        "farm3.staticflickr.com", "farm4.staticflickr.com",
        "farm5.staticflickr.com", "farm6.staticflickr.com",
        // Pinterest
        "i.pinimg.com",
        // Gravatar
        "www.gravatar.com",
        "secure.gravatar.com",
        // Medium
        "miro.medium.com",
        // Substack
        "substackcdn.com",
        // Notion
        "prod-files-secure.s3.us-west-2.amazonaws.com",
        // Amazon product images
        "images-na.ssl-images-amazon.com",
        "m.media-amazon.com",
        // eBay
        "i.ebayimg.com",
        // Alibaba / AliExpress
        "ae01.alicdn.com",
        // Generic image microservice patterns (checked via path below)
    ];

    // ── Path patterns that indicate an image-serving endpoint ────────────────
    private static readonly string[] ImagePathPrefixes =
    [
        "/image/upload/",    // Cloudinary
        "/image/fetch/",     // Cloudinary fetch
        "/image/",           // generic image endpoints
        "/images/",          // common
        "/img/",             // common
        "/photos/",          // common
        "/media/",           // common
        "/assets/images/",   // common
        "/static/images/",   // common
        "/wp-content/uploads/", // WordPress
        "/cdn-cgi/image/",   // Cloudflare image resizing
        "/_next/image",      // Next.js (handled specially)
    ];

    // ── Recognised image-format query param keys ──────────────────────────────
    private static readonly string[] ImageFormatQueryKeys =
        ["format", "fmt", "fm", "f", "ext", "type", "as", "output"];

    // ── Recognised image-format values ───────────────────────────────────────
    private static readonly string[] ImageFormatValues =
        ["jpg", "jpeg", "png", "webp", "gif", "avif", "bmp", "tiff", "tif", "svg"];

    // ── Public API ────────────────────────────────────────────────────────────

    public static string? Resolve(string? src, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;

        // Hard-reject non-http schemes up front
        if (src.StartsWith("data:",       StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("mailto:",     StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("tel:",        StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("file:",       StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("blob:",       StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("chrome:",     StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("about:",      StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            if (Uri.TryCreate(src, UriKind.Absolute, out var abs))
            {
                if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps)
                    return null;
                return NormalizeUrl(abs.ToString());
            }

            if (Uri.TryCreate(new Uri(baseUrl), src, out var rel))
            {
                if (rel.Scheme != Uri.UriSchemeHttp && rel.Scheme != Uri.UriSchemeHttps)
                    return null;
                return NormalizeUrl(rel.ToString());
            }
        }
        catch { /* ignore malformed */ }

        return null;
    }

    /// <summary>
    /// Determines if a URL points to an image using:
    ///   1. File extension in the path
    ///   2. Image-format query parameters (format=jpg, fm=webp, etc.)
    ///   3. Known CDN hostname patterns
    ///   4. Known image-serving path prefixes
    /// </summary>
    public static bool IsImageUrl(string url, string[] allowedExtensions)
    {
        try
        {
            var uri = new Uri(url);

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            // 1. Direct extension match on path
            string path = uri.AbsolutePath.ToLowerInvariant();
            if (allowedExtensions.Any(ext => path.EndsWith(ext)))
                return true;

            // 2. Query-parameter format hints
            string query = uri.Query.ToLowerInvariant();
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string key in ImageFormatQueryKeys)
                {
                    foreach (string val in ImageFormatValues)
                    {
                        if (query.Contains($"{key}={val}"))
                            return true;
                    }
                }

                // as=image (e.g. Cloudflare)
                if (query.Contains("as=image")) return true;
            }

            // 3. Next.js /_next/image — always an image; real URL is in ?url=
            if (path.StartsWith("/_next/image", StringComparison.Ordinal))
                return true;

            // 4. Known CDN hostnames
            string host = uri.Host.ToLowerInvariant();
            foreach (string cdn in CdnImageHosts)
            {
                if (cdn.StartsWith('.'))
                {
                    if (host.EndsWith(cdn, StringComparison.Ordinal)) return true;
                }
                else
                {
                    if (host == cdn || host.EndsWith("." + cdn)) return true;
                }
            }

            // 5. Known image-serving path prefixes (broad match — only trust
            //    when the path also has a reasonable image-like segment)
            foreach (string prefix in ImagePathPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true for URLs that are almost certainly images even without an
    /// extension (CDN hosts or image path prefixes). Used by the downloader to
    /// decide whether to attempt a fetch and rely on Content-Type for detection.
    /// </summary>
    public static bool IsLikelyCdnImageUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            string path = uri.AbsolutePath.ToLowerInvariant();

            foreach (string cdn in CdnImageHosts)
            {
                if (cdn.StartsWith('.'))
                {
                    if (host.EndsWith(cdn)) return true;
                }
                else
                {
                    if (host == cdn || host.EndsWith("." + cdn)) return true;
                }
            }

            foreach (string prefix in ImagePathPrefixes)
                if (path.StartsWith(prefix)) return true;

            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// For Next.js /_next/image?url=&amp;w=&amp;q= URLs, extracts and returns the
    /// real underlying image URL. Returns null for non-Next.js URLs.
    /// </summary>
    public static string? ExtractNextJsImageUrl(string url, string pageUrl)
    {
        try
        {
            var uri = new Uri(url);
            if (!uri.AbsolutePath.StartsWith("/_next/image", StringComparison.OrdinalIgnoreCase))
                return null;

            var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string? innerUrl = qs["url"];
            if (string.IsNullOrWhiteSpace(innerUrl)) return null;

            return Resolve(innerUrl, pageUrl);
        }
        catch { return null; }
    }

    public static string GetRoot(string url)
    {
        try
        {
            var uri = new Uri(url);
            return $"{uri.Scheme}://{uri.Host}";
        }
        catch { return url; }
    }

    private static string NormalizeUrl(string url)
    {
        if (url.EndsWith('/') && url.Count(c => c == '/') > 2)
            url = url.TrimEnd('/');
        return url;
    }
}

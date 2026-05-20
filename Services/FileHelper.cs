using System.Text.RegularExpressions;

namespace ImageDownloader.Services;

/// <summary>
/// File and folder name utilities — path-safe naming and deduplication.
///
/// v11 changes:
///   • BuildUniqueFilename accepts an optional inferredExtension parameter.
///     When the URL has no recognised image extension but the server returned
///     an image/* Content-Type, the downloader passes the inferred extension
///     (e.g. ".jpg") so CDN files get a proper suffix.
/// </summary>
public static class FileHelper
{
    private static readonly char[] InvalidChars =
        [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // ── Folder name ───────────────────────────────────────────────────────────

    public static string DeriveFolderName(string? pageTitle, string pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(pageTitle))
        {
            string decoded = System.Net.WebUtility.HtmlDecode(pageTitle).Trim();
            if (decoded.Length > 70) decoded = decoded[..70];
            return Sanitize(decoded);
        }

        try
        {
            string seg = new Uri(pageUrl)
                             .Segments
                             .LastOrDefault(s => s.TrimEnd('/').Length > 0)
                             ?.TrimEnd('/') ?? "root";
            return Sanitize(Uri.UnescapeDataString(seg));
        }
        catch { return "unnamed"; }
    }

    // ── Image filename ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a unique filename for an image file.
    ///
    /// If <paramref name="inferredExtension"/> is supplied (non-null), it is
    /// used when the URL path has no recognised image extension — this handles
    /// CDN images served without file extensions (Cloudinary, imgix, etc.).
    /// </summary>
    public static string BuildUniqueFilename(
        string imageUrl,
        string targetFolder,
        string[] allowedExtensions,
        string? inferredExtension = null)
    {
        string name;
        string ext;

        try
        {
            var uri      = new Uri(imageUrl);
            string local = uri.LocalPath;

            // Strip CDN transform path segments to get a clean name.
            // e.g. /image/upload/w_300,h_200/sample.jpg  →  sample
            //      /cdn-cgi/image/width=800/photos/hero    →  hero
            string[] segments = local.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string lastSegment = segments.LastOrDefault() ?? local;

            name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(lastSegment));
            ext  = Path.GetExtension(lastSegment).ToLowerInvariant();
            int q = ext.IndexOf('?');
            if (q >= 0) ext = ext[..q];
        }
        catch
        {
            name = Guid.NewGuid().ToString("N")[..10];
            ext  = inferredExtension ?? ".jpg";
        }

        // If the URL had no usable extension, fall back:
        //   1. inferredExtension from Content-Type
        //   2. .jpg
        if (string.IsNullOrWhiteSpace(ext) || !allowedExtensions.Contains(ext))
            ext = inferredExtension ?? ".jpg";

        if (string.IsNullOrWhiteSpace(name) || name == "image" || name.Length < 2)
            name = Guid.NewGuid().ToString("N")[..12];

        name = Sanitize(name);

        string candidate = name + ext;
        int counter = 2;
        while (File.Exists(Path.Combine(targetFolder, candidate)))
            candidate = $"{name}_{counter++}{ext}";

        return candidate;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    public static string Sanitize(string name)
    {
        string clean = string.Concat(name.Select(c => InvalidChars.Contains(c) ? '_' : c));
        clean = Regex.Replace(clean, @"\s+", "_");
        clean = Regex.Replace(clean, @"_+",  "_").Trim('_');
        return string.IsNullOrWhiteSpace(clean) ? "unnamed" : clean;
    }

    public static string EnsureDirectory(params string[] paths)
    {
        string full = Path.Combine(paths);
        Directory.CreateDirectory(full);
        return full;
    }

    public static string FormatSize(long bytes) =>
        bytes switch
        {
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024     => $"{bytes / 1_024.0:F1} KB",
            _            => $"{bytes} B"
        };
}

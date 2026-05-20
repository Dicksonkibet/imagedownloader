using HtmlAgilityPack;
using ImageDownloader.Interfaces;
using ImageDownloader.Models;

namespace ImageDownloader.Services;

/// <summary>
/// Recursively crawls pages and downloads images.
/// PageFetcher handles the HttpClient-vs-Playwright decision per page;
/// this class just orchestrates crawl order, depth, and per-page folders.
/// </summary>
public sealed class Crawler
{
    private readonly PageFetcher             _fetcher;
    private readonly ImageDownloaderService  _downloader;
    private readonly DownloadStats           _stats;
    private readonly IProgressReporter       _reporter;
    private readonly StartJobRequest         _config;
    private readonly string                  _outputFolder;
    private readonly HashSet<string>         _visited =
        new(StringComparer.OrdinalIgnoreCase);

    public Crawler(
        PageFetcher fetcher,
        ImageDownloaderService downloader,
        DownloadStats stats,
        IProgressReporter reporter,
        StartJobRequest config,
        string outputFolder)
    {
        _fetcher      = fetcher;
        _downloader   = downloader;
        _stats        = stats;
        _reporter     = reporter;
        _config       = config;
        _outputFolder = outputFolder;
    }

    public Task StartAsync(string startUrl, CancellationToken ct) =>
        CrawlAsync(startUrl, UrlHelper.GetRoot(startUrl), 0, ct);

    private async Task CrawlAsync(
        string pageUrl, string rootUrl, int depth, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (depth > _config.MaxDepth)   return;
        if (!_visited.Add(pageUrl))     return;

        _stats.IncrementPages();
        _reporter.PageHeader(_stats.Pages, depth, pageUrl);

        string html = await _fetcher.FetchAsync(pageUrl, ct);

        if (string.IsNullOrWhiteSpace(html))
        {
            _reporter.Warning($"  ✗ Could not retrieve content from: {pageUrl}");
            return;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string? title      = HtmlParser.ExtractTitle(doc);
        string  folderName = FileHelper.DeriveFolderName(title, pageUrl);
        string  folderPath = FileHelper.EnsureDirectory(_outputFolder, folderName);

        _reporter.Dim($"  > Folder : {folderName}");

        var imageUrls = HtmlParser.ExtractImageUrls(
            doc, pageUrl, _config.EffectiveExtensions);
        _reporter.Dim($"  > Images : {imageUrls.Count} found");

        if (imageUrls.Count == 0)
            _reporter.Info("  > No matching images on this page (check image type filters)");

        // Pass pageUrl so the downloader sets the correct Referer per image
        await _downloader.DownloadAllAsync(imageUrls, folderPath, pageUrl, ct);

        if (depth < _config.MaxDepth)
        {
            var links = HtmlParser.ExtractLinks(doc, pageUrl, rootUrl, _visited);
            _reporter.Dim($"  > Links  : {links.Count} to follow");

            foreach (string link in links)
            {
                if (ct.IsCancellationRequested) break;
                await SafeDelay(_config.RequestDelayMs, ct);
                await CrawlAsync(link, rootUrl, depth + 1, ct);
            }
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }
}

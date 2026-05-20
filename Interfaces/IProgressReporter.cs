namespace ImageDownloader.Interfaces;

/// <summary>
/// Abstracts progress reporting so the same crawler/downloader logic
/// works with any transport (SignalR, console, etc.).
/// </summary>
public interface IProgressReporter
{
    void PageHeader(int pageNum, int depth, string url);
    void ImageSaved(string filename, long bytes);
    void ImageSkipped(string filename);
    void ImageRetry(string filename, int attempt, int max);
    void ImageFailed(string filename, string reason);
    void Info(string text);
    void Dim(string text);
    void Warning(string text);
    void Error(string text);
}

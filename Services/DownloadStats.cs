namespace ImageDownloader.Services;

/// <summary>Thread-safe download statistics using interlocked operations.</summary>
public sealed class DownloadStats
{
    private int _pages;
    private int _saved;
    private int _skipped;
    private int _failed;

    public int Pages   => Volatile.Read(ref _pages);
    public int Saved   => Volatile.Read(ref _saved);
    public int Skipped => Volatile.Read(ref _skipped);
    public int Failed  => Volatile.Read(ref _failed);

    public void IncrementPages()   => Interlocked.Increment(ref _pages);
    public void IncrementSaved()   => Interlocked.Increment(ref _saved);
    public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
    public void IncrementFailed()  => Interlocked.Increment(ref _failed);
}

namespace z3l.Scraper.Tests;

/// <summary>A folder of its own for a test, deleted with everything in it at the end.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "z3l-scraper-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Files under the folder whose names end with the ending, at any depth.</summary>
    public string[] Files(string ending) =>
        Directory.Exists(Path) ? Directory.GetFiles(Path, "*" + ending, SearchOption.AllDirectories) : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

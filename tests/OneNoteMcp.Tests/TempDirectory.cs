namespace OneNoteMcp.Tests;

/// <summary>A fresh folder under the system temp path, deleted on dispose. Tests never touch the real %APPDATA%.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onenote-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a test's logger; the OS temp cleanup gets it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

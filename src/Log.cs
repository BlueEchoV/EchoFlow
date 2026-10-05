namespace EchoFlow;

/// Plain-text log at %LOCALAPPDATA%\EchoFlow\log.txt, for troubleshooting.
static class Log
{
    static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(Settings.LocalDir, "log.txt");

    static Log()
    {
        Directory.CreateDirectory(Settings.LocalDir);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}

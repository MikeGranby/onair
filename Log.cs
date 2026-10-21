namespace OnAir;

/// <summary>Writes to the console and to OnAir.log next to the exe (the only output when running hidden).</summary>
static class Log
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "OnAir.log");

    const long MaxFileSize = 1_000_000;
    static readonly object Gate = new();

    public static void Write(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxFileSize)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the monitor.
            }
        }
    }
}

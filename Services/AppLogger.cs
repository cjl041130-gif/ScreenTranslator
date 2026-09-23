using System.IO;

namespace ScreenTranslator.Services;

/// <summary>Low-volume, bounded rolling logs. No frame-by-frame logging.</summary>
public sealed class AppLogger
{
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public AppLogger(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(AppSettings.DataDirectory, "logs");
        Directory.CreateDirectory(DirectoryPath);
    }
    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? error = null) => Write("ERROR", $"{message} {error}");
    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(DirectoryPath, "screen-translator.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        var source = i == 1 ? path : path + "." + (i - 1);
                        if (File.Exists(source)) File.Move(source, path + "." + i, true);
                    }
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
            catch (IOException) { /* Logging must never stop capture or shutdown. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}

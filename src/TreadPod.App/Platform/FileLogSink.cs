using Serilog;
using Serilog.Core;

namespace TreadPod.App.Platform;

public sealed class FileLogSink : IDisposable
{
    private readonly Logger _logger;

    public string LogDirectory { get; }

    public FileLogSink(string? logDirectory = null)
    {
        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TreadPod",
            "logs");

        Directory.CreateDirectory(LogDirectory);

        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "treadpod-.log"),
                outputTemplate: "{Timestamp:yyyy-MM-dd} {Message:l}{NewLine}",
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 512 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 5,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
    }

    public void Write(string line)
    {
        try
        {
            _logger.Information("{Line:l}", line);
        }
        catch
        {
            // Never let file logging disrupt BLE operations.
        }
    }

    public void WriteSystem(string message) =>
        Write($"{DateTime.Now:HH:mm:ss} {message}");

    public void Dispose()
    {
        try
        {
            _logger.Dispose();
        }
        catch
        {
        }
    }
}

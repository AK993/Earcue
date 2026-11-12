using Serilog;
using Serilog.Core;

namespace Earcue.Core.Logging;

public static class AppLogger
{
    private static Logger? _logger;

    public static ILogger Instance => _logger ??= CreateDefaultLogger();

    public static void Initialize(bool diagnosticMode = false)
    {
        _logger = CreateDefaultLogger(diagnosticMode);
    }

    private static Logger CreateDefaultLogger(bool diagnosticMode = false)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(localAppData, "Earcue", "logs");

        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logDir, "earcue-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            );

        if (diagnosticMode)
        {
            config.MinimumLevel.Verbose();
        }

        return config.CreateLogger();
    }
}

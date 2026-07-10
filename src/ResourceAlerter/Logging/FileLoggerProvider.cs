using Microsoft.Extensions.Logging;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Logging;

/// <summary>
/// Minimal rotating file logger: one file per calendar day, additionally rolled mid-day if it
/// exceeds the configured size. Old day-files beyond the retention window are pruned on startup.
/// Exists so the service can be diagnosed locally without depending on mail delivery.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggingOptions _options;
    private readonly string _directory;
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private int _rollIndex;

    public FileLoggerProvider(FileLoggingOptions options)
    {
        _options = options;
        _directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);

        Directory.CreateDirectory(_directory);
        PruneOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();

            if (_writer.BaseStream.Length > _options.MaxFileSizeMb * 1024L * 1024L)
            {
                _rollIndex++;
                _writer.Dispose();
                _writer = null;
                EnsureWriter();
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && today == _currentDay)
        {
            return;
        }

        if (today != _currentDay)
        {
            _rollIndex = 0;
            PruneOldFiles();
        }

        _currentDay = today;
        _writer?.Dispose();

        var path = GetLogFilePath(today, _rollIndex);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = false,
        };
    }

    private string GetLogFilePath(DateOnly day, int rollIndex)
    {
        var suffix = rollIndex == 0 ? "" : $"_{rollIndex}";
        return Path.Combine(_directory, $"resourcealerter-{day:yyyyMMdd}{suffix}.log");
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_options.RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "resourcealerter-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best-effort cleanup; never let log housekeeping take the service down.
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

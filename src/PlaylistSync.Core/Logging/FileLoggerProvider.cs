using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlaylistSync.Core.Logging;

/// <summary>
/// Minimal file logger: one file per day (playlist-sync-yyyyMMdd.log), keeps the newest N files.
/// Writes are serialized through a background queue so logging never blocks callers.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory, int retainFiles = 7)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Prune(retainFiles);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var file = Path.Combine(_directory, $"playlist-sync-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Utf8NoBom);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
        }
    }

    private void Prune(int retain)
    {
        try
        {
            foreach (var old in new DirectoryInfo(_directory).GetFiles("playlist-sync-*.log")
                         .OrderByDescending(f => f.Name).Skip(retain))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Enqueue(line);
        }
    }
}

// ============================================================
// FILE        : FileLogger.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Minimal file logger so the service logs to
//               C:\ProgramData\BEVGateway\logs even when no
//               console attached.
// OWNS        : Local log file output.
// CALLED BY   : DI via AddProvider.
// ============================================================

using Microsoft.Extensions.Logging;

namespace BEVGateway.Service;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path) { _path = path; }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(_path, _lock, categoryName);

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _path;
        private readonly object _lock;
        private readonly string _category;

        public FileLogger(string path, object @lock, string category)
        {
            _path = path; _lock = @lock; _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{logLevel,-11}] {_category}: {formatter(state, exception)}";
            if (exception is not null) line += $"\n  {exception}";
            lock (_lock)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); }
                catch { /* logger failures must not crash the service */ }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

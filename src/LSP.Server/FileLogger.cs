using Microsoft.Extensions.Logging;

namespace LSP.Server;

/// <summary>
/// Minimalisticky file logger — zapisuje logy do AppPaths log adresare,
/// aby šlo diagnostikovat i GUI běh (WinExe nemá viditelnou konzoli).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly Lock _lock = new();

    public FileLoggerProvider()
    {
        _logDir = AppPaths.SubDir("logs");
        Directory.CreateDirectory(_logDir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _logDir, _lock);

    public void Dispose() { }

    private sealed class FileLogger(string category, string logDir, Lock gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var shortCategory = category.Split('.').LastOrDefault() ?? category;
            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel.ToString()[..3].ToUpperInvariant()}] {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
                line += $"\n    {exception}";

            var file = Path.Combine(logDir, $"lsp-{DateTime.Now:yyyyMMdd}.log");
            lock (gate)
            {
                try { File.AppendAllText(file, line + Environment.NewLine); } catch { /* best-effort */ }
            }
        }
    }
}

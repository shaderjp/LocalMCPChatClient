using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LocalMCPChatClient.Infrastructure;

/// <summary>
/// Writes a small rolling diagnostic log while masking credentials and common secret fields.
/// </summary>
public sealed partial class RedactingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly Lock _writeLock = new();

    public RedactingFileLoggerProvider(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        Directory.CreateDirectory(_logsDirectory);
    }

    public ILogger CreateLogger(string categoryName) => new RedactingFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        text = MaskSecrets(text);
        var line = $"{DateTimeOffset.Now:O} [{level}] {category}: {text}{Environment.NewLine}";
        var path = Path.Combine(_logsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

        lock (_writeLock)
        {
            File.AppendAllText(path, line);
        }
    }

    public static string MaskSecrets(string value)
    {
        var masked = BearerPattern().Replace(value, "Bearer ***");
        masked = SecretJsonPattern().Replace(masked, match => $"{match.Groups[1].Value}***{match.Groups[3].Value}");
        return SecretAssignmentPattern().Replace(masked, match => $"{match.Groups[1].Value}=***");
    }

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+\-/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(\\\"(?:token|authorization|api[_-]?key|password|secret)[^\\\"]*\\\"\\s*:\\s*\\\")(.*?)(\\\")", RegexOptions.CultureInvariant)]
    private static partial Regex SecretJsonPattern();

    [GeneratedRegex(@"(?i)\b((?:token|authorization|api[_-]?key|password|secret)[A-Za-z0-9_.-]*)\s*=\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentPattern();

    private sealed class RedactingFileLogger(RedactingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

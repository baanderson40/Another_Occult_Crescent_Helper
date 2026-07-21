using System;

namespace AOCCH.Logging;

public enum AocchLogLevel
{
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record AocchLogEntry(DateTimeOffset Timestamp, AocchLogLevel Level, string Message)
{
    public string Format() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}

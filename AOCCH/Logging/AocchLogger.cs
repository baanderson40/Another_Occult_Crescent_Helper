using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace AOCCH.Logging;

public sealed class AocchLogger
{
    private const int MaxEntries = 1000;

    private readonly IPluginLog pluginLog;
    private readonly List<AocchLogEntry> entries = new();
    private readonly object gate = new();

    public AocchLogger(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public IReadOnlyList<AocchLogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

    public void Verbose(string message) => Add(AocchLogLevel.Verbose, message);
    public void Debug(string message) => Add(AocchLogLevel.Debug, message);
    public void Info(string message) => Add(AocchLogLevel.Info, message);
    public void Warning(string message) => Add(AocchLogLevel.Warning, message);
    public void Error(string message) => Add(AocchLogLevel.Error, message);

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
        }
    }

    private void Add(AocchLogLevel level, string message)
    {
        var entry = new AocchLogEntry(DateTimeOffset.Now, level, message);

        lock (gate)
        {
            entries.Add(entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(0, entries.Count - MaxEntries);
            }
        }

        WriteToDalamud(level, message);
    }

    private void WriteToDalamud(AocchLogLevel level, string message)
    {
        switch (level)
        {
            case AocchLogLevel.Verbose:
                pluginLog.Verbose(message);
                break;
            case AocchLogLevel.Debug:
                pluginLog.Debug(message);
                break;
            case AocchLogLevel.Info:
                pluginLog.Information(message);
                break;
            case AocchLogLevel.Warning:
                pluginLog.Warning(message);
                break;
            case AocchLogLevel.Error:
                pluginLog.Error(message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }
}

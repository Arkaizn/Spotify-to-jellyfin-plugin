using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>What a run does.</summary>
public enum SyncMode
{
    /// <summary>Fetch from Spotify (when changed), rematch, write playlists.</summary>
    Full,

    /// <summary>No Spotify calls: rematch cached tracks and write playlists.</summary>
    RematchOnly,

    /// <summary>Scan the Jellyfin music libraries for new/changed files first, then rematch.</summary>
    ScanLibrary,
}

/// <summary>One log line shown on the dashboard.</summary>
/// <param name="Seq">Sequence number.</param>
/// <param name="Time">UTC time.</param>
/// <param name="Level">"info", "warn" or "error".</param>
/// <param name="Text">Message.</param>
public sealed record LogLine(long Seq, DateTime Time, string Level, string Text);

/// <summary>Live status of the sync engine. Lives in memory; a few KB at most.</summary>
public sealed class SyncStatus
{
    private const int MaxLog = 200;
    private readonly object _lock = new();
    private readonly LinkedList<LogLine> _log = new();
    private long _seq;

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
    public bool Running { get; private set; }

    /// <summary>Gets the mode of the current or last run.</summary>
    public SyncMode Mode { get; private set; }

    /// <summary>Gets the playlist being processed.</summary>
    public string? Current { get; private set; }

    /// <summary>Gets the progress in percent.</summary>
    public double Progress { get; private set; }

    /// <summary>Gets when the current / last run started.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Gets when the last run finished.</summary>
    public DateTime? FinishedAt { get; private set; }

    /// <summary>Gets the one-line result of the last run.</summary>
    public string? LastResult { get; private set; }

    /// <summary>Gets a counter that changes whenever anything visible changes (used as ETag).</summary>
    public long Version { get; private set; }

    /// <summary>Gets the latest log sequence number.</summary>
    public long LogSeq => _seq;

    internal void Start(SyncMode mode)
    {
        lock (_lock)
        {
            Running = true;
            Mode = mode;
            Current = null;
            Progress = 0;
            StartedAt = DateTime.UtcNow;
            Version++;
        }
    }

    internal void Step(string? current, double progress)
    {
        lock (_lock)
        {
            Current = current;
            Progress = Math.Round(progress, 1);
            Version++;
        }
    }

    internal void Finish(string result)
    {
        lock (_lock)
        {
            Running = false;
            Current = null;
            Progress = 100;
            FinishedAt = DateTime.UtcNow;
            LastResult = result;
            Version++;
        }
    }

    /// <summary>Adds a dashboard log line.</summary>
    /// <param name="level">Level.</param>
    /// <param name="text">Text.</param>
    public void Log(string level, string text)
    {
        lock (_lock)
        {
            _log.AddLast(new LogLine(++_seq, DateTime.UtcNow, level, text));
            if (_log.Count > MaxLog)
            {
                _log.RemoveFirst();
            }

            Version++;
        }
    }

    /// <summary>Log lines newer than <paramref name="since"/>.</summary>
    /// <param name="since">Last sequence the client has.</param>
    /// <returns>Lines.</returns>
    public List<LogLine> LogSince(long since)
    {
        lock (_lock)
        {
            var list = new List<LogLine>();
            foreach (var l in _log)
            {
                if (l.Seq > since)
                {
                    list.Add(l);
                }
            }

            return list;
        }
    }
}

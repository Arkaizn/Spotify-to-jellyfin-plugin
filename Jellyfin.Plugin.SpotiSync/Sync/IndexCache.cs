using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SpotiSync.Matching;

namespace Jellyfin.Plugin.SpotiSync.Sync;

/// <summary>
/// Holds the library index only while it is useful: built on demand, kept for a few minutes
/// after the last sync / rematch / search (one one-shot timer, no polling), then dropped.
/// </summary>
public sealed class IndexCache : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private LibraryIndex? _index;
    private int _gen;
    private Timer? _release;

    /// <summary>Gets or sets how long the index survives after its last use.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the time the last build spent reading the library (ms).</summary>
    public long LastReadMs { get; private set; }

    /// <summary>Gets the total time of the last build (ms).</summary>
    public long LastBuildMs { get; private set; }

    /// <summary>Gets a value indicating whether an index is currently held.</summary>
    public bool IsLoaded => _index is not null;

    /// <summary>Returns the index for library generation <paramref name="gen"/>, building it if needed.</summary>
    /// <param name="gen">Library generation. A negative value means "any cached index is fine; if one
    /// has to be built, tag it with -gen".</param>
    /// <param name="source">Library.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The index.</returns>
    public async Task<LibraryIndex> GetAsync(int gen, ILibrarySource source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancelRelease();
            if (_index is not null && (gen <= 0 || _gen == gen))
            {
                return _index;
            }

            gen = Math.Abs(gen);

            _index = null; // let the old one go before building the new one
            var total = System.Diagnostics.Stopwatch.StartNew();
            var hint = source.GetFingerprint().Count;
            var read = new System.Diagnostics.Stopwatch();
            var built = LibraryIndexBuilder.Build(Timed(source.ReadAll(ct), read), hint, ct);
            LastReadMs = read.ElapsedMilliseconds;
            LastBuildMs = total.ElapsedMilliseconds;
            _index = built;
            _gen = gen;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Schedules the index to be dropped after <see cref="KeepAlive"/>.</summary>
    public void ReleaseLater()
    {
        lock (_timerLock)
        {
            _release?.Dispose();
            _release = _index is null ? null : new Timer(_ => Drop(), null, KeepAlive, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops the index now.</summary>
    public void Drop()
    {
        lock (_timerLock)
        {
            _release?.Dispose();
            _release = null;
            _index = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Drop();
        _gate.Dispose();
    }

    private static System.Collections.Generic.IEnumerable<LibraryRecord> Timed(System.Collections.Generic.IEnumerable<LibraryRecord> source, System.Diagnostics.Stopwatch sw)
    {
        using var e = source.GetEnumerator();
        while (true)
        {
            sw.Start();
            bool more = e.MoveNext();
            sw.Stop();
            if (!more)
            {
                yield break;
            }

            yield return e.Current;
        }
    }

    private void CancelRelease()
    {
        lock (_timerLock)
        {
            _release?.Dispose();
            _release = null;
        }
    }
}

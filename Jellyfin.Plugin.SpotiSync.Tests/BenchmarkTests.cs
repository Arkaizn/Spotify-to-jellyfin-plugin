using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.SpotiSync.Matching;
using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SpotiSync.Tests;

/// <summary>
/// Performance budgets on a synthetic library. They need a Release build running alone (other
/// tests running in parallel distort time and GC numbers), so they only run when SPOTISYNC_BENCH=1:
/// <c>SPOTISYNC_BENCH=1 dotnet test -c Release --filter Category=Benchmark --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Benchmark")]
[Collection("Benchmark")]
public class BenchmarkTests
{
    private const int LibrarySize = 50_000;
    private readonly ITestOutputHelper _out;

    public BenchmarkTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static readonly string[] Words =
    [
        "love", "night", "heart", "fire", "dream", "rain", "blue", "summer", "dance", "light", "stay", "home", "time", "baby",
        "world", "life", "girl", "boy", "road", "river", "city", "gold", "wild", "young", "free", "run", "fall", "rise", "sky",
        "moon", "sun", "star", "shadow", "ghost", "angel", "devil", "money", "highway", "train", "ocean", "storm", "thunder",
        "electric", "silver", "paper", "glass", "stone", "broken", "lonely", "crazy", "sweet", "bitter", "midnight", "morning",
        "forever", "never", "always", "tonight", "yesterday", "tomorrow", "kiss", "touch", "hold", "dark", "bright", "echo",
        "machine", "radio", "velvet", "neon", "garden", "winter", "autumn", "spring", "desert", "island", "mountain", "valley",
        "kingdom", "empire", "rebel", "hero", "liar", "lover", "stranger", "friend", "enemy", "secret", "promise", "memory",
    ];

    private static readonly string[] Suffixes = ["", "", "", "", " - Remastered 2011", " (feat. Guest)", " - Live", " - Radio Edit", " (Acoustic)"];

    /// <summary>Deterministic synthetic library: titles of 1-4 words, 6000 artists, albums of 10 songs.</summary>
    private static IEnumerable<LibraryRecord> SyntheticLibrary(int n, int seed = 7)
    {
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            yield return Record(i, rng);
        }
    }

    private static LibraryRecord Record(int i, Random rng)
    {
        int words = 1 + (i % 4);
        var title = string.Join(' ', Enumerable.Range(0, words).Select(k => Cap(Words[rng.Next(Words.Length)])));
        title += " " + Cap(Words[(i * 7) % Words.Length]) + (i % 13 == 0 ? " Part " + (i % 3 + 1).ToString(CultureInfo.InvariantCulture) : string.Empty);
        title += Suffixes[i % Suffixes.Length];
        var artist = "Artist " + Cap(Words[(i / 10) % Words.Length]) + " " + ((i / 10) % 6000).ToString(CultureInfo.InvariantCulture);
        var album = "Album " + (i / 10).ToString(CultureInfo.InvariantCulture);
        return new LibraryRecord(GuidFor(i), title, [artist], [artist], album, (120 + (i % 240)) * TimeSpan.TicksPerSecond, null);
    }

    private static Guid GuidFor(int i)
    {
        var b = new byte[16];
        BitConverter.TryWriteBytes(b, i);
        b[15] = 0x5A;
        return new Guid(b);
    }

    private static string Cap(string w) => char.ToUpperInvariant(w[0]) + w[1..];

    /// <summary>Spotify playlists: 80% songs from the library (with Spotify-style title variations), 20% unknown.</summary>
    private static List<List<SpTrack>> SyntheticPlaylists(int playlists, int tracks)
    {
        var rng = new Random(99);
        var libRng = new Random(7);
        var lib = Enumerable.Range(0, LibrarySize).Select(i => Record(i, libRng)).ToArray();
        var result = new List<List<SpTrack>>();
        for (int p = 0; p < playlists; p++)
        {
            var list = new List<SpTrack>(tracks);
            for (int t = 0; t < tracks; t++)
            {
                string id = "sp" + p.ToString(CultureInfo.InvariantCulture) + "_" + t.ToString(CultureInfo.InvariantCulture);
                if (rng.NextDouble() < 0.8)
                {
                    var r = lib[rng.Next(lib.Length)];
                    var name = r.Name!;
                    name = rng.Next(3) switch
                    {
                        0 => name.ToLowerInvariant(),
                        1 => name.Replace(" - Remastered 2011", " - 2011 Remaster", StringComparison.Ordinal),
                        _ => name,
                    };
                    list.Add(FakeSpotify.Track(id, name, r.Artists![0], (int)(r.RunTimeTicks!.Value / TimeSpan.TicksPerSecond) + rng.Next(-2, 3), r.Album!));
                }
                else
                {
                    list.Add(FakeSpotify.Track(id, "Unknown " + Words[rng.Next(Words.Length)] + " Song " + t.ToString(CultureInfo.InvariantCulture), "Nobody " + p.ToString(CultureInfo.InvariantCulture), 200));
                }
            }

            result.Add(list);
        }

        return result;
    }

    [BenchmarkFact]
    public void IndexBuild_50k_Under1s_Under50MB_AndReleased()
    {
        // warm up JIT on a small library
        LibraryIndexBuilder.Build(SyntheticLibrary(2000), 2000);

        // Records are generated up front so the timing covers the index only (in Jellyfin the
        // database read comes on top; see README for numbers measured in a real server).
        var records = SyntheticLibrary(LibrarySize).ToArray();
        long before = GC.GetTotalMemory(true);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var index = LibraryIndexBuilder.Build(records, LibrarySize);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        long retained = GC.GetTotalMemory(true) - before;
        Assert.Equal(LibrarySize, index.Count);
        GC.KeepAlive(index);
        index = null;
        long afterDrop = GC.GetTotalMemory(true) - before;

        // Peak of live memory while building, in a second, untimed pass. GetTotalMemory(false) would also
        // count garbage the GC simply has not collected yet; that depends on the machine's GC budget (it
        // made this check fail on some CI runners at 84 MB while live memory stays ~15 MB), so each sample
        // collects first.
        before = GC.GetTotalMemory(true);
        long peak = 0;
        IEnumerable<LibraryRecord> Sampled()
        {
            for (int k = 0; k < records.Length; k++)
            {
                if (k % 5000 == 4999)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(true) - before);
                }

                yield return records[k];
            }
        }

        var again = LibraryIndexBuilder.Build(Sampled(), LibrarySize);
        peak = Math.Max(peak, GC.GetTotalMemory(true) - before);
        GC.KeepAlive(again);
        GC.KeepAlive(records);

        _out.WriteLine($"Index build {LibrarySize:N0} items: {sw.ElapsedMilliseconds} ms, retained {retained / 1048576.0:F1} MB, peak live memory while building {peak / 1048576.0:F1} MB, allocated during build {allocated / 1048576.0:F1} MB (transient garbage), after drop {afterDrop / 1048576.0:F1} MB");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"build took {sw.ElapsedMilliseconds} ms");
        Assert.True(retained < 50L * 1024 * 1024, $"retained {retained} bytes");
        Assert.True(peak < 50L * 1024 * 1024, $"peak {peak} bytes");
        Assert.True(afterDrop < 2L * 1024 * 1024, $"after drop {afterDrop} bytes");
    }

    [BenchmarkFact]
    public void Matching_10x500_Under2s()
    {
        var index = LibraryIndexBuilder.Build(SyntheticLibrary(LibrarySize), LibrarySize);
        var playlists = SyntheticPlaylists(10, 500);
        var matcher = new Matcher(index);

        // warm up
        matcher.Match(new MatchQuery("Love Night", ["Artist Love 1"], "Album 1", 200_000, null));

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int matched = 0, review = 0, missing = 0;
        foreach (var pl in playlists)
        {
            foreach (var t in pl)
            {
                var r = matcher.Match(new MatchQuery(t.Name, t.Artists!.Select(a => a.Name!).ToArray(), t.Album?.Name, t.DurationMs, null));
                switch (r.Kind)
                {
                    case MatchKind.Matched: matched++; break;
                    case MatchKind.Review: review++; break;
                    default: missing++; break;
                }
            }
        }

        sw.Stop();
        proc.Refresh();
        var cpu = proc.TotalProcessorTime - cpu0;
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        _out.WriteLine($"Matching 10x500 tracks vs {LibrarySize:N0}: wall {sw.ElapsedMilliseconds} ms, process CPU {cpu.TotalMilliseconds:F0} ms, allocated {alloc / 1048576.0:F1} MB; matched {matched}, need a look {review}, not found {missing}");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"matching took {sw.ElapsedMilliseconds} ms");
        Assert.True(matched >= 3600, $"only {matched} matched"); // ~4000 tracks exist in the library
        Assert.True(missing >= 900, $"only {missing} not found"); // ~1000 unknown tracks
    }

    [BenchmarkFact]
    public async Task NoChangeSync_NoWrites_Under200ms()
    {
        using var h = new Harness();
        h.Library.Items.AddRange(SyntheticLibrary(LibrarySize));
        var playlists = SyntheticPlaylists(10, 500);
        for (int p = 0; p < playlists.Count; p++)
        {
            h.Spotify.AddPlaylist("p" + p.ToString(CultureInfo.InvariantCulture), "Playlist " + p.ToString(CultureInfo.InvariantCulture), playlists[p]);
        }

        await h.Sync();
        for (int p = 0; p < playlists.Count; p++)
        {
            await h.Enable("p" + p.ToString(CultureInfo.InvariantCulture));
        }

        var sw = Stopwatch.StartNew();
        var first = await h.Sync();
        var firstMs = sw.ElapsedMilliseconds;
        Assert.Equal(10, first.Fetched);
        h.Cache.Drop(); // as if the keep-alive window had passed

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        sw.Restart();
        var second = await h.Sync();
        sw.Stop();
        proc.Refresh();
        var cpu = proc.TotalProcessorTime - cpu0;
        _out.WriteLine($"First sync (10x500, index build + match + create): {firstMs} ms. No-change sync: wall {sw.ElapsedMilliseconds} ms, process CPU {cpu.TotalMilliseconds:F0} ms, Spotify list calls {h.Spotify.ListCalls}, item calls {h.Spotify.ItemCalls}, playlist writes {second.Writes}, state writes {second.StateWrites}, index used {second.IndexUsed}");
        Assert.Equal(0, second.Writes);
        Assert.Equal(0, second.StateWrites);
        Assert.Equal(0, second.Fetched);
        Assert.False(second.IndexUsed);
        Assert.True(sw.ElapsedMilliseconds < 200, $"no-change sync took {sw.ElapsedMilliseconds} ms");
    }
}

[CollectionDefinition("Benchmark", DisableParallelization = true)]
public class BenchmarkCollection
{
}

/// <summary>A fact that runs only when SPOTISYNC_BENCH=1.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPOTISYNC_BENCH") != "1")
        {
            Skip = "Benchmark: set SPOTISYNC_BENCH=1 and run in Release with --filter Category=Benchmark";
        }
    }
}

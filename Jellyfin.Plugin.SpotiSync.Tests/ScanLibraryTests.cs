using Jellyfin.Plugin.SpotiSync.Spotify;
using Jellyfin.Plugin.SpotiSync.State;
using Jellyfin.Plugin.SpotiSync.Sync;
using Xunit;

namespace Jellyfin.Plugin.SpotiSync.Tests;

/// <summary>The "Scan library" button: find changed folders by date, let Jellyfin update only those, rematch.</summary>
public sealed class ScanLibraryTests : IDisposable
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spotisync-scan-" + Guid.NewGuid().ToString("N"));

    public ScanLibraryTests()
    {
        // Music/Artist A/Album 1/01.flac, Music/Artist B/Album 2/01.flac, all old.
        Old1 = File("Artist A/Album 1/01.flac");
        File("Artist B/Album 2/01.flac");
        foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Append(_root))
        {
            Directory.SetLastWriteTimeUtc(d, Old);
        }
    }

    private string Old1 { get; }

    public void Dispose() => Directory.Delete(_root, true);

    private string File(string rel, bool old = true)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, "x");
        if (old)
        {
            System.IO.File.SetLastWriteTimeUtc(p, Old);
        }

        return p;
    }

    private string P(string rel) => Path.Combine(_root, rel);

    [Fact]
    public void Finder_NothingChanged_FindsNothing()
    {
        var r = ChangeFinder.Find([_root], Old.AddDays(1), CancellationToken.None);
        Assert.Empty(r.Dirs);
        Assert.Equal(2, r.FilesChecked);
        Assert.Equal(5, r.DirsChecked);
    }

    [Fact]
    public void Finder_NewAlbum_EditedFile_MovedInFile_HiddenIgnored()
    {
        var since = DateTime.UtcNow.AddMinutes(-1);
        File("Artist A/Album 3/01.flac", old: false);                 // new album
        System.IO.File.SetLastWriteTimeUtc(Old1, DateTime.UtcNow);    // tags edited in place
        File("Artist B/Album 2/02.flac");                             // moved in with an old date: the folder date changes
        Directory.SetLastWriteTimeUtc(P("Artist B"), Old);
        File(".trash/x.flac", old: false);                            // hidden: ignored
        Directory.SetLastWriteTimeUtc(_root, Old);

        var r = ChangeFinder.Find([_root + Path.DirectorySeparatorChar], since, CancellationToken.None);
        Assert.Equal([P("Artist A"), P("Artist A/Album 1"), P("Artist A/Album 3"), P("Artist B/Album 2")], r.Dirs);
    }

    [Fact]
    public void Finder_MissingRoot_IsSkipped()
        => Assert.Empty(ChangeFinder.Find([P("nope")], Old, CancellationToken.None).Dirs);

    [Fact]
    public void IsUnder_RespectsFolderBoundaries()
    {
        Assert.True(ChangeFinder.IsUnder(P("Artist A/Album 1"), P("Artist A")));
        Assert.True(ChangeFinder.IsUnder(P("Artist A"), P("Artist A")));
        Assert.False(ChangeFinder.IsUnder(P("Artist AB"), P("Artist A")));
    }

    private sealed class FakeScanner(IReadOnlyList<ScanLibrary> libs, Action<IReadOnlyList<string>> onRefresh) : ILibraryScanner
    {
        public List<(string Lib, IReadOnlyList<string> Dirs)> Refreshes { get; } = [];

        public DateTime? FullScan { get; set; }

        public IReadOnlyList<ScanLibrary> Libraries() => libs;

        public DateTime? LastFullScanUtc() => FullScan;

        public Task<int> RefreshAsync(ScanLibrary library, IReadOnlyList<string> dirs, IProgress<double> progress, CancellationToken ct)
        {
            Refreshes.Add((library.Id, dirs));
            progress.Report(50);
            onRefresh(dirs);
            progress.Report(100);
            return Task.FromResult(dirs.Count);
        }
    }

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private static SpTrack T(string id, string title) => FakeSpotify.Track(id, title, "Band " + title, 200, "Album " + title);

    private static async Task<Harness> Setup()
    {
        var h = new Harness();
        h.Library.Add("Alpha", "Band Alpha", 200, "Album Alpha");
        h.Public.Lists["A"] = new PublicPlaylist("Spotify A", null, [T("a1", "Alpha"), T("a2", "Bravo")], false);
        var c = await h.Store.LoadCatalogAsync();
        c.Playlists.Add(new CatalogEntry { Id = "pub-A", SpotifyId = "A", Source = CatalogEntry.SourcePublic, Name = "Spotify A", Readable = true });
        c.Syncs.Add(new SyncDef { Id = SyncDefs.NewId(), Name = "Mix", Sources = ["pub-A"] });
        await h.Store.SaveCatalogAsync(c);
        return h;
    }

    [Fact]
    public async Task Scan_RefreshesOnlyChangedFolders_ThenMatches_WithoutSpotify()
    {
        using var h = await Setup();
        await h.Sync();
        Assert.Single(h.JellyfinItems("Mix"));
        int calls = h.Spotify.ListCalls + h.Spotify.ItemCalls + h.Public.Calls;

        var music = new ScanLibrary("lib-music", "Music", true, [_root]);
        var movies = new ScanLibrary("lib-movies", "Movies", false, [P("nope")]);
        Guid bravo = Guid.Empty;
        var scanner = new FakeScanner([music, movies], _ => bravo = h.Library.Add("Bravo", "Band Bravo", 200, "Album Bravo"));
        h.Engine.Scanner = scanner;
        File("Artist B/Album 2/02.flac", old: false);

        var progress = new Recorder();
        var o = await h.Engine.RunAsync(SyncMode.ScanLibrary, progress, CancellationToken.None);

        var refresh = Assert.Single(scanner.Refreshes); // movies not chosen (default: music libraries)
        Assert.Equal("lib-music", refresh.Lib);
        Assert.Equal([P("Artist B/Album 2")], refresh.Dirs);
        Assert.Equal(calls, h.Spotify.ListCalls + h.Spotify.ItemCalls + h.Public.Calls);
        Assert.Contains(bravo, h.JellyfinItems("Mix"));
        Assert.True(o.IndexUsed);
        Assert.Equal(progress.Values.Order().ToList(), progress.Values);
        Assert.Equal(100, progress.Values[^1]);
        Assert.False(h.Engine.Status.Running);

        // Remembered: the next check only looks at what changed after this one.
        Assert.True((await h.Store.LoadCatalogAsync()).ScannedAt.ContainsKey("lib-music"));
        var ago = DateTime.UtcNow.AddMinutes(-10);
        Directory.SetLastWriteTimeUtc(P("Artist B/Album 2"), ago);
        System.IO.File.SetLastWriteTimeUtc(P("Artist B/Album 2/02.flac"), ago);
        var c = await h.Store.LoadCatalogAsync();
        c.ScannedAt["lib-music"] = DateTime.UtcNow;
        await h.Store.SaveCatalogAsync(c);
        await h.Engine.RunAsync(SyncMode.ScanLibrary, null, CancellationToken.None);
        Assert.Single(scanner.Refreshes);
    }

    [Fact]
    public async Task Scan_FirstRun_LooksBackToJellyfinsLastFullScan()
    {
        using var h = await Setup();
        File("Artist A/Album 1/02.flac", old: false);
        Directory.SetLastWriteTimeUtc(P("Artist A/Album 1"), DateTime.UtcNow.AddHours(-3));
        System.IO.File.SetLastWriteTimeUtc(P("Artist A/Album 1/02.flac"), DateTime.UtcNow.AddHours(-3));
        var scanner = new FakeScanner([new ScanLibrary("m", "Music", true, [_root])], _ => { }) { FullScan = DateTime.UtcNow.AddHours(-1) };
        h.Engine.Scanner = scanner;
        await h.Engine.RunAsync(SyncMode.ScanLibrary, null, CancellationToken.None);
        Assert.Empty(scanner.Refreshes); // Jellyfin's full scan an hour ago already saw it

        scanner.FullScan = DateTime.UtcNow.AddHours(-5);
        var c = await h.Store.LoadCatalogAsync();
        c.ScannedAt.Clear();
        await h.Store.SaveCatalogAsync(c);
        await h.Engine.RunAsync(SyncMode.ScanLibrary, null, CancellationToken.None);
        Assert.Equal([P("Artist A/Album 1")], Assert.Single(scanner.Refreshes).Dirs);
    }

    [Fact]
    public void Selection_DefaultsToMusic_OrUsesTheChosenOnes()
    {
        var all = new List<ScanLibrary> { new("a", "Music", true, ["/m"]), new("b", "Audiobooks", false, ["/b"]), new("c", "More music", true, ["/c"]) };
        Assert.Equal(["a", "c"], SyncEngine.ScanSelection(all, null).Select(l => l.Id));
        Assert.Equal(["b"], SyncEngine.ScanSelection(all, ["b", "gone"]).Select(l => l.Id));
        Assert.Empty(SyncEngine.ScanSelection(all, []));
    }

    [Fact]
    public async Task Scan_WithoutScanner_Fails_AndReleasesTheLock()
    {
        using var h = await Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.RunAsync(SyncMode.ScanLibrary, null, CancellationToken.None));
        Assert.False(h.Engine.Status.Running);
        Assert.False((await h.Sync()).AlreadyRunning);
    }
}

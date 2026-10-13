using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SpotiSync.State;

/// <summary>
/// Small JSON files under the plugin data folder: catalog.json, overrides.json, auth.json and
/// playlists/&lt;id&gt;.json. Writes are atomic (temp file + rename). Nothing is cached in
/// memory between calls.
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="StateStore"/> class.</summary>
    /// <param name="root">Data directory.</param>
    public StateStore(string root)
    {
        _root = root;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Gets the data directory.</summary>
    public string Root => _root;

    /// <summary>Gets a lock callers hold while doing read-modify-write of shared files.</summary>
    public SemaphoreSlim Gate => _gate;

    /// <summary>Path of the catalog.</summary>
    public string CatalogPath => Path.Combine(_root, "catalog.json");

    /// <summary>Path of the overrides file.</summary>
    public string OverridesPath => Path.Combine(_root, "overrides.json");

    /// <summary>Path of the auth file.</summary>
    public string AuthPath => Path.Combine(_root, "auth.json");

    /// <summary>Path of a playlist file.</summary>
    /// <param name="id">Spotify playlist id.</param>
    /// <returns>The path.</returns>
    public string PlaylistPath(string id) => Path.Combine(_root, "playlists", SafeName(id) + ".json");

    /// <summary>Loads the catalog (empty when missing).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The catalog.</returns>
    public async Task<Catalog> LoadCatalogAsync(CancellationToken ct = default)
        => await LoadAsync(CatalogPath, StateJsonContext.Default.Catalog, ct).ConfigureAwait(false) ?? new Catalog();

    /// <summary>Loads overrides (empty when missing).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The overrides.</returns>
    public async Task<Overrides> LoadOverridesAsync(CancellationToken ct = default)
        => await LoadAsync(OverridesPath, StateJsonContext.Default.Overrides, ct).ConfigureAwait(false) ?? new Overrides();

    /// <summary>Loads auth (empty when missing).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The auth state.</returns>
    public async Task<AuthState> LoadAuthAsync(CancellationToken ct = default)
        => await LoadAsync(AuthPath, StateJsonContext.Default.AuthState, ct).ConfigureAwait(false) ?? new AuthState();

    /// <summary>Loads a playlist (null when never fetched).</summary>
    /// <param name="id">Spotify playlist id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The playlist state.</returns>
    public Task<PlaylistState?> LoadPlaylistAsync(string id, CancellationToken ct = default)
        => LoadAsync(PlaylistPath(id), StateJsonContext.Default.PlaylistState, ct);

    /// <summary>Saves the catalog.</summary>
    /// <param name="c">Catalog.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SaveCatalogAsync(Catalog c, CancellationToken ct = default) => SaveAsync(CatalogPath, c, StateJsonContext.Default.Catalog, ct);

    /// <summary>Saves overrides.</summary>
    /// <param name="o">Overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SaveOverridesAsync(Overrides o, CancellationToken ct = default) => SaveAsync(OverridesPath, o, StateJsonContext.Default.Overrides, ct);

    /// <summary>Saves auth.</summary>
    /// <param name="a">Auth state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SaveAuthAsync(AuthState a, CancellationToken ct = default) => SaveAsync(AuthPath, a, StateJsonContext.Default.AuthState, ct);

    /// <summary>Saves a playlist.</summary>
    /// <param name="p">Playlist state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SavePlaylistAsync(PlaylistState p, CancellationToken ct = default) => SaveAsync(PlaylistPath(p.Id), p, StateJsonContext.Default.PlaylistState, ct);

    /// <summary>Deletes a playlist file if present.</summary>
    /// <param name="id">Spotify playlist id.</param>
    public void DeletePlaylist(string id)
    {
        var p = PlaylistPath(id);
        if (File.Exists(p))
        {
            File.Delete(p);
        }
    }

    /// <summary>Weak ETag of a file from its size and write time, or null when missing.</summary>
    /// <param name="path">File path.</param>
    /// <returns>ETag value including quotes.</returns>
    public static string? ETagOf(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? "W/\"" + fi.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "-" + fi.LastWriteTimeUtc.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\"" : null;
    }

    private static async Task<T?> LoadAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (fs.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(fs, type, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            // A corrupt file is treated as missing; the next save replaces it.
            return null;
        }
    }

    private static async Task SaveAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous);
        await using (fs.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(fs, value, type, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeName(string id)
    {
        Span<char> buf = stackalloc char[Math.Min(id.Length, 100)];
        for (int i = 0; i < buf.Length; i++)
        {
            char c = id[i];
            buf[i] = char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }

        return new string(buf);
    }
}

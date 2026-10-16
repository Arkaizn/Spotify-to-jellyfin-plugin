# SpotiSync for Jellyfin

A Jellyfin server plugin that syncs your Spotify playlists (and Liked Songs) into Jellyfin
playlists, using the music you already have in your library.

- Fuzzy matching of Spotify tracks to library songs (title cleaning, live/remix/acoustic
  awareness, artist splitting, duration, ISRC when available).
- Per playlist: **Mirror** (Spotify order, removes songs that left) or **Add only**.
- Takes over an existing Jellyfin playlist with the same name.
- Manual overrides: pick the right song, ignore a track, accept suggestions (one by one or all).
- "Missing" lists: plain `https://open.spotify.com/track/...` links of songs you don't have.
- Scheduled task **SpotiSync: sync playlists** (every 6 hours by default) and **SpotiSync: rematch only**
  (no Spotify calls).
- Dashboard page in the sidebar under *Plugins*, light/dark theme, mobile friendly.

Requires **Jellyfin 12.2** (the plugin targets .NET 10 / ABI 12.2.0.0).

## Install

1. In Jellyfin go to **Dashboard → Plugins → Repositories** (or *Catalog → ⚙*) and add a repository:
   - Name: `SpotiSync`
   - URL: `https://raw.githubusercontent.com/Arkaizn/Spotify-to-jellyfin-plugin/main/manifest.json`
2. Open **Catalog**, find **SpotiSync**, install it and restart Jellyfin.
3. **SpotiSync** now appears in the dashboard sidebar under *Plugins*.

## No Premium? Use public links or your data export

Since 2026 Spotify only lets self-made apps work when the app owner has Premium. Without it you
can still sync, without connecting an account at all:

- **Public playlist link:** make the playlist public in Spotify (you can keep editing it on your
  phone), copy its link (Share → Copy link), paste it into **Add playlist** on the SpotiSync page.
  It is re-read on every sync. Limit: Spotify's public page shows only the **first 100 songs**.
- **Import Spotify data:** in Spotify go to Account → Privacy → *Download your data*. When the
  e-mail arrives (it can take a few days), unzip it and upload `Playlist1.json` (and
  `Playlist2.json`, …, plus `YourLibrary.json` for liked songs) with **Import Spotify data**.
  No song limit, but updates need a new export.

**Jellyfin playlists built from several Spotify playlists:** on the *Playlists* tab every entry
is one Jellyfin playlist. Create one (name + Mirror / Add only), open it and add its sources: public
links, imported files or playlists of your connected account. The songs of all sources are combined
in the order shown (↑ ↓ to change it), each song once. The song list shows where every song comes
from, in tabs *In Jellyfin*, *Duplicates (skipped)*, *Need a look*, *Not found* and *Ignored*.

- *More than 100 songs without Premium:* keep "Rock 1", "Rock 2", … (up to 100 songs each) public on
  Spotify and add all of them to one Jellyfin playlist "Rock".
- *Inbox:* add a small public "Rock inbox" playlist to a Jellyfin playlist in *Add only* mode. Once a
  song is synced you can remove it from the inbox on your phone; Jellyfin keeps it. Seed the
  playlist once with **Import Spotify data**.
- If a source can't be read during a sync, the playlist only gains songs that time, never loses any.
- Renaming the entry renames the Jellyfin playlist; deleting it keeps the Jellyfin playlist.

Older setups (playlists combined through a shared "Jellyfin playlist" name) are converted
automatically.

Both kinds of playlists get the same matching, overrides and missing lists. The × next to them
removes them from SpotiSync (the Jellyfin playlist stays).

## Spotify app setup

SpotiSync logs in with your own Spotify app (Authorization Code + PKCE, so no client secret
is needed or stored).

1. Open the SpotiSync page in Jellyfin → **Settings**. Copy the **Redirect URI** shown there
   (it looks like `https://your-jellyfin/SpotiSync/Callback`).
   - Spotify only accepts `https://` redirect URIs, or `http://127.0.0.1:...` (loopback).
     If you reach Jellyfin over plain http on your LAN, put the public https address of your
     server (e.g. behind a reverse proxy) into **Public Jellyfin URL** and save.
2. Go to <https://developer.spotify.com/dashboard> → **Create app**.
   - App name / description: anything (e.g. "SpotiSync").
   - Redirect URI: paste the URI from step 1 exactly.
   - APIs used: **Web API**.
3. Open the app's **Settings**, copy the **Client ID** into SpotiSync → Settings.
4. Choose the Jellyfin user the playlists should belong to, then **Save**.
5. Click **Connect Spotify** and approve. If Spotify asks you to, add your Spotify account in the
   app's *User Management* first.
6. Back on the *Playlists* tab press **Sync now** once to load your playlists, switch on the ones
   you want, and press **Sync now** again.

Scopes requested: `playlist-read-private playlist-read-collaborative user-library-read`.

Since Spotify's February 2026 API changes, playlist contents are only returned for playlists you
**own or collaborate on**. Other playlists are listed greyed out. Copy their songs into a playlist
of your own on Spotify if you want to sync them.

## Using it

- **Playlists tab:** toggle a playlist to sync it and choose *Mirror* or *Add only*. The bar shows
  in Jellyfin (green), need a look (orange), not found (red) and ignored (grey).
- **Click a playlist** to see its songs in four tabs:
  - **In Jellyfin:** the matched song and its score. *Find* picks another song, *Ignore* skips it.
  - **Need a look:** a likely but uncertain match. *Accept* it, or use **Accept all suggestions**.
  - **Not found:** *Find* lets you search the library; the Spotify title links to the track.
  - **Ignored:** *Unignore* to bring it back.
- Overrides apply to that Spotify track everywhere and on every sync. Changes are applied right
  away by a quick rematch that does not contact Spotify.
- **Copy missing links** copies the Spotify links of songs that are not in your library, either
  for one playlist or for all synced playlists.
- Automatic syncs run from **Dashboard → Scheduled Tasks → SpotiSync: sync playlists**.

## How matching works

Titles are normalized (case, accents, punctuation, `&` → `and`) and cleaned: suffixes such as
"- Remastered 2011", "(feat. X)" and "[Radio Edit]" are removed. Version markers that make a
*different recording* (live, remix, acoustic, instrumental, demo, extended, sped up, slowed, …)
are kept as tags; a live album name also counts as "live". The score is

`0.50 · title + 0.30 · artist + 0.15 · duration + 0.05 · album`

- **Title:** the best of the Levenshtein ratio, token-sort and (damped) token-set ratio.
- **Artist:** the primary Spotify artist against all track and album artists. Featured artists
  are split out, and "Various Artists" is ignored.
- **Penalties:** 0.20 per differing version tag (at most 0.40), and 0.25 when the numbers in the
  title differ ("Part 1" vs "Part 2").
- **Caps:** a title below 0.60 can't score above 0.55, and an artist below 0.50 can't score above
  0.75.
- **Results:** a score of ≥ 0.82 is *In Jellyfin*, ≥ 0.60 is *Need a look*, anything lower is
  *Not found*. An identical ISRC is an instant match; Jellyfin 12.2 has no ISRC field, so this
  only works if a metadata plugin stores one as an `ISRC` provider id.

## Resource use

SpotiSync is built to stay out of the way of playback:

- **No background work:** no hosted services, timers or polling of its own. It runs only when
  the scheduled task fires or you press a button. The dashboard polls status only while a sync
  runs and the browser tab is visible.
- **Library index:** built at the start of a sync, kept for 5 minutes after the last
  sync, rematch or search (so the Find dialog stays fast), then dropped.
- **Spotify traffic:**
  - One `/me/playlists` listing per sync.
  - A playlist is only downloaded again when its `snapshot_id` changed. Liked Songs costs one
    small request to detect changes.
  - `fields` filtering on `/items`, gzip/brotli, `limit=50` paging.
  - 429 `Retry-After`: it waits up to 90 s; for longer waits it stops fetching, remembers the
    deadline, and keeps working from the cached track lists.
- **Incremental matching:** a track is only scored when it is new, its override changed, it
  isn't matched yet and the library changed, or its matched song disappeared. Library changes are
  detected from the count of audio items, the newest DateCreated, and items saved since the last
  check.
- **Minimal playlist writes:**
  - Nothing is written when the content is already right.
  - Otherwise: one remove, one append, and the fewest moves (longest increasing subsequence).
  - In Jellyfin 12.2 every single *move* rewrites the whole playlist, so when more than 8 moves
    are needed (or the diff is larger than the playlist) one full replace is used instead.
- **Small state files:** atomic JSON files (temp file + rename) under
  `<config>/plugins/configurations/SpotiSync/`, written only when they change.
- **Logging:** one Information line per playlist; per-track details only at Debug level.

Benchmarks (synthetic library, Release build, `SPOTISYNC_BENCH=1`; see `BenchmarkTests.cs`):

| Budget | Target | Measured |
| --- | --- | --- |
| Index build, 50,000 songs | < 1 s, < 50 MB | ~0.5 s, 14 MB retained, ~41 MB peak heap growth, 0 MB after release |
| Match 10 playlists × 500 tracks | < 2 s CPU | ~0.9 s wall, ~1.4 s process CPU |
| Sync with nothing changed | no writes, < 200 ms | 0 playlist writes, 0 file writes, ~80 ms, index not built |

The index-build figure excludes Jellyfin's own database read of the songs, which depends on your
server and disk.

Measured in a real Jellyfin 12.2 container (2,000-song library, Spotify replaced by a local mock,
`docker stats`):

| | Result |
| --- | --- |
| Idle, 5 min after restart, without plugin | CPU avg 0.50 % (Jellyfin's own spikes up to 14 %), RAM 149 MiB |
| Idle, 5 min after restart, with plugin | CPU avg 0.29 %, RAM 135 MiB; no difference beyond run-to-run noise |
| Library read + index build | 133 ms warm, of which 111 ms is Jellyfin's database read (~55 µs per song) |
| First sync (3 playlists, 330 tracks, 3 playlists created) | 2.7 s, mostly Jellyfin creating the playlists |
| Sync with nothing changed | 0.1 s; Spotify calls: 1 playlist listing + 1 Liked Songs check |

At about 55 µs per song, a 50,000-song library spends roughly 3 s in Jellyfin's database while
the index is built. That only happens when there is something to match (new tracks, or a changed
library), never on a sync where nothing changed.

For end-to-end tests the Spotify hosts can be redirected with the `SPOTISYNC_SPOTIFY_API_URL`
and `SPOTISYNC_SPOTIFY_ACCOUNTS_URL` environment variables. Leave them unset in normal use.

## Building

```sh
dotnet build -c Release
dotnet test
SPOTISYNC_BENCH=1 dotnet test -c Release --filter Category=Benchmark --logger "console;verbosity=detailed"
```

The plugin is `Jellyfin.Plugin.SpotiSync/bin/Release/net10.0/Jellyfin.Plugin.SpotiSync.dll`. To
install it by hand, copy it into `<config>/plugins/SpotiSync_1.0.0.0/` and restart Jellyfin.

## Releasing

Push a tag such as `v1.0.0`; an annotated tag's message becomes the changelog. The *Release*
workflow:

1. Builds and tests, including the benchmark budgets.
2. Zips the DLL with a `meta.json` and creates a GitHub release with the zip.
3. Adds a version entry (version, changelog, targetAbi, sourceUrl, MD5 checksum, timestamp) to
   `manifest.json` on `main`.

If `main` is protected, allow GitHub Actions to push to it.

## License

MIT, see [LICENSE](LICENSE).

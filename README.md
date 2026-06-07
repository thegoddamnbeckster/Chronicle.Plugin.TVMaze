# Chronicle.Plugin.TVMaze

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TVMaze?style=flat-square&label=release&color=CF0000)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TVMaze/releases/latest)

TV series, season, and episode metadata for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) powered by [TVMaze](https://www.tvmaze.com/).

**No API key required.** TVMaze's public API is completely open — install and go.

Covers titles, overviews, posters, backdrops, genres, cast, ratings, and episode air dates for TV and Anime. Episode and season coverage matches what Sonarr and other *arr apps use.

---

## How It Works

TVMaze is a community TV database with a fully open REST API. This plugin:

1. **Fast-paths via cross-reference IDs** — if an item was synced from Trakt or SIMKL (which store TVDB IDs), the plugin resolves it immediately via `GET /lookup/shows?thetvdb={id}` without a text search.
2. **Falls back to text search** — `GET /search/shows?q={name}` with year scoring for items that have no cross-reference IDs.
3. **Writes TVDB cross-reference back** — TVMaze show responses include `externals.thetvdb`; the plugin stores this so Fanart.tv can use TVDB IDs for TV artwork even if TheTVDB plugin is not installed.

| Level | TVMaze Endpoints Used |
|-------|-----------------------|
| Show (level 0) | `GET /shows/{id}?embed[]=cast&embed[]=images` |
| Season (level 1) | `GET /shows/{id}/seasons` — match by season number |
| Episode (level 2) | `GET /seasons/{seasonId}/episodes` — match by episode number or title fallback |

---

## Supported Media Types

| Media Type | Levels | Fields |
|------------|--------|--------|
| `tv` | Show, Season, Episode | title, overview, year, poster, backdrop, banner, genres, cast, rating, runtime |
| `anime` | Show, Season, Episode | title, overview, year, poster, backdrop, banner, genres, cast, rating, runtime |

Movies are not supported — use the TMDB plugin for those.

---

## External ID Format

This plugin stores IDs in the following formats:

| Level | Format | Example |
|-------|--------|---------|
| Show | `show:{tvmazeId}` | `show:169` |
| Season | `show:{tvmazeId}/season:{n}` | `show:169/season:2` |
| Episode | `episode:{tvmazeId}` | `episode:4952` |

**Fix Match:** enter any of the above formats, a TVMaze URL, a TVDB ID prefixed with `thetvdb:` (e.g. `thetvdb:76290`), or an IMDB ID (e.g. `tt0903747`). TVMaze IDs take precedence; TVDB and IMDB IDs are resolved via lookup.

---

## Installation

1. Build the plugin:
   ```powershell
   dotnet build -c Release
   ```

2. Copy `bin\Release\net9.0\*.dll` and `manifest.json` into your Chronicle `plugins\chronicle.plugin.tvmaze\` directory.

3. Enable the plugin in Chronicle → Plugins.

No configuration needed — the plugin works immediately after enabling.

---

## Configuration

None. TVMaze's public API requires no authentication.

---

## Dependencies

TVMaze works as a standalone metadata source. It integrates with the enrichment pipeline as follows:

- **For best episode coverage:** TVMaze fills gaps that TMDB sometimes leaves for older or non-mainstream shows.
- **Recommended enrichment order:**
  1. Trakt / SIMKL sync (stores TVDB cross-reference IDs — TVMaze uses these for instant lookup)
  2. TMDB — Fetch Missing Metadata
  3. **TVMaze — Fetch Missing TV Metadata**
  4. Fanart.tv — Fetch Missing Artwork (uses TVDB IDs written back by TVMaze)

TVMaze and TheTVDB cover the same media types. You can run both — use Metadata Assignment to control which plugin wins per field.

---

## Development

Both repositories must be cloned as siblings:

```
<base>\
  Chronicle\
  Chronicle.Plugin.TVMaze\
```

The plugin references `Chronicle.Plugins` via a local project reference marked `Private="false"` so the host's copy is used at runtime rather than a copy in the plugin output directory.

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.tvmaze"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```

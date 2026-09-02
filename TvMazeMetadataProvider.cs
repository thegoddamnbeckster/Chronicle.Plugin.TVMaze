using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Chronicle.Plugin.TVMaze;

/// <summary>
/// Chronicle metadata provider for TVMaze.
/// No API key required — the TVMaze public API is fully open.
///
/// Search strategy by hierarchy level:
///   0 (Show)    — TVDB/IMDB cross-ref lookup → text search + year scoring
///   1 (Season)  — resolve parent show; match season by number
///   2 (Episode) — resolve parent show + season; match episode by number, title fallback
///
/// External ID formats stored (source = "tvmaze"):
///   show:{tvmazeId}                — TV show
///   show:{tvmazeId}/season:{n}     — season
///   episode:{tvmazeId}             — episode
///
/// Cross-reference IDs read from KnownExternalIds:
///   "tvdb"           — raw numeric TVDB ID stored by Trakt/SIMKL ("76290")
///   "imdb"           — IMDB ID stored by TMDB/Trakt ("tt0903747")
///   "tvmaze"         — own stored show/episode ID
///   "parent_tvmaze"  — parent show's stored ID (injected by enrichment pipeline)
///   "parent_tvdb"    — parent show's raw TVDB ID
/// </summary>
public sealed class TvMazeMetadataProvider : IMetadataProvider, IDisposable
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.tvmaze";
    public string Name     => "TVMaze";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Live state ────────────────────────────────────────────────────────────

    private TvMazeClient? _client;
    private HttpClient?   _ownedHttp;
    private readonly ILogger _logger;

    public TvMazeMetadataProvider()
        : this(NullLogger.Instance) { }

    public TvMazeMetadataProvider(ILogger logger) => _logger = logger;

    internal TvMazeMetadataProvider(TvMazeClient client)
        : this(NullLogger.Instance)
    {
        _client = client;
    }

    // ── Supported types ───────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] _supportedTypes =
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "tv",
            DisplayName     = "TV",
            HierarchyLevels = 3,
            HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = 15,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "banner_url", "genres", "cast", "rating", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url"],
                [2] = ["title", "overview", "year", "runtime_minutes", "rating", "cast"],
            },
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "anime",
            DisplayName     = "Anime",
            HierarchyLevels = 3,
            HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = 15,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "banner_url", "genres", "cast", "rating", "tags"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url"],
                [2] = ["title", "overview", "year", "runtime_minutes", "rating", "cast"],
            },
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => _supportedTypes;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new() { Settings = [] };

    // ── Configuration ─────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        // Dispose the old client first so its SemaphoreSlim is not leaked.
        _client?.Dispose();
        _ownedHttp?.Dispose();
        var http = new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "Chronicle/1.0" } },
        };
        _ownedHttp = http;
        _client    = new TvMazeClient(http, _logger);
        _logger.LogInformation("TVMaze plugin configured (no API key required)");
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        return context.HierarchyLevel switch
        {
            0 => await SearchShowAsync(context, ct).ConfigureAwait(false),
            1 => await SearchSeasonAsync(context, ct).ConfigureAwait(false),
            2 => await SearchEpisodeAsync(context, ct).ConfigureAwait(false),
            _ => [],
        };
    }

    // Level 0 — TV Show ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchShowAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        // 1. Cross-reference: TVDB ID (from Trakt/SIMKL)
        if (context.KnownExternalIds?.TryGetValue("tvdb", out var tvdbRaw) == true
            && long.TryParse(tvdbRaw, out var tvdbId))
        {
            var show = await _client!.LookupByTvdbIdAsync(tvdbId, ct).ConfigureAwait(false);
            if (show is not null)
                return [new ScoredCandidate(await FullShowMetadata(show, ct), 100, "TVDB cross-ref")];
        }

        // 2. Cross-reference: IMDB ID
        if (context.KnownExternalIds?.TryGetValue("imdb", out var imdb) == true
            && !string.IsNullOrWhiteSpace(imdb))
        {
            var show = await _client!.LookupByImdbIdAsync(imdb, ct).ConfigureAwait(false);
            if (show is not null)
                return [new ScoredCandidate(await FullShowMetadata(show, ct), 100, "IMDB cross-ref")];
        }

        // 3. Own stored ID
        if (ExtractShowId(context.KnownExternalIds) is { } ownId)
        {
            var show = await _client!.GetShowAsync(ownId, ct).ConfigureAwait(false);
            if (show is not null)
                return [new ScoredCandidate(MapShow(show), 100, "own stored ID")];
        }

        // 4. Text search
        var results = await _client!.SearchShowsAsync(context.Name, ct).ConfigureAwait(false);
        if (results is null or { Length: 0 }) return [];

        bool isAnimeSearch = string.Equals(
            context.MediaTypeName, "anime", StringComparison.OrdinalIgnoreCase);

        var candidates = new List<ScoredCandidate>();
        foreach (var r in results.Take(5))
        {
            // When the user searches the Anime tab, only include shows TVMaze classifies
            // as "Anime" — otherwise non-anime TV shows bleed into anime results.
            if (isAnimeSearch &&
                !string.Equals(r.Show.Type, "Anime", StringComparison.OrdinalIgnoreCase))
                continue;

            var score = ScoreSearchResult(r.Show, context);
            if (score < 40) continue;
            candidates.Add(new ScoredCandidate(MapShow(r.Show), score, "text search"));
        }

        // Sort first so we enrich the highest-scoring candidate, not just whichever
        // TVMaze happened to return first in its own relevance order.
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Enrich the top candidate with embedded cast/images (single extra request).
        if (candidates.Count > 0)
        {
            var top = candidates[0];
            var topExtId = top.Metadata.ExternalId ?? string.Empty;
            var topIdStr = topExtId.StartsWith("show:", StringComparison.OrdinalIgnoreCase)
                ? topExtId[5..] : topExtId;
            if (int.TryParse(topIdStr, out var showId))
            {
                var full = await _client.GetShowAsync(showId, ct).ConfigureAwait(false);
                if (full is not null)
                    candidates[0] = top with { Metadata = MapShow(full) };
            }
        }

        return candidates;
    }

    // Level 1 — Season ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchSeasonAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        var showId = await ResolveShowIdAsync(context.KnownExternalIds, ct).ConfigureAwait(false);
        if (showId is null)
        {
            _logger.LogDebug("TVMaze: no show ID available for season '{Name}' — skipping", context.Name);
            return [];
        }

        if (!context.ItemNumber.HasValue)
        {
            _logger.LogDebug("TVMaze: no season number in context for show {Id}", showId);
            return [];
        }

        var seasons = await _client!.GetSeasonsAsync(showId.Value, ct).ConfigureAwait(false);
        var season  = seasons?.FirstOrDefault(s => s.Number == context.ItemNumber.Value);

        if (season is null)
        {
            _logger.LogDebug("TVMaze: season {N} not found for show {Id}",
                context.ItemNumber, showId);
            return [];
        }

        return [new ScoredCandidate(MapSeason(season, showId.Value), 100, "season number match")];
    }

    // Level 2 — Episode ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchEpisodeAsync(
        MediaSearchContext context, CancellationToken ct)
    {
        var showId = await ResolveShowIdAsync(context.KnownExternalIds, ct).ConfigureAwait(false);
        if (showId is null)
        {
            _logger.LogDebug("TVMaze: no show ID for episode '{Name}' — skipping", context.Name);
            return [];
        }

        var seasonNumber = ExtractParentSeasonNumber(context.KnownExternalIds);
        if (!seasonNumber.HasValue)
        {
            _logger.LogDebug("TVMaze: no season number for episode '{Name}' (show {Id})",
                context.Name, showId);
            return [];
        }

        // Find the TVMaze season record to get the season's internal ID
        var seasons = await _client!.GetSeasonsAsync(showId.Value, ct).ConfigureAwait(false);
        var season  = seasons?.FirstOrDefault(s => s.Number == seasonNumber.Value);
        if (season is null)
        {
            _logger.LogDebug("TVMaze: season {N} not found for show {Id}",
                seasonNumber, showId);
            return [];
        }

        var episodes = await _client.GetEpisodesForSeasonAsync(season.Id, ct).ConfigureAwait(false);
        if (episodes is null or { Length: 0 }) return [];

        // Primary: match by episode number
        TvMazeEpisode? match     = null;
        var            scoreReason = string.Empty;

        if (context.ItemNumber.HasValue)
        {
            match = episodes.FirstOrDefault(e => e.Number == context.ItemNumber.Value);
            if (match is not null)
                scoreReason = $"S{seasonNumber:D2}E{context.ItemNumber:D2} match";
        }

        // Fallback: title match (handles TVMaze/TMDB numbering divergence).
        // Use bidirectional containment to tolerate minor title differences in either direction.
        if (match is null && !string.IsNullOrWhiteSpace(context.Name))
        {
            var normContext = NormaliseName(context.Name);
            match = episodes.FirstOrDefault(e =>
            {
                var normEp = NormaliseName(e.Name);
                return normEp.Equals(normContext, StringComparison.Ordinal)
                    || normEp.Contains(normContext, StringComparison.Ordinal)
                    || normContext.Contains(normEp, StringComparison.Ordinal);
            });

            if (match is not null)
            {
                _logger.LogWarning(
                    "TVMaze: episode S{S:D2}E{E:D2} '{Name}' not found by number for show {Id} — " +
                    "matched by title fallback. Matched: '{Matched}'",
                    seasonNumber, context.ItemNumber, context.Name, showId, match.Name);
                scoreReason = "title fallback (numbering mismatch)";
            }
        }

        if (match is null)
        {
            _logger.LogDebug("TVMaze: no episode match for '{Name}' in S{S} show {Id}",
                context.Name, seasonNumber, showId);
            return [];
        }

        return [new ScoredCandidate(MapEpisode(match), 100, scoreReason)];
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        externalId = NormaliseTvMazeUrl(externalId.Trim());

        // thetvdb:{id} cross-ref
        if (externalId.StartsWith("thetvdb:", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(externalId[8..], out var tvdbId))
        {
            var show = await _client!.LookupByTvdbIdAsync(tvdbId, ct).ConfigureAwait(false);
            if (show is not null) return await FullShowMetadata(show, ct);
            return EmptyResult(externalId);
        }

        // imdb: or tt... cross-ref
        if (externalId.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
            externalId = externalId[5..];
        if (externalId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && externalId.Length > 2 && externalId[2..].All(char.IsDigit))
        {
            var show = await _client!.LookupByImdbIdAsync(externalId, ct).ConfigureAwait(false);
            if (show is not null) return await FullShowMetadata(show, ct);
            _logger.LogDebug("TVMaze: no show found for IMDB ID '{Id}'", externalId);
            return EmptyResult(externalId);
        }

        // episode:{id}
        if (externalId.StartsWith("episode:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(externalId[8..], out var epId))
        {
            var ep = await _client!.GetEpisodeAsync(epId, ct).ConfigureAwait(false);
            return ep is not null ? MapEpisode(ep) : EmptyResult(externalId);
        }

        // show:{id}/season:{n}
        var seasonMatch = _seasonIdRe.Match(externalId ?? string.Empty);
        if (seasonMatch.Success
            && int.TryParse(seasonMatch.Groups[1].Value, out var sid)
            && int.TryParse(seasonMatch.Groups[2].Value, out var sn))
        {
            var seasons = await _client!.GetSeasonsAsync(sid, ct).ConfigureAwait(false);
            var season  = seasons?.FirstOrDefault(s => s.Number == sn);
            if (season is null) return EmptyResult(externalId ?? string.Empty);
            return MapSeason(season, sid);
        }

        // show:{id} or bare numeric
        var exId = externalId ?? string.Empty;
        var showIdStr = exId.StartsWith("show:", StringComparison.OrdinalIgnoreCase)
            ? exId[5..] : exId;

        if (int.TryParse(showIdStr, out var showId))
        {
            var show = await _client!.GetShowAsync(showId, ct).ConfigureAwait(false);
            return show is not null ? MapShow(show) : EmptyResult(exId);
        }

        return EmptyResult(exId);
    }

    private static MediaMetadata EmptyResult(string externalId) =>
        new() { ExternalId = externalId, Source = "tvmaze" };

    // ── Image + health ────────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        // TVMaze image CDN URLs are public and require no auth — fetch directly via the client's HttpClient.
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            _logger.LogWarning("TVMaze health check skipped — plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = null;
        _client    = null;
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private async Task<MediaMetadata> FullShowMetadata(TvMazeShow show, CancellationToken ct)
    {
        // If the show already has embedded data, use it directly
        if (show.Embedded is not null)
            return MapShow(show);

        // Otherwise fetch with embeds
        var full = await _client!.GetShowAsync(show.Id, ct).ConfigureAwait(false);
        return full is not null ? MapShow(full) : MapShow(show);
    }

    private static MediaMetadata MapShow(TvMazeShow show)
    {
        var cast = show.Embedded?.Cast?
            .Where(c => c.Person?.Name is not null)
            .Select(c => new CastMember(c.Person!.Name!, c.Character?.Name))
            .Take(20)
            .ToList() ?? [];

        var backdropUrl = BestArtwork(show.Embedded?.Images, "background");
        var bannerUrl   = BestArtwork(show.Embedded?.Images, "banner");

        var extra = new Dictionary<string, object?>();
        if (show.Network?.Name is not null)         extra["network"]  = show.Network.Name;
        if (show.Status is not null)                extra["status"]   = show.Status;
        if (show.Type is not null)                  extra["type"]     = show.Type;
        if (show.Language is not null)              extra["language"] = show.Language;
        if (show.Runtime.HasValue)                  extra["runtime"]  = show.Runtime;
        if (show.Externals?.TheTvdb.HasValue == true) extra["tvdb"]  = show.Externals.TheTvdb;
        if (show.Externals?.Imdb is not null)       extra["imdb"]    = show.Externals.Imdb;

        return new MediaMetadata
        {
            ExternalId   = $"show:{show.Id}",
            Source       = "tvmaze",
            Title        = show.Name,
            Overview     = StripHtml(show.Summary),
            Year         = ParseYear(show.Premiered),
            PosterUrl    = show.Image?.Original ?? show.Image?.Medium,
            BackdropUrl  = backdropUrl,
            BannerUrl    = bannerUrl,
            Genres       = show.Genres?.ToList() ?? [],
            Cast         = cast,
            Crew         = [],   // TVMaze API doesn't expose crew credits
            Rating       = show.Rating?.Average,
            ExtendedData = extra.Count > 0
                ? JsonSerializer.SerializeToElement(extra)
                : null,
        };
    }

    private static MediaMetadata MapSeason(TvMazeSeason season, int showId) =>
        new()
        {
            ExternalId = $"show:{showId}/season:{season.Number}",
            Source     = "tvmaze",
            Title      = string.IsNullOrWhiteSpace(season.Name)
                ? $"Season {season.Number}"
                : season.Name,
            Overview   = StripHtml(season.Summary),
            Year       = ParseYear(season.PremiereDate),
            PosterUrl  = season.Image?.Original ?? season.Image?.Medium,
        };

    private static MediaMetadata MapEpisode(TvMazeEpisode episode) =>
        new()
        {
            ExternalId     = $"episode:{episode.Id}",
            Source         = "tvmaze",
            Title          = episode.Name ?? string.Empty,
            Overview       = StripHtml(episode.Summary),
            Year           = ParseYear(episode.Airdate),
            RuntimeMinutes = episode.Runtime,
            Rating         = episode.Rating?.Average,
            PosterUrl      = episode.Image?.Original ?? episode.Image?.Medium,
            Cast           = [],
            Crew           = [],
        };

    // ── Artwork selection ─────────────────────────────────────────────────────

    private static string? BestArtwork(TvMazeArtwork[]? artworks, string type)
    {
        if (artworks is null or { Length: 0 }) return null;
        var typed = artworks.Where(a => string.Equals(a.Type, type, StringComparison.OrdinalIgnoreCase))
                            .ToList();
        // Prefer main image, then largest original
        var best = typed.FirstOrDefault(a => a.Main)
                ?? typed.FirstOrDefault();
        return best?.Resolutions?.Original?.Url
            ?? best?.Resolutions?.Medium?.Url;
    }

    // ── ID resolution ─────────────────────────────────────────────────────────

    private async Task<int?> ResolveShowIdAsync(
        IReadOnlyDictionary<string, string>? ids, CancellationToken ct)
    {
        // Own stored show ID: "show:169" or "show:169/season:2"
        if (ExtractShowId(ids) is { } direct)
            return direct;

        // Parent's stored show ID (injected by enrichment pipeline)
        if (ids?.TryGetValue("parent_tvmaze", out var parentOwn) == true)
        {
            if (int.TryParse(ExtractShowIdString(parentOwn), out var pid))
                return pid;
        }

        // Parent raw TVDB ID → lookup
        long? triedTvdbId = null;
        if (ids?.TryGetValue("parent_tvdb", out var parentTvdb) == true
            && long.TryParse(parentTvdb, out var tvdbId))
        {
            triedTvdbId = tvdbId;
            var show = await _client!.LookupByTvdbIdAsync(tvdbId, ct).ConfigureAwait(false);
            if (show is not null) return show.Id;
        }

        // Own TVDB cross-ref (for show-level context passed down to child).
        // Skip if it's the same ID we already tried above to avoid a redundant request.
        if (ids?.TryGetValue("tvdb", out var tvdbRaw) == true
            && long.TryParse(tvdbRaw, out var tvdbId2)
            && tvdbId2 != triedTvdbId)
        {
            var show = await _client!.LookupByTvdbIdAsync(tvdbId2, ct).ConfigureAwait(false);
            if (show is not null) return show.Id;
        }

        return null;
    }

    private static int? ExtractShowId(IReadOnlyDictionary<string, string>? ids)
    {
        if (ids?.TryGetValue("tvmaze", out var own) != true) return null;
        var idStr = ExtractShowIdString(own);
        return int.TryParse(idStr, out var n) ? n : null;
    }

    private static string? ExtractShowIdString(string? value)
    {
        if (value is null) return null;
        var str = value.StartsWith("show:", StringComparison.OrdinalIgnoreCase)
            ? value[5..] : value;
        var slash = str.IndexOf('/');
        return slash > 0 ? str[..slash] : str;
    }

    private static int? ExtractParentSeasonNumber(IReadOnlyDictionary<string, string>? ids)
    {
        if (ids?.TryGetValue("parent_tvmaze", out var parentOwn) != true) return null;
        var m = _seasonIdRe.Match(parentOwn ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[2].Value, out var sn) ? sn : null;
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static int ScoreSearchResult(TvMazeShow show, MediaSearchContext context)
    {
        var score = 0;

        if (string.Equals(NormaliseName(show.Name), NormaliseName(context.Name),
                StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (NormaliseName(show.Name).Contains(NormaliseName(context.Name),
                     StringComparison.OrdinalIgnoreCase))
            score += 35;

        if (context.Year.HasValue)
        {
            var year = ParseYear(show.Premiered);
            if (year.HasValue && Math.Abs(year.Value - context.Year.Value) <= 1)
                score += 30;
        }
        else
        {
            score += 10;
        }

        return Math.Min(score, 99);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static readonly Regex _seasonIdRe =
        new(@"show:(\d+)/season:(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _tvMazeShowUrlRe =
        new(@"tvmaze\.com/shows/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _htmlTagRe =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private static string NormaliseTvMazeUrl(string id)
    {
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return id;
        var m = _tvMazeShowUrlRe.Match(id);
        return m.Success ? $"show:{m.Groups[1].Value}" : id;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return _htmlTagRe.Replace(html, "").Trim();
    }

    private static int? ParseYear(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        return DateTime.TryParse(dateStr, out var d) ? d.Year : null;
    }

    private static string NormaliseName(string? s)
        => (s ?? string.Empty)
            .ToLowerInvariant()
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(":", "")
            .Trim();

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "TVMaze plugin is not configured. Call Configure() first.");
    }
}

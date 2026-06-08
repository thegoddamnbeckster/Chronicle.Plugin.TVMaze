using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.TVMaze;

/// <summary>
/// HTTP wrapper for the TVMaze public REST API.
/// No authentication required. Rate limit: 20 requests/10 seconds;
/// a 100ms inter-request delay keeps Chronicle well under the limit.
/// All public methods return null on 404; throw on other non-success status codes.
/// </summary>
internal sealed class TvMazeClient : IDisposable
{
    private const string BaseUrl = "https://api.tvmaze.com";

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient    _http;
    private readonly ILogger       _logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset         _lastRequest = DateTimeOffset.MinValue;

    public TvMazeClient(HttpClient http, ILogger logger)
    {
        _http   = http;
        _logger = logger;
    }

    // ── Rate-limited GET ──────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        bool released = false;
        try
        {
            var wait = MinInterval - (DateTimeOffset.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            _lastRequest = DateTimeOffset.UtcNow;

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryDelta = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                // Cap the delay at 60 s so a misbehaving server can't stall the pipeline indefinitely.
                var capped = retryDelta > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retryDelta;
                _logger.LogWarning("TVMaze: rate-limited; waiting {Secs}s before retry", capped.TotalSeconds);

                // Release before delaying so other callers aren't blocked during the wait,
                // then re-enter through the full rate-limited wrapper (MinInterval + semaphore).
                released = true;
                _rateLimiter.Release();
                await Task.Delay(capped, ct).ConfigureAwait(false);
                return await GetAsync(url, ct).ConfigureAwait(false);
            }

            return resp;
        }
        finally
        {
            if (!released)
                _rateLimiter.Release();
        }
    }

    private async Task<T?> FetchAsync<T>(string url, CancellationToken ct) where T : class
    {
        var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(_json, ct).ConfigureAwait(false);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public Task<TvMazeSearchResult[]?> SearchShowsAsync(string query, CancellationToken ct)
        => FetchAsync<TvMazeSearchResult[]>(
            $"{BaseUrl}/search/shows?q={Uri.EscapeDataString(query)}", ct);

    // ── Lookup by external ID ─────────────────────────────────────────────────

    public Task<TvMazeShow?> LookupByTvdbIdAsync(long tvdbId, CancellationToken ct)
        => FetchAsync<TvMazeShow>($"{BaseUrl}/lookup/shows?thetvdb={tvdbId}", ct);

    public Task<TvMazeShow?> LookupByImdbIdAsync(string imdbId, CancellationToken ct)
        => FetchAsync<TvMazeShow>($"{BaseUrl}/lookup/shows?imdb={Uri.EscapeDataString(imdbId)}", ct);

    // ── Show ──────────────────────────────────────────────────────────────────

    /// <summary>Fetches show with cast and images embedded in a single request.</summary>
    public Task<TvMazeShow?> GetShowAsync(int showId, CancellationToken ct)
        => FetchAsync<TvMazeShow>(
            $"{BaseUrl}/shows/{showId}?embed[]=cast&embed[]=images", ct);

    // ── Seasons ───────────────────────────────────────────────────────────────

    public Task<TvMazeSeason[]?> GetSeasonsAsync(int showId, CancellationToken ct)
        => FetchAsync<TvMazeSeason[]>($"{BaseUrl}/shows/{showId}/seasons", ct);

    // ── Episodes ──────────────────────────────────────────────────────────────

    public Task<TvMazeEpisode[]?> GetEpisodesForSeasonAsync(int seasonId, CancellationToken ct)
        => FetchAsync<TvMazeEpisode[]>($"{BaseUrl}/seasons/{seasonId}/episodes", ct);

    public Task<TvMazeEpisode?> GetEpisodeAsync(int episodeId, CancellationToken ct)
        => FetchAsync<TvMazeEpisode>($"{BaseUrl}/episodes/{episodeId}", ct);

    // ── Artwork ───────────────────────────────────────────────────────────────

    public Task<TvMazeArtwork[]?> GetArtworkAsync(int showId, CancellationToken ct)
        => FetchAsync<TvMazeArtwork[]>($"{BaseUrl}/shows/{showId}/images", ct);

    // ── Image download ────────────────────────────────────────────────────────

    /// <summary>Downloads raw image bytes from a direct URL (no rate-limiting needed for CDN).</summary>
    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct)
        => _http.GetByteArrayAsync(url, ct);

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // Breaking Bad — TVMaze ID 169
            var show = await GetShowAsync(169, ct).ConfigureAwait(false);
            return show is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVMaze health check failed");
            return false;
        }
    }

    public void Dispose() => _rateLimiter.Dispose();
}

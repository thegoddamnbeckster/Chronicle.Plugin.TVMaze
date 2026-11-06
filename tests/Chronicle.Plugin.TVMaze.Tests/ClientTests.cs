using System.Net;

namespace Chronicle.Plugin.TVMaze.Tests;

public class ClientTests
{
    private static TvMazeClient Client(StubHandler h) => new(new HttpClient(h), new CaptureLogger());

    [Fact]
    public async Task A_search_answer_is_read()
    {
        var c = Client(new StubHandler(_ => Reply.Json("[{\"score\":0.9,\"show\":{\"id\":1,\"name\":\"Bonding\"}}]")));
        var r = await c.SearchShowsAsync("bonding", default);
        Assert.Equal("Bonding", Assert.Single(r!).Show.Name);
    }

    [Fact]
    public async Task A_missing_show_is_null_not_an_error()
    {
        var c = Client(new StubHandler(_ => Reply.Status(HttpStatusCode.NotFound)));
        Assert.Null(await c.GetShowAsync(1, default));
    }

    [Fact]
    public async Task A_server_error_is_thrown_with_its_status()
    {
        var c = Client(new StubHandler(_ => Reply.Status(HttpStatusCode.InternalServerError)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => c.GetShowAsync(1, default));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Rate_limiting_that_never_clears_is_reported_as_a_rate_limit_after_a_bounded_wait()
    {
        // Retry-After 0 so the test does not sit waiting; the point is that it stops.
        HttpResponseMessage Limited()
        {
            var r = Reply.Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        }
        var h = new StubHandler(_ => Limited());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client(h).GetShowAsync(1, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(h.Requests.Count <= 4, $"asked {h.Requests.Count} times");
    }
}

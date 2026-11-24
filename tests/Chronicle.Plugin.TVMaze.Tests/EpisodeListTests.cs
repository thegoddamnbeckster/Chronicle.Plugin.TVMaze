namespace Chronicle.Plugin.TVMaze.Tests;

/// <summary>The episode guide of one season, as Chronicle asks for it when a show was added with no files.</summary>
public class EpisodeListTests
{
    private static TvMazeMetadataProvider Provider(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) =>
        new(new TvMazeClient(new HttpClient(new StubHandler(answers)), new CaptureLogger()));

    private const string Seasons = "[{\"id\":10,\"number\":1},{\"id\":11,\"number\":2}]";

    [Fact]
    public async Task ASeasonsEpisodes_AreListedWithTheirNumberTitleSummaryAndAirDate()
    {
        var provider = Provider(
            _ => Reply.Json(Seasons),
            r => Reply.Json(r.RequestUri!.AbsolutePath.EndsWith("/seasons/11/episodes")
                ? "[{\"id\":1,\"name\":\"Pilot\",\"number\":1,\"airdate\":\"2020-01-17\",\"summary\":\"<p>First.</p>\"},{\"id\":2,\"name\":\"Two\",\"number\":2,\"airdate\":\"2020-01-24\"}]"
                : "[]"));

        var episodes = await provider.GetEpisodeListAsync("show:49113", 2);

        Assert.Equal([1, 2], episodes.Select(e => e.EpisodeNumber));
        Assert.Equal("Pilot", episodes[0].Title);
        Assert.Equal("First.", episodes[0].Overview);
        Assert.Equal("2020-01-17", episodes[0].AirDate);
    }

    [Fact]
    public async Task ASeasonItDoesNotList_IsEmpty()
    {
        var provider = Provider(_ => Reply.Json(Seasons));
        Assert.Empty(await provider.GetEpisodeListAsync("show:49113", 0));
    }

    [Fact]
    public async Task AnIdThatIsNotAShow_IsEmpty_WithoutAskingTvMaze()
    {
        var handler = new StubHandler(_ => Reply.Json(Seasons));
        var provider = new TvMazeMetadataProvider(new TvMazeClient(new HttpClient(handler), new CaptureLogger()));

        Assert.Empty(await provider.GetEpisodeListAsync("episode:5", 1));
        Assert.Empty(handler.Requests);
    }
}

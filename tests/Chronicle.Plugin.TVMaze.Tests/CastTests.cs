using System.Text.Json;

namespace Chronicle.Plugin.TVMaze.Tests;

/// <summary>Everything TVMaze lists for a show is kept: the whole cast, however long it is (it used to be cut to the first 20).</summary>
public class CastTests
{
    private static TvMazeShow ShowWithCast(int people)
    {
        var cast = string.Join(",", Enumerable.Range(1, people).Select(i =>
            "{\"person\":{\"id\":" + i + ",\"name\":\"Actor " + i + "\"},\"character\":{\"id\":" + i + ",\"name\":\"Role " + i + "\"}}"));
        var json = "{\"id\":1,\"name\":\"A Long Show\",\"_embedded\":{\"cast\":[" + cast + "]}}";
        return JsonSerializer.Deserialize<TvMazeShow>(json)!;
    }

    [Fact]
    public void A_cast_longer_than_the_old_cap_of_twenty_is_kept_whole_and_in_order()
    {
        var meta = TvMazeMetadataProvider.MapShow(ShowWithCast(75));
        Assert.Equal(75, meta.Cast.Count);
        Assert.Equal("Actor 1", meta.Cast[0].Name);
        Assert.Equal("Actor 75", meta.Cast[74].Name);
        Assert.Equal("Role 75", meta.Cast[74].Role);
    }

    [Fact]
    public void A_short_cast_is_unchanged()
    {
        Assert.Equal(3, TvMazeMetadataProvider.MapShow(ShowWithCast(3)).Cast.Count);
    }
}

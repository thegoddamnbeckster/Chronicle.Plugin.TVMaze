namespace Chronicle.Plugin.TVMaze.Tests;

public class ProviderTests
{
    private static readonly HashSet<string> HostKnows = new HashSet<string> { "backdrop_url", "banner_url", "birth_date", "bpm", "cast", "character_art_url", "clearart_url", "collection", "composer", "crew", "death_date", "disc_url", "genres", "isrc", "label", "language", "logo_url", "mood", "overview", "poster_url", "rating", "runtime_minutes", "tags", "thumb_url", "title", "url", "year", "year" };

    [Fact]
    public void Every_field_it_declares_is_one_the_host_can_resolve()
    {
        // A field the host does not know shows nowhere on Metadata Assignment, so declaring it does nothing.
        foreach (var t in new TvMazeMetadataProvider().GetSupportedMediaTypes())
        {
            Assert.All(t.SupportedFields, f => Assert.Contains(f, HostKnows));
            foreach (var (_, fields) in t.LevelFields ?? [])
                Assert.All(fields, f => Assert.Contains(f, HostKnows));
        }
    }

    [Fact]
    public void The_plugin_needs_no_settings()
    {
        Assert.Empty(new TvMazeMetadataProvider().GetSettingsSchema().Settings);
    }

    [Fact]
    public void The_version_it_reports_is_a_real_version_number()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", new TvMazeMetadataProvider().Version);
    }
}

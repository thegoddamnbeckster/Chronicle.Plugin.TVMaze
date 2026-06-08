using System.Text.Json.Serialization;

namespace Chronicle.Plugin.TVMaze;

record TvMazeSearchResult(
    [property: JsonPropertyName("score")]  double     Score,
    [property: JsonPropertyName("show")]   TvMazeShow Show);

record TvMazeShow(
    [property: JsonPropertyName("id")]       int              Id,
    [property: JsonPropertyName("name")]     string           Name,
    [property: JsonPropertyName("type")]     string?          Type,
    [property: JsonPropertyName("language")] string?          Language,
    [property: JsonPropertyName("genres")]   string[]?        Genres,
    [property: JsonPropertyName("status")]   string?          Status,
    [property: JsonPropertyName("runtime")]  int?             Runtime,
    [property: JsonPropertyName("premiered")]string?          Premiered,
    [property: JsonPropertyName("summary")]  string?          Summary,
    [property: JsonPropertyName("rating")]   TvMazeRating?    Rating,
    [property: JsonPropertyName("image")]    TvMazeImage?     Image,
    [property: JsonPropertyName("network")]  TvMazeNetwork?   Network,
    [property: JsonPropertyName("externals")]TvMazeExternals? Externals,
    [property: JsonPropertyName("_embedded")]TvMazeEmbedded?  Embedded);

record TvMazeSeason(
    [property: JsonPropertyName("id")]           int          Id,
    [property: JsonPropertyName("number")]       int          Number,
    [property: JsonPropertyName("name")]         string?      Name,
    [property: JsonPropertyName("episodeOrder")] int?         EpisodeOrder,
    [property: JsonPropertyName("premiereDate")] string?      PremiereDate,
    [property: JsonPropertyName("endDate")]      string?      EndDate,
    [property: JsonPropertyName("summary")]      string?      Summary,
    [property: JsonPropertyName("image")]        TvMazeImage? Image);

record TvMazeEpisode(
    [property: JsonPropertyName("id")]      int           Id,
    [property: JsonPropertyName("name")]    string?       Name,
    [property: JsonPropertyName("season")]  int?          Season,
    [property: JsonPropertyName("number")]  int?          Number,
    [property: JsonPropertyName("airdate")] string?       Airdate,
    [property: JsonPropertyName("runtime")] int?          Runtime,
    [property: JsonPropertyName("summary")] string?       Summary,
    [property: JsonPropertyName("rating")]  TvMazeRating? Rating,
    [property: JsonPropertyName("image")]   TvMazeImage?  Image);

record TvMazeImage(
    [property: JsonPropertyName("medium")]   string? Medium,
    [property: JsonPropertyName("original")] string? Original);

record TvMazeRating(
    [property: JsonPropertyName("average")] double? Average);

record TvMazeNetwork(
    [property: JsonPropertyName("name")]    string         Name,
    [property: JsonPropertyName("country")] TvMazeCountry? Country);

record TvMazeCountry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("code")] string? Code);

record TvMazeExternals(
    [property: JsonPropertyName("thetvdb")] long?   TheTvdb,
    [property: JsonPropertyName("imdb")]    string? Imdb,
    [property: JsonPropertyName("tvrage")]  long?   TvRage);

record TvMazeCastMember(
    [property: JsonPropertyName("person")]    TvMazePerson?    Person,
    [property: JsonPropertyName("character")] TvMazeCharacter? Character);

record TvMazePerson(
    [property: JsonPropertyName("id")]   int     Id,
    [property: JsonPropertyName("name")] string? Name);

record TvMazeCharacter(
    [property: JsonPropertyName("id")]   int     Id,
    [property: JsonPropertyName("name")] string? Name);

record TvMazeArtwork(
    [property: JsonPropertyName("id")]           int                    Id,
    [property: JsonPropertyName("type")]         string?                Type,
    [property: JsonPropertyName("main")]         bool                   Main,
    [property: JsonPropertyName("resolutions")]  TvMazeArtworkResolutions? Resolutions);

record TvMazeArtworkResolutions(
    [property: JsonPropertyName("original")] TvMazeArtworkSize? Original,
    [property: JsonPropertyName("medium")]   TvMazeArtworkSize? Medium);

record TvMazeArtworkSize(
    [property: JsonPropertyName("url")]    string? Url,
    [property: JsonPropertyName("width")]  int?    Width,
    [property: JsonPropertyName("height")] int?    Height);

record TvMazeEmbedded(
    [property: JsonPropertyName("cast")]   TvMazeCastMember[]? Cast,
    [property: JsonPropertyName("images")] TvMazeArtwork[]?    Images);

using System.Text.Json.Serialization;

namespace SteamDownloader.WebApi;

public class GetPublishedFileDetailsResponse
{
    [JsonPropertyName("result")]
    public uint Result { get; set; }

    [JsonPropertyName("resultcount")]
    public uint ResultCount { get; set; }

    [JsonPropertyName("publishedfiledetails")]
    public WorkshopFileDetails[]? PublishedFileDetails { get; set; }
}

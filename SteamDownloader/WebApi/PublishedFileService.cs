using System.Text.Json;
using SteamDownloader.Helpers;

namespace SteamDownloader.WebApi;

public class PublishedFileService(SteamSession steamSession)
{
    public string ApiKey => steamSession.SteamClient.Configuration.WebAPIKey;
    public Uri WebApiBaseAddress => steamSession.SteamClient.Configuration.WebAPIBaseAddress;

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new DateTimeOffsetSecConverter(),
        }
    };

    public async Task<QueryFilesResponse> QueryFiles(PublishedFileQueryType? query_type = null,
                                                     uint? page = null,
                                                     string? cursor = null,
                                                     uint? numperpage = null,
                                                     string? creator_appid = null,
                                                     uint? appid = null,
                                                     string? requiredtags = null,
                                                     string? excludedtags = null,
                                                     bool? match_all_tags = null,
                                                     string? required_flags = null,
                                                     string? omitted_flags = null,
                                                     string? search_text = null,
                                                     PublishedFileInfoMatchingFileType? filetype = null,
                                                     uint? child_publishedfileid = null,
                                                     uint? days = null,
                                                     bool? include_recent_votes_only = null,
                                                     uint? cache_max_age_seconds = null,
                                                     uint? language = null,
                                                     object? required_kv_tags = null,
                                                     bool? totalonly = null,
                                                     bool? ids_only = null,
                                                     bool? return_vote_data = null,
                                                     bool? return_tags = null,
                                                     bool? return_kv_tags = null,
                                                     bool? return_previews = null,
                                                     bool? return_children = null,
                                                     bool? return_short_description = null,
                                                     bool? return_for_sale_data = null,
                                                     bool? return_metadata = null,
                                                     uint? return_playtime_stats = null
        )
    {
        KeyValuePair<string, string?>[] kvs =
            [
                new("key", ApiKey),
                    new(nameof(query_type), ((int?)query_type)?.ToString()),
                    new(nameof(page), page?.ToString()),
                    new(nameof(cursor), cursor),
                    new(nameof(numperpage), numperpage?.ToString()),
                    new(nameof(creator_appid), creator_appid),
                    new(nameof(appid), appid?.ToString()),
                    new(nameof(requiredtags), requiredtags),
                    new(nameof(excludedtags), excludedtags),
                    new(nameof(match_all_tags), match_all_tags?.ToString()),
                    new(nameof(required_flags), required_flags),
                    new(nameof(omitted_flags), omitted_flags),
                    new(nameof(search_text), search_text),
                    new(nameof(filetype), ((int?)filetype)?.ToString()),
                    new(nameof(child_publishedfileid), child_publishedfileid?.ToString()),
                    new(nameof(days), days?.ToString()),
                    new(nameof(include_recent_votes_only), include_recent_votes_only?.ToString()),
                    new(nameof(cache_max_age_seconds), cache_max_age_seconds?.ToString()),
                    new(nameof(language), language?.ToString()),
                    new(nameof(required_kv_tags), required_kv_tags?.ToString()),
                    new(nameof(totalonly), totalonly?.ToString()),
                    new(nameof(ids_only), ids_only?.ToString()),
                    new(nameof(return_vote_data), return_vote_data?.ToString()),
                    new(nameof(return_tags), return_tags?.ToString()),
                    new(nameof(return_kv_tags), return_kv_tags?.ToString()),
                    new(nameof(return_previews), return_previews?.ToString()),
                    new(nameof(return_children), return_children?.ToString()),
                    new(nameof(return_short_description), return_short_description?.ToString()),
                    new(nameof(return_for_sale_data), return_for_sale_data?.ToString()),
                    new(nameof(return_metadata), return_metadata?.ToString()),
                    new(nameof(return_playtime_stats), return_playtime_stats?.ToString()),
                ];

        Uri url = new(WebApiBaseAddress, $"/IPublishedFileService/QueryFiles/v1/?{Utils.MakeQueryParams(kvs)}");
        var json = await steamSession.HttpClient.GetStringAsync(url);

        WebApiResponse<QueryFilesResponse>? response = JsonSerializer.Deserialize<WebApiResponse<QueryFilesResponse>>(json, jsonOptions);

        return response?.Response ?? throw new ArgumentNullException("response");
    }
}

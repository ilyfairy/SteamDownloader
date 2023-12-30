using System.Diagnostics;

namespace SteamDownloader;

public static class SteamHelper
{
    public static async Task<SteamContentServer[]> TestContentServerConnectionAsync(HttpClient httpClient, IReadOnlyCollection<SteamContentServer> servers, TimeSpan timeout)
    {
        List<SteamContentServer> success = new();
        await Parallel.ForEachAsync(servers, async (s, _) =>
        {
            Stopwatch sw = new();
            CancellationTokenSource cts = new();
            cts.CancelAfter(timeout);
            sw.Restart();
            Uri url = new UriBuilder("https", s.Host).Uri;
            try
            {
                var response = await httpClient.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception)
            {
                return;
            }
            if (sw.ElapsedMilliseconds > 4000)
                return;

            lock (success)
            {
                success.Add(s);
            }
        });
        return success.ToArray();
    }
}
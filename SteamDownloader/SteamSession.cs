using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamDownloader;

public partial class SteamSession : IDisposable
{
    public HttpClient HttpClient { get; set; }
    public SteamClient SteamClient { get; }
    public CallbackManager CallbackManager { get; }

    private readonly SteamUser steamUser;
    private readonly SteamApps steamApps;
    private readonly SteamContent steamContent;
    private readonly SteamCloud steamCloud;
    private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> publishedFile;

    private readonly Dictionary<uint, ulong> AppTokensCache = new();
    private readonly Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfosCache = new();
    private readonly Dictionary<uint, byte[]> DepotKeysCache = new();

    public SteamAuthentication Authentication { get; }

    public event EventHandler<SteamClient.DisconnectedCallback>? Disconnected;
    private EResult connectionLoginResult;

    private readonly SemaphoreSlim loginLock = new(1);

    public List<SteamContentServer> ContentServers { get; set; } = new();

    public SteamSession(SteamConfiguration? steamConfiguration = null)
    {
        HttpClient = new();
        if (steamConfiguration is null)
        {
            SteamClient = new();
        }
        else
        {
            SteamClient = new(steamConfiguration);
        }
        CallbackManager = new(SteamClient);

        steamUser = SteamClient.GetHandler<SteamUser>() ?? throw new Exception("SteamUser获取失败");
        steamApps = SteamClient.GetHandler<SteamApps>() ?? throw new Exception("SteamApps获取失败");
        steamContent = SteamClient.GetHandler<SteamContent>() ?? throw new Exception("SteamContent获取失败");
        steamCloud = SteamClient.GetHandler<SteamCloud>() ?? throw new Exception("SteamCloud获取失败");

        var steamUnifiedMessages = SteamClient.GetHandler<SteamUnifiedMessages>()!;
        publishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();

        Authentication = new SteamAuthentication(this);

        CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(v =>
        {
            Disconnected?.Invoke(this, v);
        });
    }

    public void Disconnect()
    {
        SteamClient.Disconnect();
        EnsureRunAllCallback();
    }

    public void EnsureRunAllCallback()
    {
        if (SteamClient.GetCallback() is not null)
            CallbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (SteamClient.IsConnected)
            return;

        try
        {
            await loginLock.WaitAsync(cancellationToken);
            SteamClient.Connect();

            await Task.Run(() => CallbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan), cancellationToken);

            if (SteamClient.IsConnected is false)
            {
                throw new ConnectionException("连接失败");
            }
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            loginLock.Release();
        }
    }


    public void CheckConnectionLogin()
    {
        if (SteamClient.IsConnected is false)
            throw new ConnectionException("没有连接");

        if (Authentication.Logged is false)
            throw new ConnectionException("没有登录");
    }


    public async Task<List<SteamContentServer>> GetCdnServersAsync(uint? cellId = null, uint? max_servers = null, CancellationToken cancellationToken = default)
    {
        cellId ??= SteamClient.CellID;

        var url = new Uri(SteamClient.Configuration.WebAPIBaseAddress, $"/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={cellId}{(max_servers is null ? "" : $"&max_servers={max_servers}")}");

        var jsonString = await HttpClient.GetStringAsync(url, cancellationToken);
        var servers = JsonSerializer.Deserialize<List<SteamContentServer>>(JsonNode.Parse(jsonString)?["response"]?["servers"]) ?? throw new Exception("获取失败");

        return servers;
    }

    public async ValueTask<SteamContentServer> GetRandomCdnServer(CancellationToken cancellationToken = default)
    {
        if (ContentServers.Count == 0)
        {
            var r = await GetCdnServersAsync(null, null, cancellationToken);
            ContentServers = r;
        }
        return ContentServers[Random.Shared.Next(0, ContentServers.Count)];
    }

    public async Task<ulong> GetAppAccessTokenAsync(uint appId)
    {
        CheckConnectionLogin();

        ulong appToken;
        if (!AppTokensCache.TryGetValue(appId, out appToken))
        {
            var appTokenResult = await steamApps.PICSGetAccessTokens(appId, null);

            if (!appTokenResult.AppTokens.TryGetValue(appId, out appToken))
            {
                if (appTokenResult.AppTokensDenied.Contains(appId))
                {
                    throw new Exception($"权限不足  AppId:{appId}");
                }
                throw new Exception("获取失败");
            }

            foreach (var tokenKV in appTokenResult.AppTokens)
            {
                AppTokensCache[tokenKV.Key] = tokenKV.Value;
            }
        }

        return appToken;
    }

    public async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(uint appId)
    {
        CheckConnectionLogin();

        var appToken = await GetAppAccessTokenAsync(appId);

        // 获取ProductInfo
        if (AppInfosCache.TryGetValue(appId, out var productInfo))
        {
            return productInfo;
        }

        CheckConnectionLogin();
        var productInfoRequest = new SteamApps.PICSRequest(appId, appToken);
        var productInfoResult = await steamApps.PICSGetProductInfo(productInfoRequest, null);

        var firstProductInfoResult = productInfoResult.Results?.FirstOrDefault();

        if (firstProductInfoResult is null)
            throw new Exception($"ProductInfo获取失败  AppId:{appId}");

        if (!firstProductInfoResult.Apps.TryGetValue(appId, out productInfo))
        {
            throw new Exception($"ProductInfo获取失败, 找不到ProductInfo  AppId:{appId}");
        }

        foreach (var item in firstProductInfoResult.Apps)
        {
            AppInfosCache[item.Key] = item.Value;
        }

        return productInfo;
    }

    public KeyValue? GetAppInfoSection(SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo, EAppInfoSection section)
    {
        string sectionKey = section switch
        {
            EAppInfoSection.Common => "common",
            EAppInfoSection.Extended => "extended",
            EAppInfoSection.Config => "config",
            EAppInfoSection.Depots => "depots",
            EAppInfoSection.Install => "install",
            EAppInfoSection.UFS => "ufs",
            EAppInfoSection.Localization => "localization",
            _ => throw new NotImplementedException(),
        };

        var secion = appInfo.KeyValues.Children.FirstOrDefault(v => v.Name == sectionKey);
        return secion;
    }

    public KeyValue? GetAppInfoSection(uint appId, EAppInfoSection section)
    {
        if (AppInfosCache.TryGetValue(appId, out var appInfo))
        {
            return GetAppInfoSection(appInfo, section);
        }

        return null;
    }

    public DepotsContent GetAppInfoDepotsSection(SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo)
    {
        CheckConnectionLogin();

        var depots = GetAppInfoSection(appInfo, EAppInfoSection.Depots)!;
        return new DepotsContent(appInfo.ID, depots);
    }

    public async Task<ulong> GetManifestRequestCodeAsync(uint appId, uint depotId, ulong manifestId, string branch = "public", string? branchPasswordHash = null)
    {
        CheckConnectionLogin();

        var result = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch, branchPasswordHash);

        return result;
    }
    public async Task<byte[]> GetDepotKeyAsync(uint depotId, uint appId = 0)
    {
        CheckConnectionLogin();

        if (DepotKeysCache.TryGetValue(depotId, out var depotKey))
        {
            return depotKey;
        }

        var result = await steamApps.GetDepotDecryptionKey(depotId, appId);

        if (result.Result is EResult.AccessDenied)
        {
            throw new Exception($"AccessDenied  DepotId:{depotId}");
        }
        if (result.Result is not EResult.OK)
        {
            throw new Exception($"获取失败  DepotId:{depotId}");
        }

        DepotKeysCache[depotId] = result.DepotKey;
        return result.DepotKey;
    }

    public async Task<DepotManifest> GetDepotManifestEncryptAsync(uint depotId, ulong manifestId, ulong manifestRequestCode)
    {
        CheckConnectionLogin();

        var server = await GetRandomCdnServer();
        const uint MANIFEST_VERSION = 5;

        Uri url = new(server.Url, $"/depot/{depotId}/manifest/{manifestId}/{MANIFEST_VERSION}/{manifestRequestCode}");
        var stream = await HttpClient.GetStreamAsync(url);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var file = zip.Entries.First();
        var bytes = new byte[file.Length];
        file.Open().ReadExactly(bytes);

        return DepotManifest.Deserialize(bytes);
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint depotId, ulong manifestId, ulong manifestRequestCode, byte[] depotKey)
    {
        CheckConnectionLogin();

        var manifestInfo = await GetDepotManifestEncryptAsync(depotId, manifestId, manifestRequestCode);
        manifestInfo.DecryptFilenames(depotKey);
        return manifestInfo;
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint appId, uint depotId, ulong manifestId, string branch = "public")
    {
        CheckConnectionLogin();

        var code = await GetManifestRequestCodeAsync(appId, depotId, manifestId, branch);
        var manifestInfo = await GetDepotManifestEncryptAsync(depotId, manifestId, code);
        var key = await GetDepotKeyAsync(depotId, appId);
        manifestInfo.DecryptFilenames(key);
        return manifestInfo;
    }

    public Task<DepotManifest> GetWorkshopManifestAsync(uint appId, ulong hcontentFileId)
    {
        CheckConnectionLogin();

        return GetDepotManifestAsync(appId, appId, hcontentFileId);
    }

    public async Task<byte[]> DownloadChunkDecryptBytesAsync(uint depotId, DepotManifest.ChunkData chunkData, byte[] depotKey, CancellationToken cancellationToken = default)
    {
        CheckConnectionLogin();

        var server = await GetRandomCdnServer(cancellationToken);
        Uri url = new(server.Url, $"/depot/{depotId}/chunk/{Convert.ToHexString(chunkData.ChunkID!)}");

        var response = await HttpClient.GetAsync(url, cancellationToken);
        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var chunk = new DepotChunk(chunkData, data);
        chunk.Process(depotKey);
        return chunk.Data;
    }

    public Task<byte[]> DownloadChunkDecryptBytesAsync(uint depotId, DepotManifest.ChunkData chunkData, CancellationToken cancellationToken = default)
    {
        CheckConnectionLogin();

        if (DepotKeysCache.TryGetValue(depotId, out var depotKey))
        {
            return DownloadChunkDecryptBytesAsync(depotId, chunkData, depotKey, cancellationToken);
        }
        else
        {
            throw new Exception("找不到DepotKey");
        }
    }

    /// <summary>
    /// 获取创意工坊文件信息
    /// </summary>
    /// <param name="appId">AppId</param>
    /// <param name="pubFileId">Id</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public async Task<PublishedFileDetails> GetPublishedFileAsync(uint appId, ulong pubFileId)
    {
        CheckConnectionLogin();

        var request = new CPublishedFile_GetDetails_Request();
        request.appid = appId;
        request.publishedfileids.Add(pubFileId);

        var result = await publishedFile.SendMessage(v => v.GetDetails(request));

        if (result.Result != EResult.OK)
        {
            throw new Exception($"响应失败: {result}");
        }

        var response = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
        return response.publishedfiledetails.First();
    }

    public async Task<ICollection<PublishedFileDetails>> GetPublishedFileAsync(uint appId, params ulong[] pubFileIds)
    {
        CheckConnectionLogin();

        var request = new CPublishedFile_GetDetails_Request();
        request.appid = appId;
        request.publishedfileids.AddRange(pubFileIds);

        var result = await publishedFile.SendMessage(v => v.GetDetails(request));

        if (result.Result != EResult.OK)
        {
            throw new Exception($"响应失败: {result}");
        }

        var response = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
        return response.publishedfiledetails;
    }

    private bool _disposed = false;
    public void Dispose()
    {
        _disposed = true;
        SteamClient.Disconnect();
        HttpClient.Dispose();
    }



}

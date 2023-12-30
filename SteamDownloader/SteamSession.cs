using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamDownloader;

public class SteamSession : IDisposable
{
    public HttpClient HttpClient { get; set; }
    private readonly SteamClient steamClient;
    private readonly CallbackManager callbackManager;

    private readonly SteamUser steamUser;
    private readonly SteamApps steamApps;
    private readonly SteamContent steamContent;
    private readonly SteamCloud steamCloud;
    private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> publishedFile;

    private readonly Dictionary<uint, ulong> AppTokensCache = new();
    private readonly Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfosCache = new();
    private readonly Dictionary<uint, byte[]> DepotKeysCache = new();

    public SteamAuthentication Authentication { get; }

    public event EventHandler? Disconnected;
    private EResult currentEResult;

    private readonly SemaphoreSlim loginLock = new(1);

    public List<SteamContentServer> ContentServers { get; set; } = new();

    public SteamSession(SteamConfiguration? steamConfiguration = null)
    {
        HttpClient = new();
        if(steamConfiguration is null)
        {
            steamClient = new();
        }
        else
        {
            steamClient = new(steamConfiguration);
        }
        callbackManager = new(steamClient);

        steamUser = steamClient.GetHandler<SteamUser>() ?? throw new Exception("SteamUser获取失败");
        steamApps = steamClient.GetHandler<SteamApps>() ?? throw new Exception("SteamApps获取失败");
        steamContent = steamClient.GetHandler<SteamContent>() ?? throw new Exception("SteamContent获取失败");
        steamCloud = steamClient.GetHandler<SteamCloud>() ?? throw new Exception("SteamCloud获取失败");

        var steamUnifiedMessages = steamClient.GetHandler<SteamUnifiedMessages>()!;
        publishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();

        Authentication = new SteamAuthentication(this);
    }

    public void Disconnect()
    {
        steamClient.Disconnect();
        callbackManager.RunWaitCallbacks(Timeout.InfiniteTimeSpan);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (steamClient.IsConnected)
            return;

        try
        {
            await loginLock.WaitAsync(cancellationToken);
            steamClient.Connect();

            await Task.Run(() => callbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan), cancellationToken);

            if (steamClient.IsConnected is false)
            {
                throw new Exception("连接失败");
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

    public async Task<List<SteamContentServer>> GetCdnServersAsync(uint? cellId = null, uint? max_servers = null, CancellationToken cancellationToken = default)
    {
        cellId ??= steamClient.CellID;

        var url = new Uri(steamClient.Configuration.WebAPIBaseAddress, $"/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={cellId}{(max_servers is null ? "" : $"&max_servers={max_servers}")}");

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
        var appToken = await GetAppAccessTokenAsync(appId);

        // 获取ProductInfo
        if (AppInfosCache.TryGetValue(appId, out var productInfo))
        {
            return productInfo;
        }

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
        var depots = GetAppInfoSection(appInfo, EAppInfoSection.Depots)!;
        return new DepotsContent(appInfo.ID, depots);
    }

    public async Task<ulong> GetManifestRequestCodeAsync(uint appId, uint depotId, ulong manifestId, string branch = "public", string? branchPasswordHash = null)
    {
        var result = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch, branchPasswordHash);

        return result;
    }
    public async Task<byte[]> GetDepotKeyAsync(uint depotId, uint appId = 0)
    {
        if (DepotKeysCache.TryGetValue(depotId, out var depotKey))
        {
            return depotKey;
        }

        var result = await steamApps.GetDepotDecryptionKey(depotId, appId);

        if (result.Result is EResult.AccessDenied)
        {
            throw new Exception($"AccessDenied  DepotId:{depotId}");
        }
        if(result.Result is not EResult.OK)
        {
            throw new Exception($"获取失败  DepotId:{depotId}");
        }

        DepotKeysCache[depotId] = result.DepotKey;
        return result.DepotKey;
    }

    public async Task<DepotManifest> GetDepotManifestEncryptAsync(uint depotId, ulong manifestId, ulong manifestRequestCode)
    {
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
        var manifestInfo = await GetDepotManifestEncryptAsync(depotId, manifestId, manifestRequestCode);
        manifestInfo.DecryptFilenames(depotKey);
        return manifestInfo;
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint appId, uint depotId, ulong manifestId, string branch = "public")
    {
        var code = await GetManifestRequestCodeAsync(appId, depotId, manifestId, branch);
        var manifestInfo = await GetDepotManifestEncryptAsync(depotId, manifestId, code);
        var key = await GetDepotKeyAsync(depotId, appId);
        manifestInfo.DecryptFilenames(key);
        return manifestInfo;
    }

    public Task<DepotManifest> GetWorkshopManifestAsync(uint appId, ulong hcontentFileId)
    {
        return GetDepotManifestAsync(appId, appId, hcontentFileId);
    }

    public async Task<byte[]> DownloadChunkDecryptBytesAsync(uint depotId, DepotManifest.ChunkData chunkData, byte[] depotKey, CancellationToken cancellationToken = default)
    {
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
        var request = new CPublishedFile_GetDetails_Request();
        request.appid = appId;
        request.publishedfileids.AddRange(pubFileIds);

        var result = await publishedFile.SendMessage(v => v.GetDetails(request));

        if(result.Result != EResult.OK)
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
        steamClient.Disconnect();
        HttpClient.Dispose();
    }



    public class SteamAuthentication
    {
        private readonly SteamSession steamSession;
        public bool Logged { get; private set; }
        public string? AccessToken { get; private set; }

        public SteamAuthentication(SteamSession steamSession)
        {
            this.steamSession = steamSession;

            steamSession.callbackManager.Subscribe<SteamClient.ConnectedCallback>(v =>
            {
                Logged = true;
                Console.WriteLine("成功连接");
            });
            steamSession.callbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
            {
                steamSession.currentEResult = v.Result;
                Console.WriteLine("用户登录");
            });
            steamSession.callbackManager.Subscribe<SteamUser.LoggedOffCallback>(v =>
            {
                steamSession.currentEResult = v.Result;
                Console.WriteLine("登录注销");
                Logged = false;
            });

        }

        /// <summary>
        /// 匿名登录
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task LoginAnonymousAsync(CancellationToken cancellationToken = default)
        {
            if (steamSession.steamClient.IsConnected is false)
            {
                await steamSession.ConnectAsync(cancellationToken);
            }

            try
            {
                await steamSession.loginLock.WaitAsync(cancellationToken);

                steamSession.steamUser.LogOnAnonymous();

                await Task.Run(steamSession.callbackManager.RunWaitCallbacks, cancellationToken);

                if (steamSession.currentEResult is EResult.OK)
                {
                    Logged = true;
                }
                else if (steamSession.currentEResult is EResult.NoConnection)
                {
                    throw new Exception("没有连接, 请先连接");
                }
                else
                {
                    throw new Exception($"登录失败: {steamSession.currentEResult}");
                }
            }
            finally
            {
                steamSession.loginLock.Release();
            }
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <param name="shouldRememberPassword">是否记住密码(在此之后可以用AccessToken登录)</param>
        /// <returns></returns>
        public async Task LoginAsync(string username, string password, bool shouldRememberPassword, CancellationToken cancellationToken = default)
        {
            if (!steamSession.steamClient.IsConnected)
            {
                await steamSession.ConnectAsync(cancellationToken);
            }

            try
            {
                await steamSession.loginLock.WaitAsync(cancellationToken);

                var authSession = await steamSession.steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails()
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = shouldRememberPassword,
                    Authenticator = new UserConsoleAuthenticator(),
                });

                using var _ = steamSession.callbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
                {
                    steamSession.currentEResult = v.Result;
                });

                var result = await authSession.PollingWaitForResultAsync(cancellationToken);

                AccessToken = result.RefreshToken;
                steamSession.steamUser.LogOn(new SteamUser.LogOnDetails()
                {
                    Username = result.AccountName,
                    Password = null,
                    AccessToken = result.RefreshToken,
                    ShouldRememberPassword = shouldRememberPassword,
                });

                while (true)
                {
                    steamSession.callbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan);
                    if (steamSession.currentEResult is EResult.OK)
                        break;
                    if (steamSession.currentEResult is EResult.NoConnection)
                        throw new Exception("登录失败");
                    await Task.Delay(100, cancellationToken);
                }
            }
            finally
            {
                steamSession.loginLock.Release();
            }
        }

        public async Task LoginFromAccessTokenAsync(string username, string accessToken, CancellationToken cancellationToken = default)
        {
            if (!steamSession.steamClient.IsConnected)
            {
                await steamSession.ConnectAsync(cancellationToken);
            }

            try
            {
                await steamSession.loginLock.WaitAsync(cancellationToken);

                using var loggedOnCallbackDisposable = steamSession.callbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
                {
                    steamSession.currentEResult = v.Result;
                });

                steamSession.steamUser.LogOn(new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = null,
                    AccessToken = accessToken,
                    ShouldRememberPassword = true,
                });

                while (true)
                {
                    steamSession.callbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan);
                    if (steamSession.currentEResult is EResult.OK)
                    {
                        AccessToken = accessToken;
                        break;
                    }
                    if (steamSession.currentEResult is EResult.NoConnection)
                        throw new Exception("登录失败");
                    await Task.Delay(50, cancellationToken);
                }

            }
            finally
            {
                steamSession.loginLock.Release();
            }
        }
    }
}

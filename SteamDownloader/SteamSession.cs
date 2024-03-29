﻿using SteamDownloader.WebApi;
using SteamDownloader.WebApi.Interfaces;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    public bool IsCache { get; set; } = true;
    private readonly ConcurrentDictionary<uint, ulong> AppTokensCache = new();
    private readonly ConcurrentDictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfosCache = new();
    private readonly ConcurrentDictionary<uint, byte[]> DepotKeysCache = new();

    public PublishedFileService PublishedFileService { get; }
    public SteamRemoteStorage SteamRemoteStorage { get; }

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

        PublishedFileService = new(this);
        SteamRemoteStorage = new(this);
    }

    public void Disconnect()
    {
        SteamClient.Disconnect();
        EnsureRunAllCallback();
    }

    public void EnsureRunAllCallback()
    {
        CallbackManager.EnsureRunAllCallbacks();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (SteamClient.IsConnected)
            return;

        try
        {
            await loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            await SteamClient.Connect(null, cancellationToken).ConfigureAwait(false);
            
            try
            {
                await SteamClient.WaitConnectionCallbackAsync().ConfigureAwait(false);
                CallbackManager.EnsureRunAllCallbacks();
            }
            catch (Exception)
            {
                if (SteamClient.IsConnected)
                    return;
            }

            if (SteamClient.IsConnected is false)
            {
                for (int i = 0; i < 2; i++)
                {
                    try
                    {
                        await ConnectWithoutLockAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (ConnectionException)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
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

    private async Task ConnectWithoutLockAsync(CancellationToken cancellationToken = default)
    {
        if (SteamClient.IsConnected)
            return;

        try
        {
            await SteamClient.Connect(null, cancellationToken).ConfigureAwait(false);
            await SteamClient.WaitConnectionCallbackAsync().ConfigureAwait(false);
            CallbackManager.EnsureRunAllCallbacks();
        }
        catch (Exception)
        {
            if (SteamClient.IsConnected)
                return;
        }

        if (SteamClient.IsConnected is false)
        {
            throw new ConnectionException("连接失败");
        }
    }

    public async Task EnsureConnectionLogin(CancellationToken cancellationToken = default)
    {
        EnsureRunAllCallback();

        if (SteamClient.IsConnected is false)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Authentication.Logged is false)
        {
            await Authentication.EnsureLoginAsync(cancellationToken).ConfigureAwait(false);
        }

        if (SteamClient.IsConnected is false)
            throw new ConnectionException("没有连接");

        if (Authentication.Logged is false)
            throw new ConnectionException("没有登录");
    }


    public async Task<List<SteamContentServer>> GetCdnServersAsync(uint? cellId = null, uint? max_servers = null, CancellationToken cancellationToken = default)
    {
        cellId ??= SteamClient.CellID;

        var url = new Uri(SteamClient.Configuration.WebAPIBaseAddress, $"/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={cellId}{(max_servers is null ? "" : $"&max_servers={max_servers}")}");

        var jsonString = await HttpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var servers = JsonSerializer.Deserialize<List<SteamContentServer>>(JsonNode.Parse(jsonString)?["response"]?["servers"]) ?? throw new Exception("获取失败");

        return servers;
    }

    public async ValueTask<SteamContentServer> GetRandomCdnServer(CancellationToken cancellationToken = default)
    {
        if (ContentServers.Count == 0)
        {
            var r = await GetCdnServersAsync(null, null, cancellationToken).ConfigureAwait(false);
            ContentServers = r;
        }
        return ContentServers[Random.Shared.Next(0, ContentServers.Count)];
    }

    public async Task<ulong> GetAppAccessTokenAsync(uint appId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        ulong appToken;
        if (!AppTokensCache.TryGetValue(appId, out appToken))
        {
            SteamApps.PICSTokensCallback appTokenResult = await steamApps.PICSGetAccessTokens(appId, null, cancellationToken);

            if (!appTokenResult.AppTokens.TryGetValue(appId, out appToken))
            {
                if (appTokenResult.AppTokensDenied.Contains(appId))
                {
                    throw new Exception($"权限不足  AppId:{appId}");
                }
                throw new Exception("获取失败");
            }

            if (IsCache)
            {
                foreach (var tokenKV in appTokenResult.AppTokens)
                {
                    AppTokensCache[tokenKV.Key] = tokenKV.Value;
                }
            }
        }

        return appToken;
    }

    public async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetProductInfoAsync(uint appId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var appToken = await GetAppAccessTokenAsync(appId, cancellationToken).ConfigureAwait(false);

        // 获取ProductInfo
        if (AppInfosCache.TryGetValue(appId, out var productInfo))
        {
            return productInfo;
        }

        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);
        var productInfoRequest = new SteamApps.PICSRequest(appId, appToken);
        var productInfoResult = await steamApps.PICSGetProductInfo(productInfoRequest, null, cancellationToken: cancellationToken);

        var firstProductInfoResult = productInfoResult.Results?.FirstOrDefault();

        if (firstProductInfoResult is null)
            throw new Exception($"ProductInfo获取失败  AppId:{appId}");

        if (!firstProductInfoResult.Apps.TryGetValue(appId, out productInfo))
        {
            throw new Exception($"ProductInfo获取失败, 找不到ProductInfo  AppId:{appId}");
        }

        if (IsCache)
        {
            foreach (var item in firstProductInfoResult.Apps)
            {
                AppInfosCache[item.Key] = item.Value;
            }
        }

        return productInfo;
    }

    public async Task<ulong> GetManifestRequestCodeAsync(uint appId, uint depotId, ulong manifestId, string branch = "public", string? branchPasswordHash = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        ulong result;
        try
        {
            result = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch, branchPasswordHash, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException();
        }

        return result;
    }

    public async Task<byte[]> GetDepotKeyAsync(uint appId, uint depotId, CancellationToken cancellationToken = default)
    {
        if (DepotKeysCache.TryGetValue(depotId, out var depotKey))
        {
            return depotKey;
        }

        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var result = await steamApps.GetDepotDecryptionKey(depotId, appId, cancellationToken);

        if (result.Result is EResult.AccessDenied)
        {
            throw new Exception($"AccessDenied  DepotId:{depotId}");
        }
        if (result.Result is not EResult.OK)
        {
            throw new Exception($"获取失败  Result:{result.Result}  DepotId:{depotId}");
        }

        if (IsCache)
        {
            DepotKeysCache[depotId] = result.DepotKey;
        }

        return result.DepotKey;
    }

    public async Task<DepotManifest> GetDepotManifestEncryptedAsync(uint depotId, ulong manifestId, ulong manifestRequestCode, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var server = await GetRandomCdnServer(cancellationToken).ConfigureAwait(false);
        const uint MANIFEST_VERSION = 5;

        Uri url = new(server.Url, $"/depot/{depotId}/manifest/{manifestId}/{MANIFEST_VERSION}/{manifestRequestCode}");

        Stream stream;
        nint unmanagedPtr = 0;

        try
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long len)
            {
                if (len >= 85000)
                {
                    unsafe
                    {
                        unmanagedPtr = (nint)NativeMemory.Alloc((nuint)len);
                        stream = new UnmanagedMemoryStream((byte*)unmanagedPtr, len, len, FileAccess.ReadWrite);
                    }
                }
                else
                {
                    stream = new MemoryStream((int)len);
                }
            }
            else
            {
                stream = new MemoryStream();
            }
            await response.Content.CopyToAsync(stream, cancellationToken);

            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var file = zip.Entries.First();
            var bytes = new byte[file.Length];
            await file.Open().ReadExactlyAsync(bytes, cancellationToken);

            return DepotManifest.Deserialize(bytes);
        }
        catch(HttpRequestException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw;
        }
        finally
        {
            if(unmanagedPtr != 0)
            {
                unsafe
                {
                    NativeMemory.Free((void*)unmanagedPtr);
                }
            }
        }
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint depotId, ulong manifestId, ulong manifestRequestCode, byte[] depotKey, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var manifestInfo = await GetDepotManifestEncryptedAsync(depotId, manifestId, manifestRequestCode, cancellationToken).ConfigureAwait(false);
        manifestInfo.DecryptFilenames(depotKey);
        return manifestInfo;
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint appId, uint depotId, ulong manifestId, string branch = "public", CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var code = await GetManifestRequestCodeAsync(appId, depotId, manifestId, branch, null, cancellationToken).ConfigureAwait(false);
        var manifestInfo = await GetDepotManifestEncryptedAsync(depotId, manifestId, code, cancellationToken).ConfigureAwait(false);
        var key = await GetDepotKeyAsync(appId, depotId, cancellationToken).ConfigureAwait(false);
        manifestInfo.DecryptFilenames(key);
        return manifestInfo;
    }

    public async Task<DepotManifest> GetDepotManifestAsync(uint appId, uint depotId, ulong manifestId, byte[] depotKey, string branch = "public", CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var code = await GetManifestRequestCodeAsync(appId, depotId, manifestId, branch, cancellationToken: cancellationToken).ConfigureAwait(false);
        var manifestInfo = await GetDepotManifestEncryptedAsync(depotId, manifestId, code, cancellationToken).ConfigureAwait(false);
        manifestInfo.DecryptFilenames(depotKey);
        return manifestInfo;
    }

    public Task<DepotManifest> GetWorkshopManifestAsync(uint appId, ulong hcontentFileId, CancellationToken cancellationToken = default)
    {
        return GetDepotManifestAsync(appId, appId, hcontentFileId, "public", cancellationToken);
    }

    public Task<DepotManifest> GetWorkshopManifestAsync(uint appId, ulong hcontentFileId, byte[] depotKey, CancellationToken cancellationToken = default)
    {
        return GetDepotManifestAsync(appId, appId, hcontentFileId, depotKey, "public", cancellationToken);
    }

    public async Task<byte[]> DownloadChunkDataAsync(uint depotId, DepotManifest.ChunkData chunkData, byte[] depotKey, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var server = await GetRandomCdnServer(cancellationToken).ConfigureAwait(false);
        Uri url = new(server.Url, $"/depot/{depotId}/chunk/{Convert.ToHexString(chunkData.ChunkID!)}");

        var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var data = new byte[chunkData.CompressedLength];
        var offset = 0;

        while (await stream.ReadAsync(data.AsMemory(offset, data.Length - offset), cancellationToken).ConfigureAwait(false) is int len and > 0)
        {
            offset += len;
        }

        if (offset != data.Length || stream.ReadByte() is int by and not -1)
            throw new InvalidDataException("Length mismatch after downloading depot chunk!");

        var chunk = new DepotChunk(chunkData, data);

        Process(chunk);

        void Process(DepotChunk chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk.Data);
            ArgumentNullException.ThrowIfNull(depotKey);

            DebugLog.Assert(depotKey.Length == 32, "CryptoHelper", "SymmetricDecrypt used with non 32 byte key!");

            using var aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;

            // first 16 bytes of input is the ECB encrypted IV
            byte[] cryptedIv = new byte[16];
            Array.Copy(chunk.Data, 0, cryptedIv, 0, cryptedIv.Length);

            // ciphertext length
            int cipherTextLength = chunk.Data.Length - cryptedIv.Length;

            // decrypt the IV using ECB
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            byte[] iv;
            using (var aesTransform = aes.CreateDecryptor(depotKey, null))
            {
                iv = aesTransform.TransformFinalBlock(cryptedIv, 0, cryptedIv.Length);
            }

            // decrypt the remaining ciphertext in cbc with the decrypted IV
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = depotKey;


            var ciphertext = chunk.Data.AsSpan(start: cryptedIv.Length);
            //var plaintextTemp = ArrayPool<byte>.Shared.Rent( aes.GetCiphertextLengthCbc( ciphertext.Length, PaddingMode.PKCS7 ) );
            var plaintextTemp = chunk.Data;
            var decryptLength = aes.DecryptCbc(ciphertext, iv, plaintextTemp, PaddingMode.PKCS7);

            byte[] processedData;
            if (plaintextTemp.Length > 1 && plaintextTemp[0] == 'V' && plaintextTemp[1] == 'Z')
            {
                processedData = VZipUtil.Decompress(plaintextTemp, decryptLength);
            }
            else
            {
                processedData = ZipUtil.Decompress(plaintextTemp);
            }
            //ArrayPool<byte>.Shared.Return( plaintextTemp );

            DebugLog.Assert(chunk.ChunkInfo.Checksum != null, nameof(DepotChunk), "Expected data chunk to have a checksum.");

            byte[] dataCrc = CryptoHelper.AdlerHash(processedData);

            if (!dataCrc.SequenceEqual(chunk.ChunkInfo.Checksum))
            {
                throw new InvalidDataException("Processed data checksum is incorrect! Downloaded depot chunk is corrupt or invalid/wrong depot key?");
            }

            chunk.Data = processedData;
            chunk.IsProcessed = true;
        }

        return chunk.Data;
    }

    /// <summary>
    /// 获取创意工坊文件信息
    /// </summary>
    /// <param name="appId">AppId</param>
    /// <param name="pubFileId">Id</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public async Task<WorkshopFileDetails> GetPublishedFileAsync(uint appId, ulong pubFileId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var request = new CPublishedFile_GetDetails_Request();
        request.appid = appId;
        request.publishedfileids.Add(pubFileId);

        var result = await publishedFile.SendMessage(v => v.GetDetails(request), cancellationToken);

        if (result.Result != EResult.OK)
        {
            throw new Exception($"响应失败: {result}");
        }

        var response = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
        return response.publishedfiledetails.First().ToWorkshopFileDetails();
    }

    public async Task<ICollection<WorkshopFileDetails>> GetPublishedFileAsync(uint appId, ulong[] pubFileIds, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionLogin(cancellationToken).ConfigureAwait(false);

        var request = new CPublishedFile_GetDetails_Request();
        request.appid = appId;
        request.publishedfileids.AddRange(pubFileIds);

        var result = await publishedFile.SendMessage(v => v.GetDetails(request), cancellationToken);

        if (result.Result != EResult.OK)
        {
            throw new Exception($"响应失败: {result}");
        }

        var response = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
        return response.publishedfiledetails.Select(v => v.ToWorkshopFileDetails()).ToArray();
    }

    private bool _disposed = false;
    public void Dispose()
    {
        _disposed = true;
        SteamClient.Disconnect();
        HttpClient.Dispose();
    }

}

using SteamKit2;
using System.Security.Cryptography;

namespace SteamDownloader.Helpers;

public static class SteamSessionExtensions
{
    public static async Task<byte[]> DownloadChunkDataWithRetryAsync(this SteamSession steamSession, uint depotId, DepotManifest.ChunkData chunkData, byte[] depotKey, int retry = 3, CancellationToken cancellationToken = default)
    {
        if (retry < 0)
            retry = 1;
        Exception exception = null!;
        for (int i = 0; i < retry; i++)
        {
            try
            {
                return await steamSession.DownloadChunkDataAsync(depotId, chunkData, depotKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
                exception = e;
            }
        }
        throw exception;
    }

    public static async Task DownloadFileDataToStreamAsync(this SteamSession steamSession, Stream stream,uint appId, uint depotId, DepotManifest.FileData fileData, CancellationToken cancellationToken = default)
    {
        var depotKey = await steamSession.GetDepotKeyAsync(appId, depotId);
        await DownloadFileDataToStreamAsync(steamSession, stream, depotId, depotKey, fileData, cancellationToken);
    }

    public static async Task DownloadFileDataToStreamAsync(this SteamSession steamSession, Stream stream, uint depotId, byte[] depotKey, DepotManifest.FileData fileData, CancellationToken cancellationToken = default)
    {
        if (fileData.Flags.HasFlag(EDepotFileFlag.Directory))
            throw new Exception("FileData不是一个文件");

        if (!stream.CanWrite)
            throw new Exception("流无法写入");

        if (fileData.Chunks.Count == 1)
        {
            var data = await steamSession.DownloadChunkDataWithRetryAsync(depotId, fileData.Chunks.First(), depotKey, 5, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (stream.CanSeek)
        {
            var startOffset = stream.Position;

            using SemaphoreSlim writeLock = new(1);
            var opt = new ParallelOptions()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 8,
            };
            await Parallel.ForEachAsync(fileData.Chunks, opt, async (chunk, cancellationToken) =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var data = await steamSession.DownloadChunkDataWithRetryAsync(depotId, chunk, depotKey, 5, cancellationToken).ConfigureAwait(false);

                try
                {
                    await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    stream.Position = startOffset + (long)chunk.Offset;
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                    writeLock.Release();
                }
            });
        }
        else
        {
            foreach (var item in fileData.Chunks.OrderBy(v => v.Offset))
            {
                var data = await steamSession.DownloadChunkDataWithRetryAsync(depotId, item, depotKey, 5, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task DownloadFileDataToDirectoryAsync(this SteamSession steamSession, string dir, uint appId, uint depotId, DepotManifest.FileData fileData, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(dir, fileData.FileName);
        if (fileData.Flags.HasFlag(EDepotFileFlag.Directory))
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(dir);
        using FileStream fs = new(path, FileMode.OpenOrCreate);

        if ((long)fileData.TotalSize == fs.Length)
        {
            var fileSHA1 = SHA1.HashData(fs);
            if (fileData.FileHash.SequenceEqual(fileSHA1))
            {
                return;
            }
            fs.Seek(0, SeekOrigin.Begin);
        }

        await steamSession.DownloadFileDataToStreamAsync(fs, appId, depotId, fileData, cancellationToken).ConfigureAwait(false);
    }

    public static async Task DownloadDepotManifestToDirectoryAsync(this SteamSession steamSession, string dir, uint appId, uint depotId, DepotManifest depotManifest, CancellationToken cancellationToken = default)
    {
        var depotKey = await steamSession.GetDepotKeyAsync(appId, depotId);
        await DownloadDepotManifestToDirectoryAsync(steamSession, dir, depotId, depotKey, depotManifest, cancellationToken);
    }

    public static async Task DownloadDepotManifestToDirectoryAsync(this SteamSession steamSession, string dir, uint depotId, byte[] depotKey, DepotManifest depotManifest, CancellationToken cancellationToken = default)
    {
        if (depotManifest.FilenamesEncrypted)
            throw new Exception("DepotManifest没有解密");

        if (depotManifest.Files is null)
            throw new Exception("DepotManifest.Files为null");

        Directory.CreateDirectory(dir);

        var flagsGroup = depotManifest.Files.GroupBy(v => v.Flags.HasFlag(EDepotFileFlag.Directory));
        var dirs = flagsGroup.FirstOrDefault(v => v.Key is true);
        var files = flagsGroup.FirstOrDefault(v => v.Key is false);

        if (dirs is { })
        {
            foreach (var item in dirs)
            {
                Directory.CreateDirectory(Path.Combine(dir, item.FileName));
            }
        }

        if (files is null)
            return;

        var sizeGroup = files.OrderByDescending(v => v.TotalSize).GroupBy(v => v.TotalSize switch
        {
            <= 1024 * 1024 => "1mb",
            <= 10 * 1024 * 1024 => "10mb",
            _ => "max",
        });
        var sFiles = sizeGroup.FirstOrDefault(v => v.Key is "1mb");
        var lFiles = sizeGroup.FirstOrDefault(v => v.Key is "10mb");
        var maxFiles = sizeGroup.FirstOrDefault(v => v.Key is "max");

        if (maxFiles is { })
            await ParallelForEachAsync(maxFiles, 1).ConfigureAwait(false);
        if (lFiles is { })
            await ParallelForEachAsync(lFiles, 3).ConfigureAwait(false);
        if (sFiles is { })
            await ParallelForEachAsync(sFiles, 10).ConfigureAwait(false);

        async ValueTask ParallelForEachAsync(IEnumerable<DepotManifest.FileData> fileDatas, int maxDegreeOfParallelism)
        {
            var opt = new ParallelOptions()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            };
            await Parallel.ForEachAsync(fileDatas, opt, async (fileData, cancellationToken) =>
            {
                var fullPath = Path.Combine(dir, fileData.FileName);
                var d = Path.GetDirectoryName(fullPath)!;
                if (!Directory.Exists(d))
                    Directory.CreateDirectory(d);

                using FileStream fs = new(fullPath, FileMode.OpenOrCreate);

                if ((long)fileData.TotalSize == fs.Length)
                {
                    var fileSHA1 = SHA1.HashData(fs);
                    if (fileData.FileHash.SequenceEqual(fileSHA1))
                    {
                        return;
                    }
                    fs.Seek(0, SeekOrigin.Begin);
                }

                await steamSession.DownloadFileDataToStreamAsync(fs, depotId, depotKey, fileData, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }


}

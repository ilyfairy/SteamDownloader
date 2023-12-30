using SteamKit2;
using System.Security.Cryptography;

namespace SteamDownloader;

public static class SteamSessionExtensions
{
    public static async Task<byte[]> DownloadChunkDecryptBytesRetryAsync(this SteamSession steamSession, uint depotId, DepotManifest.ChunkData chunkData, byte[] depotKey, int retry = 3, CancellationToken cancellationToken = default)
    {
        if(retry < 0)
            retry = 1;
        Exception exception = null!;
        for (int i = 0; i < retry; i++)
        {
            try
            {
                return await steamSession.DownloadChunkDecryptBytesAsync(depotId, chunkData, depotKey, cancellationToken);
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

    public static async Task DownloadFileDataToStreamAsync(this SteamSession steamSession, Stream stream, uint depotId, DepotManifest.FileData fileData, CancellationToken cancellationToken = default)
    {
        if (fileData.Flags.HasFlag(EDepotFileFlag.Directory))
            throw new Exception("FileData不是一个文件");

        if (!stream.CanWrite)
            throw new Exception("流无法写入");

        var depotKey = await steamSession.GetDepotKeyAsync(depotId);

        if(fileData.Chunks.Count == 1)
        {
            var data = await steamSession.DownloadChunkDecryptBytesRetryAsync(depotId, fileData.Chunks.First(), depotKey, 5, cancellationToken);
            await stream.WriteAsync(data, cancellationToken);
            return;
        }

        if (stream.CanSeek)
        {
            var startOffset = stream.Position;

            SemaphoreSlim writeLock = new(1);
            var opt = new ParallelOptions()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 8,
            };
            await Parallel.ForEachAsync(fileData.Chunks, opt, async (chunk, cancellationToken) =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var data = await steamSession.DownloadChunkDecryptBytesRetryAsync(depotId, chunk, depotKey, 5, cancellationToken);

                try
                {
                    await writeLock.WaitAsync(cancellationToken);
                    stream.Position = startOffset + (long)chunk.Offset;
                    await stream.WriteAsync(data, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
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
            Console.WriteLine($"不能随机读写");
            foreach (var item in fileData.Chunks.OrderBy(v => v.Offset))
            {
                var data = await steamSession.DownloadChunkDecryptBytesRetryAsync(depotId, item, depotKey, 5, cancellationToken);
                await stream.WriteAsync(data, cancellationToken);
            }
        }
    }

    public static async Task DownloadFileDataToDirectory(this SteamSession steamSession, string dir, uint depotId, DepotManifest.FileData fileData, CancellationToken cancellationToken = default)
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

        await steamSession.DownloadFileDataToStreamAsync(fs, depotId, fileData, cancellationToken);
    }

    public static async Task DownloadDepotManifestToDirectoryAsync(this SteamSession steamSession, string dir, uint depotId, DepotManifest depotManifest, CancellationToken cancellationToken = default)
    {
        if (depotManifest.FilenamesEncrypted)
            throw new Exception("DepotManifest没有解密");

        if (depotManifest.Files is null)
            throw new Exception("DepotManifest.Files为null");

        Directory.CreateDirectory(dir);

        var flagsGroup = depotManifest.Files.GroupBy(v => v.Flags.HasFlag(EDepotFileFlag.Directory));
        var dirs = flagsGroup.FirstOrDefault(v => v.Key is true);
        var files = flagsGroup.FirstOrDefault(v => v.Key is false);

        if(dirs is { })
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

        if(maxFiles is { })
            await ParallelForEachAsync(maxFiles, 1);
        if(lFiles is { })
            await ParallelForEachAsync(lFiles, 5);
        if(sFiles is { })
            await ParallelForEachAsync(sFiles, 10);

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
                var d = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(d))
                    Directory.CreateDirectory(d);

                using FileStream fs = new(fullPath, FileMode.OpenOrCreate);

                if ((long)fileData.TotalSize == fs.Length)
                {
                    var fileSHA1 = SHA1.HashData(fs);
                    if (fileData.FileHash.SequenceEqual(fileSHA1))
                    {
                        await Console.Out.WriteLineAsync($"文件已存在: {fs.Name}");
                        return;
                    }
                    fs.Seek(0, SeekOrigin.Begin);
                }

                await steamSession.DownloadFileDataToStreamAsync(fs, depotId, fileData, cancellationToken);
                await Console.Out.WriteLineAsync($"下载完成: {fs.Name}");
            });
        }
    }


}

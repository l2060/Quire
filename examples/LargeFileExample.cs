using System.Buffers;
using System.Security.Cryptography;

namespace Quire.Examples
{
    internal static class LargeFileExample
    {
        // 与 examples/quire.project.yml 中的 256 KiB 数据块大小一致。
        private const int DefaultBufferSize = 256 * 1024;

        internal static async Task<int> RunAsync(string[] arguments)
        {
            if (arguments.Length < 3 || arguments.Length > 4)
            {
                Console.Error.WriteLine("Usage: Quire.Examples --large-file PACKAGE_DIRECTORY ASSET_PATH [CLIENT_KEY_FILE]");
                return 1;
            }

            string packageDirectory = arguments[1];
            string assetPath = arguments[2];
            AssetKey? key = null;
            if (arguments.Length == 4)
            {
                key = new AssetKey(File.ReadAllBytes(arguments[3]));
            }

            ReadOptions readOptions = new ReadOptions
            {
                Key = key,
                ReadMode = AssetReadMode.RandomAccess,
                // 此入口只使用流式读取，大文件无需提高完整读取的内存上限。
                MaxBufferedAssetBytes = 0
            };

            using (CancellationTokenSource cancellationSource = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancelHandler = (object? sender, ConsoleCancelEventArgs eventArguments) =>
                {
                    eventArguments.Cancel = true;
                    cancellationSource.Cancel();
                };

                Console.CancelKeyPress += cancelHandler;
                try
                {
                    CancellationToken cancellationToken = cancellationSource.Token;
                    using (QuireBundle bundle = await QuireBundle.OpenAsync(packageDirectory, readOptions, cancellationToken))
                    {
                        if (!bundle.FileExists(assetPath))
                        {
                            Console.Error.WriteLine($"Asset not found: {assetPath}");
                            return 1;
                        }

                        using (IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            Console.WriteLine($"Reading '{assetPath}' in {DefaultBufferSize} byte chunks. Press Ctrl+C to cancel.");

                            // 示例消费者逐块计算摘要，不保存块内容，也不分配整文件缓冲区。
                            // 若消费者要异步写入或解析，可在其实现中 await，并返回 ValueTask。
                            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> consumeChunkAsync =
                                (ReadOnlyMemory<byte> chunk, CancellationToken consumerCancellationToken) =>
                                {
                                    consumerCancellationToken.ThrowIfCancellationRequested();
                                    contentHash.AppendData(chunk.Span);
                                    return ValueTask.CompletedTask;
                                };

                            long totalBytesRead = await ReadFileInChunksAsync(
                                bundle,
                                assetPath,
                                consumeChunkAsync,
                                DefaultBufferSize,
                                cancellationToken);

                            byte[] digest = contentHash.GetHashAndReset();
                            Console.WriteLine($"Read {totalBytesRead} bytes.");
                            Console.WriteLine($"SHA-256: {Convert.ToHexString(digest)}");
                        }
                    }

                    return 0;
                }
                catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Reading canceled.");
                    return 130;
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
        }

        /// <summary>顺序读取整个文件，每次等待消费者完成后再复用缓冲区。</summary>
        /// <remarks>
        /// 消费者只能在本次调用完成前使用传入的内存；如需保留内容，必须自行复制。
        /// 同一个读取器不执行并发读取。总字节数使用 long，支持超过 4 GiB 的资源。
        /// 分块读取不比较包内保存的整文件摘要；本示例的摘要供调用者使用。
        /// </remarks>
        internal static async Task<long> ReadFileInChunksAsync(
            QuireBundle bundle,
            string assetPath,
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> consumeChunkAsync,
            int bufferSize,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentNullException.ThrowIfNull(consumeChunkAsync);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
            cancellationToken.ThrowIfCancellationRequested();

            using (AssetReader assetReader = bundle.OpenRead(assetPath))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                try
                {
                    // 池可能返回更大的数组，读取范围仍限制为请求的缓冲区大小。
                    Memory<byte> readBuffer = buffer.AsMemory(0, bufferSize);
                    long totalBytesRead = 0;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int bytesRead = await assetReader.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        // 最后一块可能不足 bufferSize；消费者只接收有效数据。
                        // await 完成前不发起下一次读取，缓冲区不会被提前覆盖。
                        ReadOnlyMemory<byte> chunk = readBuffer.Slice(0, bytesRead);
                        await consumeChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                        totalBytesRead += bytesRead;
                    }

                    return totalBytesRead;
                }
                finally
                {
                    // 成功、取消、读取失败或消费者异常时，都归还并清理缓冲区。
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
        }
    }
}

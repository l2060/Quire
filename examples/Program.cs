using System.Text;

namespace Quire.Examples
{
    internal static class Program
    {
        private static async Task<int> Main(string[] arguments)
        {
            if (arguments.Length > 0 && arguments[0] == "--large-file")
            {
                return await LargeFileExample.RunAsync(arguments);
            }

            arguments = new string[]
            {
                "D:\\SourceCode\\Quire\\examples\\dist\\22df3eb9276944b4995bdff6ad67c678\\",
                "D:\\SourceCode\\Quire\\examples\\client.key"
            };

            if (arguments.Length < 1 || arguments.Length > 2)
            {
                Console.WriteLine("Usage: Quire.Examples PACKAGE_DIRECTORY [CLIENT_KEY_FILE]");
                return 1;
            }

            AssetKey? key = null;
            if (arguments.Length == 2)
            {
                key = new AssetKey(File.ReadAllBytes(arguments[1]));
            }

            ReadOptions readOptions = new ReadOptions
            {
                Key = key
            };

            // 1. 打开目录：递归发现 .asset，并按打包时间合并基础包与补丁。
            using (QuireBundle QuireBundle = await QuireBundle.OpenAsync(arguments[0], readOptions))
            {
                foreach (AssetEntryInfo assetInfo in QuireBundle.EnumerateFiles())
                {
                    Console.WriteLine($"{assetInfo.Path}: {assetInfo.Length} bytes, meta={assetInfo.HasMetadata}");
                }

                foreach (string directoryPath in QuireBundle.EnumerateDirectories(string.Empty, recursive: false))
                {
                    Console.WriteLine($"Directory: {directoryPath}");
                }

                // 文件浏览器：一次取得当前层级的完整数组，根目录使用 string.Empty。
                AssetEntryInfo[] rootEntries = QuireBundle.Dir(string.Empty);
                foreach (AssetEntryInfo entry in rootEntries)
                {
                    Console.WriteLine($"Root {entry.Kind}: {entry.Path}");
                }

                // 异步版本在分页索引读取时使用异步 I/O，也可传入 CancellationToken。
                AssetEntryInfo[] entries = await QuireBundle.DirAsync("common/data");
                foreach (AssetEntryInfo entry in entries)
                {
                    if (entry.IsDirectory)
                        Console.WriteLine($"Browse directory: {entry.Path}");
                    else
                        Console.WriteLine($"Browse file: {entry.Path}, {entry.Length} bytes, meta={entry.HasMetadata}");
                }

                // 可选流式 API：适合逐项处理或提前停止，不预先分配完整数组。
                await foreach (AssetEntryInfo entry in QuireBundle.EnumerateEntriesAsync("common/data"))
                {
                    Console.WriteLine($"Stream {entry.Kind}: {entry.Path}");
                }

                const string assetPath = "common/data/settings.txt";
                if (!QuireBundle.FileExists(assetPath))
                {
                    Console.Error.WriteLine($"The example needs '{assetPath}'. Package examples/quire.project.yml first.");
                    return 1;
                }

                // 2. 读取可选 Meta：属性不存在或类型不匹配时 TryGet 返回 false，不抛异常。
                Metadata metadata = await QuireBundle.ReadMetadataAsync(assetPath);
                if (metadata.TryGetString("name", out string? name))
                {
                    Console.WriteLine($"Name: {name}");
                }

                if (metadata.TryGetInt64("revision", out long revision))
                {
                    Console.WriteLine($"Revision: {revision}");
                }

                double scale = 1.0;
                if (metadata.TryGetDouble("scale", out double scaleValue))
                {
                    scale = scaleValue;
                }

                Console.WriteLine($"Scale: {scale}");

                if (metadata.TryGetBoolean("enabled", out bool enabled))
                {
                    Console.WriteLine($"Enabled: {enabled}");
                }

                if (metadata.TryGetStringArray("tags", out IReadOnlyList<string>? tags))
                {
                    Console.WriteLine($"Tags: {string.Join(", ", tags)}");
                }

                if (metadata.TryGetInt64Array("levels", out ReadOnlySpan<long> levels))
                {
                    Console.WriteLine($"Levels: {string.Join(", ", levels.ToArray())}");
                }

                if (metadata.TryGetDoubleArray("weights", out ReadOnlySpan<double> weights))
                {
                    Console.WriteLine($"Weights: {string.Join(", ", weights.ToArray())}");
                }

                if (metadata.TryGetBooleanArray("switches", out ReadOnlySpan<bool> switches))
                {
                    Console.WriteLine($"Switches: {string.Join(", ", switches.ToArray())}");
                }

                if (metadata.TryGetValueType("name", out MetadataType nameType))
                {
                    Console.WriteLine($"Name type: {nameType}");
                }

                if (!metadata.TryGetString("optional", out string? optionalValue))
                {
                    Console.WriteLine("Optional property is absent.");
                }

                // 3. 小文件一次读入：AssetData 持有池化缓冲，使用后 Dispose。
                using (AssetData fileData = await QuireBundle.ReadAsync(assetPath))
                {
                    Console.WriteLine(Encoding.UTF8.GetString(fileData.Data.Span));
                }

                // 4. 延迟加载完整数据：创建 Lazy 时不读内容，首次访问 Value 才开始读取。
                Lazy<Task<AssetData>> deferredFileData = new Lazy<Task<AssetData>>(() =>
                {
                    return QuireBundle.ReadAsync(assetPath).AsTask();
                });

                Console.WriteLine($"Content requested: {deferredFileData.IsValueCreated}");
                using (AssetData fileData = await deferredFileData.Value)
                {
                    Console.WriteLine($"Deferred content: {fileData.Data.Length} bytes");
                }

                // deferredFileData.Value 缓存的是同一个 AssetData；上面释放后不要再次使用其 Data。

                // 5. 大文件按需读取：OpenRead 不读取内容；ReadAsync 只加载需要的块。
                byte[] prefixBuffer = new byte[16];
                using (Stream assetStream = QuireBundle.OpenRead(assetPath))
                {
                    int prefixBytesRead = await assetStream.ReadAsync(prefixBuffer);
                    Console.WriteLine($"First {prefixBytesRead} bytes: {Convert.ToHexString(prefixBuffer.AsSpan(0, prefixBytesRead))}");

                    // 范围读取不依赖流的位置；多个调用可以并行读取同一资源。
                    byte[] tailBuffer = new byte[Math.Min(8L, assetStream.Length)];
                    await QuireBundle.ReadAtAsync(assetPath, assetStream.Length - tailBuffer.Length, tailBuffer);
                    Console.WriteLine($"Tail: {Convert.ToHexString(tailBuffer)}");
                }

                // 高频范围读取复用同一读取器，不改变流位置。
                using (AssetReader reusableReader = QuireBundle.OpenRead(assetPath))
                {
                    int prefixBytesRead = reusableReader.ReadAt(0, prefixBuffer);
                    Console.WriteLine($"Reusable reader: {prefixBytesRead} bytes, position={reusableReader.Position}");
                }
            }

            return 0;
        }
    }
}

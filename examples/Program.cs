using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Quire.Package;

namespace Quire.Package.Examples
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
                "123456"
            };

            if (arguments.Length < 1 || arguments.Length > 2)
            {
                Console.WriteLine("Usage: Quire.Package.Examples PACKAGE_DIRECTORY [PASSWORD]");
                return 1;
            }

            string? password = null;
            if (arguments.Length == 2)
            {
                password = arguments[1];
            }

            ReadOptions readOptions = new ReadOptions
            {
                Password = password
            };

            // 1. 打开目录：递归发现 .asset，并按打包时间合并基础包与补丁。
            using (PackageSet packageSet = await PackageSet.OpenAsync(arguments[0], readOptions))
            {
                foreach (AssetInfo assetInfo in packageSet.EnumerateFiles())
                {
                    Console.WriteLine($"{assetInfo.Path}: {assetInfo.Length} bytes, meta={assetInfo.HasMetadata}");
                }

                foreach (string directoryPath in packageSet.EnumerateDirectories(string.Empty, recursive: false))
                {
                    Console.WriteLine($"Directory: {directoryPath}");
                }

                const string assetPath = "common/data/settings.txt";
                if (!packageSet.FileExists(assetPath))
                {
                    Console.Error.WriteLine($"The example needs '{assetPath}'. Package examples/quire.project.yml first.");
                    return 1;
                }

                // 2. 读取可选 Meta：属性不存在或类型不匹配时 TryGet 返回 false，不抛异常。
                Metadata metadata = await packageSet.ReadMetadataAsync(assetPath);
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
                using (AssetData fileData = await packageSet.ReadAsync(assetPath))
                {
                    Console.WriteLine(Encoding.UTF8.GetString(fileData.Data.Span));
                }

                // 4. 延迟加载完整数据：创建 Lazy 时不读内容，首次访问 Value 才开始读取。
                Lazy<Task<AssetData>> deferredFileData = new Lazy<Task<AssetData>>(() =>
                {
                    return packageSet.ReadAsync(assetPath).AsTask();
                });

                Console.WriteLine($"Content requested: {deferredFileData.IsValueCreated}");
                using (AssetData fileData = await deferredFileData.Value)
                {
                    Console.WriteLine($"Deferred content: {fileData.Data.Length} bytes");
                }

                // deferredFileData.Value 缓存的是同一个 AssetData；上面释放后不要再次使用其 Data。

                // 5. 大文件按需读取：OpenRead 不读取内容；ReadAsync 只加载需要的块。
                byte[] prefixBuffer = new byte[16];
                using (Stream assetStream = packageSet.OpenRead(assetPath))
                {
                    int prefixBytesRead = await assetStream.ReadAsync(prefixBuffer);
                    Console.WriteLine($"First {prefixBytesRead} bytes: {Convert.ToHexString(prefixBuffer.AsSpan(0, prefixBytesRead))}");

                    // 范围读取不依赖流的位置；多个调用可以并行读取同一资源。
                    byte[] tailBuffer = new byte[Math.Min(8L, assetStream.Length)];
                    await packageSet.ReadAtAsync(assetPath, assetStream.Length - tailBuffer.Length, tailBuffer);
                    Console.WriteLine($"Tail: {Convert.ToHexString(tailBuffer)}");
                }

                // 高频范围读取复用同一读取器，不改变流位置。
                using (AssetReader reusableReader = packageSet.OpenRead(assetPath))
                {
                    int prefixBytesRead = reusableReader.ReadAt(0, prefixBuffer);
                    Console.WriteLine($"Reusable reader: {prefixBytesRead} bytes, position={reusableReader.Position}");
                }
            }

            return 0;
        }
    }
}

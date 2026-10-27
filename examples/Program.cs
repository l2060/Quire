using System.Text;
using Quire.Package;

if (args.Length is < 1 or > 2)
{
    Console.WriteLine("Usage: Quire.Package.Examples PACKAGE_DIRECTORY [PASSWORD]");
    return 1;
}

// 1. 打开目录：递归发现 .asset，并按打包时间合并基础包与补丁。
using var assets = await AssetPackageSet.OpenDirectoryAsync(args[0],
    new AssetReadOptions { Password = args.Length == 2 ? args[1] : null });
foreach (AssetInfo info in assets.Enumerate())
    Console.WriteLine($"{info.Path}: {info.Length} bytes, meta={info.HasMetadata}");

const string path = "common/data/settings.txt";
if (!assets.Contains(path))
{
    Console.Error.WriteLine($"The example needs '{path}'. Package examples/quire.project.yml first.");
    return 1;
}

// 2. 读取 Meta：独立于资源内容，可在加载文件数据前获取。
AssetMetadata meta = await assets.ReadMetadataAsync(path);
Console.WriteLine($"Name: {meta.GetString("name")}, revision: {meta.GetInt64("revision")}");
Console.WriteLine($"Scale: {meta.GetDouble("scale")}, enabled: {meta.GetBoolean("enabled")}");
Console.WriteLine($"Tags: {string.Join(", ", meta.GetStringArray("tags"))}");
Console.WriteLine($"Levels: {string.Join(", ", meta.GetInt64Array("levels").ToArray())}");
Console.WriteLine($"Weights: {string.Join(", ", meta.GetDoubleArray("weights").ToArray())}");
Console.WriteLine($"Switches: {string.Join(", ", meta.GetBooleanArray("switches").ToArray())}");
if (!meta.TryGetString("optional", out _)) Console.WriteLine("Optional property is absent.");

// 3. 小文件一次读入：AssetData 持有池化缓冲，使用后 Dispose。
using (AssetData file = await assets.ReadAsync(path))
    Console.WriteLine(Encoding.UTF8.GetString(file.Data.Span));

// 4. 延迟加载完整数据：创建 Lazy 时不读内容，首次访问 Value 才开始读取。
var deferred = new Lazy<Task<AssetData>>(() => assets.ReadAsync(path).AsTask());
Console.WriteLine($"Content requested: {deferred.IsValueCreated}");
using (AssetData file = await deferred.Value)
    Console.WriteLine($"Deferred content: {file.Data.Length} bytes");
// deferred.Value 缓存的是同一个 AssetData；上面释放后不要再次使用其 Data。

// 5. 大文件按需读取：OpenRead 不读取内容；ReadAsync 只加载需要的块。
using Stream stream = assets.OpenRead(path);
byte[] prefix = new byte[16];
int read = await stream.ReadAsync(prefix);
Console.WriteLine($"First {read} bytes: {Convert.ToHexString(prefix.AsSpan(0, read))}");

// 范围读取不依赖流的位置；多个调用可以并行读取同一资源。
byte[] tail = new byte[Math.Min(8L, stream.Length)];
await assets.ReadAtAsync(path, stream.Length - tail.Length, tail);
Console.WriteLine($"Tail: {Convert.ToHexString(tail)}");
return 0;

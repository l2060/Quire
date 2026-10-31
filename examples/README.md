# Quire 示例

从 Quire 仓库根目录运行。示例项目只引用 `Quire`；示例包已开启加密，块大小为 256 KiB：

```powershell
dotnet run --project quire-cli -c Release -- keys add example "your-high-entropy-password"
dotnet run --project quire-cli -c Release -- keys export example examples/client.key
dotnet run --project quire-cli -c Release -- package -f examples/quire.project.yml -tag example-first
```

已安装 CLI 时，也可以进入 `examples` 目录直接运行 `quire-cli package`，它会读取当前目录的 `quire.project.yml`。示例配置使用 `suffix: asset`、`build.jobs: 0`；0 根据 CPU 数和内存预算自动选择构建并行度，`-jobs N` 可覆盖。

`package` 只生成候选，默认配置直接生成 `examples/dist/common.asset` 和 `examples/dist/locale.asset`，不自动追加 Tag 子目录。测试通过后执行 `dotnet run --project quire-cli -c Release -- commit example-first -f examples/quire.project.yml`，后续打包才会相对它生成增量。省略 `-tag` 会自动生成名称；`changes NAME` 查看该 Tag 打包时记录的历史 A/M/D 路径，不扫描当前源文件；`diff` 扫描当前源文件，与已提交的正式基线比较。`remove NAME` 仅移除未提交候选记录，保留产物；不支持 `-base` 或历史 Tag 重打。

查看当前变化可运行 `quire-cli diff -f examples/quire.project.yml`。即使示例配置开启加密，`diff` 也不需要密码或本机凭据。扫描和哈希使用内部默认并发；`package -jobs` 只控制打包时的压缩/加密工作线程。

`package` / `diff` 可加 `-root D:/game_root`，使示例中的相对源目录指向 `D:/game_root/assets/common` 和 `D:/game_root/assets/locale`。换一台机器可用 `-root E:/new_root`，无需修改 YAML 或重置索引。`-root` 自身支持绝对路径及相对命令执行目录的路径；省略时仍以 YAML 所在目录为基准。YAML 中绝对的 `sources.directory` 保持原路径，输出仍在 `examples/dist`，基线仍在 `examples/.quire/example/`。其他命令不接受 `-root`。

示例默认 `output: ./dist` 使用平铺目录和静态包名；后续需要生成同名包时会报错，不会覆盖已有文件。要保留多次构建的包，可将输出改为 `output: ./dist/{tag}`，或保持平铺目录并使用 `name: "common-{tag}"`、`name: "locale-{tag}"`。`output` 直接表示最终目录，支持与包名相同的 12 个变量；目录和包名共用本次构建的 Tag、内部 ID 和冻结的时间。使用固定父目录 `./dist/{tag}` 时，整个 `dist` 输出区域自动排除在源扫描之外。输出目录已有的无关文件会保留。

同目录的另一份配置（例如 `quire.patch.yml`）设置相同的 `project: example` 时，共用 `.quire/example/` 基线。`project` 必填且不能为空。可以让完整配置分多个包，补丁配置集中写一个 `Patch` 包，只要扫描得到的逻辑路径保持一致。差异按逻辑路径、长度、内容和 Meta 摘要判断；换包名、压缩算法、块大小、加密开关都不会重写未变资源。新增和内容变化按补丁配置写入，内容换包只显示 `M`；单包配置的删除也写入 `Patch.asset`。补丁配置应覆盖当前完整资源集合，未扫描到的旧路径会作为删除处理。

补丁包的 `name` 可设为 `"Patch-{tag}-{yyyy}{MM}{dd}_{HH}{mm}{ss}"`；例如 `-tag example-patch` 在本地时间 2026-10-31 09:08:07 构建时，产物名为 `Patch-example-patch-20261031_090807.asset`。支持 `{tag}`（本次 Tag 名称）、`{tagid}`（内部 8 位小写十六进制 ID）、`{yyyy}`、`{MM}`、`{dd}`、`{HH}`、`{mm}`、`{ss}`、`{fff}`，以及 `{date}`（`yyyyMMdd`）、`{time}`（`HHmmss`）、`{timestamp}`（Unix 秒）。变量区分大小写，不支持未知变量、合并格式 `{yyyyMMdd}` 或 `|` 任选语法；变量值只展开一次。本地日期时间取自本次构建统一冻结的时间，所有分包一致，数字格式不受系统语言影响。

展开后的名称允许 Unicode、点、内部空格及开头的 `-`，必须是合法单个文件名组件；不能包含路径分隔符、Windows 文件名非法字符或控制字符，不能有首尾空白、末尾点，也不能使用设备保留名。完整的 `<包名>.<suffix>` 最多 255 个 UTF-8 字节，展开后不能出现忽略大小写的重名；错误在扫描源文件前报告。`diff` 显示包名模板，不生成 Tag 或内部 ID；`changes` 显示实际构建名称。动态包名不会导致未变资源重新打包，示例配置保留静态的 `common` 和 `locale` 包名。

跨包重复映射的逻辑路径按配置中未展开的 `name` 模板使用 Ordinal 排序选择最后一个来源，首次全量也只打包这个有效来源；Tag 和时间变量不改变来源优先级。只修改 Meta 时，补丁写入 Meta-only 记录，原内容及其编码保持不变；基线在 `commit` 后才更新。CLI 不再依赖 `key.salt` 或密码指纹。

示例读取目录应只包含选定的正式包链和本次候选。首次使用默认配置时读取 `examples/dist` 即可；若后来在该目录中保存了其他未提交候选，递归加载根目录也会加载这些包。使用 `output: ./dist/{tag}` 时，可以将选定 Tag 的产物整理到独立目录后读取。

当前资源包格式为 6，旧版本包需要重新打包。每个包记录所属 Tag 的 UID、名称、打包时间、自身 PackageUid，以及该 Tag 本次构建的全部分包清单；它不记录父 Tag 依赖。

`build.memory-mib` 预算按每个工作线程 7 个块加公共预留估算管线缓冲，计入池化缓冲的容量取整；它不是进程总内存或 zlib 原生分配的硬上限。

全局默认使用 `compression: lz4` 和 `compression-policy: adaptive`，`locale` 包通过 `compression: none` 关闭压缩。算法可选 `none`、`lz4`、`zlib`；策略可选 `adaptive`（仅保留更小的压缩结果）或 `always`（即使变大也保存压缩结果）。每个包分别继承或覆盖这两个字段；AES-256-GCM 加密独立配置。

这些输出设置只影响本次实际写入的数据，不转换历史包。先用 `keys add example PASSWORD` 添加当前用户的全局命名密钥；示例 YAML 通过 `secret-key: example` 引用它，后续构建无需再提供密码。`keys` 不读取项目配置、不接受 `-f`，从任意目录执行都访问同一密钥库，多个项目也可引用同名密钥。相同密码生成相同客户端密钥，密钥名称不参与密码派生。

显式导出的 `examples/client.key` 共 96 字节，包含 32 字节 AES 主密钥及 P-256 公钥 X、Y 坐标（各 32 字节大端整数），不包含密码或签名私钥。应用用 `new AssetKey(File.ReadAllBytes(path))` 导入；`inspect`、`verify`、`unpackage` 读取签名包时必须传入 `-key FILE`，不会自动查找项目或全局密钥。打包命令不接受 `-pwd` 或 `-pwd-env`，YAML 不支持 `encryption` 或密码字段。来自不同发布密钥的包可通过 `ReadOptions.KeyProvider` 选择各包的可信客户端密钥。修改 `suffix` 后，CLI 使用 `-suffix`，读取选项设置对应的 `Suffix`。

客户端密钥也可保存为文本配置中的标准 Base64 字符串。`quire-cli keys show example` 只输出一行 128 字符的密钥，不写文件、不依赖项目，可直接传给 `new AssetKey(string)`。也可从二进制密钥转换：

```csharp
var clientKey = new AssetKey(File.ReadAllBytes("examples/client.key"));
string base64Key = clientKey.ExportBase64();
var optionsFromText = new ReadOptions { Key = new AssetKey(base64Key) };
```

字符串表示完整的 96 字节客户端材料，编码后为 128 个字符（无填充，字符串内部无换行），不表示初始化密码。`keys list` 显示 `FINGERPRINT`、`KEYNAME`、`CLIENTKEY` 三列，其中 `CLIENTKEY` 就是该字符串。`keys export KEYNAME FILE` 输出二进制文件，不能直接用 `File.ReadAllText` 读取为该字符串。旧的 64 字节客户端密钥和格式 5 资源包不兼容；升级时归档旧 `.quire` 和输出目录，用原密码添加全局密钥、显式导出客户端材料并打全量包。

`secret-key: example` 要求全局密钥存在，生成加密且签名的包；`secret-key: none` 或省略该字段时生成未加密、未签名的包。`keys remove example` 删除本机凭据，之后引用它的打包会失败，直到重新添加；已有包和导出的客户端文件不受影响。使用 `Key` 或 `KeyProvider` 打开包集合会拒绝未签名包。客户端材料必须从可信渠道取得。

## 基础示例

`Program.cs` 展示打开包、分别枚举文件与目录、独立读取八种 Meta 类型、完整读取小文件、延迟加载，以及流和偏移读取。**基础模式当前保留了本地调试参数覆盖**，会使用 `Program.Main` 中写定的目录和客户端密钥路径；调整或移除该覆盖后，下面的参数才会生效：

```powershell
dotnet run --project examples/Quire.Examples.csproj -c Release -- examples/dist examples/client.key
```

应用中可显式开启本次构建的分包完整性检查，并读取已加载包中的最新 Tag：

```csharp
using var assets = await QuireBundle.OpenAsync("examples/dist", new ReadOptions
{
    Key = new AssetKey(File.ReadAllBytes("examples/client.key")),
    VerifyPackageChain = true
});
if (assets.LatestTag is { } latest)
    Console.WriteLine($"{latest.Name} ({latest.TagUid}) | {latest.BuildTimeUtc:O}");
```

`VerifyPackageChain` 默认关闭。开启后，同一个已发现 Tag 少了分包、UID 不符或 PackageUid 重复会使打开失败；文件名仅用于报错，允许重命名，但仍需匹配配置的包后缀。关闭时仍验证包头、每个物理包的清单认证与格式、同 Tag 元数据一致性。校验只涉及该 Tag 本次生成的包，不要求父 Tag 存在，也不检查发布或提交状态。某个 Tag 的包全部缺失时无法凭目录发现它；零变化且无产物的 Tag 也不会显示在 `LatestTag`。`LatestTag` 按打包时间及 TagUid 选取，空目录为 `null`，不改变资源覆盖顺序。

`Lazy<Task<AssetData>>` 首次访问 `Value` 时加载并缓存结果，所有使用者完成后释放一次 `AssetData`。释放后不得继续使用其 `Data`，Meta 可以独立保留。高频访问同一文件可复用 `OpenRead` 返回的 `AssetReader`；`ReadAt` / `ReadAtAsync` 不改变流位置，同一读取器不能并发执行多个操作。

完整读取的 `Read` / `ReadAsync` 可通过单次调用的 `verifyContentHash` 参数决定是否校验内容 SHA-256，例如 `assets.ReadAsync(path, verifyContentHash: false)`。省略参数时采用 `ReadOptions.VerifyContentHash`，集合默认值为 `true`；同一集合中不同文件、不同调用可分别覆盖，参数只影响这一次读取。签名包仍需提供客户端密钥，并继续执行块签名摘要、适用的 AES-256-GCM 认证以及格式和解压检查。`Verify` / `VerifyAsync` 和 CLI 解包始终校验完整内容；范围读取、`OpenRead` 返回的流和 `CopyToAsync` 本身不执行整文件摘要比较。

## 大文件分块异步读取

命令格式为 `--large-file PACKAGE_DIRECTORY ASSET_PATH [CLIENT_KEY_FILE]`。此模式在基础模式的本地参数覆盖前分派，独立使用实际命令行参数：

```powershell
dotnet run --project examples/Quire.Examples.csproj -c Release -- --large-file examples/dist common/data/settings.txt examples/client.key
```

`PACKAGE_DIRECTORY` 是包含包的磁盘目录；`ASSET_PATH` 是包内路径，使用 `/`，例如 `common/data/settings.txt`。将其替换为任意大文件的包内路径即可；空文件也适用。`CLIENT_KEY_FILE` 为可信客户端密钥文件；仅未签名、未加密的包可以省略。

`LargeFileExample.cs` 独立打开包，使用 `RandomAccess` 和 `MaxBufferedAssetBytes = 0`，只打开一次 `AssetReader`。它租用一个 256 KiB 的 `ArrayPool<byte>` 缓冲，顺序 `await ReadAsync`，按实际读取长度等待消费者处理完成，并用 `long` 累计字节数。示例消费者通过 `IncrementalHash` 按块计算 SHA-256，不保留整个文件；按 Ctrl+C 可取消，缓冲始终在 `finally` 中清零归还。

- 接入自己的异步处理逻辑时使用 `ReadFileInChunksAsync` 的消费回调。必须等待回调完成后再复用缓冲；回调不能保存传入的内存或在返回后继续访问它。
- 调用方读取缓冲不随文件大小增长；包索引、解压、解密和消费者仍可能需要额外内存。`MaxBufferedAssetBytes = 0` 限制完整缓冲读取，不是整个进程的内存上限。
- 256 KiB 与示例包的块大小一致。从文件开头顺序读取、缓冲大小为包块大小的整数倍时，完整块可直接解码到调用方缓冲，减少部分块的中间复制。1 MiB 是当前所有合法包块大小的整数倍，但占用更多应用缓冲；应按实际负载测量。
- `RandomAccess` 提供操作系统异步 I/O；`MemoryMapped` 仍在调用线程上复制，缺页可能阻塞。取消令牌传入打开、读取和消费者；解压、解密等同步 CPU 工作不会在单块处理中被立即打断。
- 默认顺序读取和消费，不使用 `Task.Run` 或一次性创建全部块的 `Task.WhenAll`。需要并发处理多个文件时限制并发数，并为每个并发操作分别持有读取器和缓冲。

只需把文件复制到目标流时可直接使用 `QuireBundle.CopyToAsync`。示例输出计算得到的摘要，不与包内保存的整文件摘要比较；需要完整校验所有物理包中的记录（包括被覆盖的历史资源）时使用 `VerifyAsync`。

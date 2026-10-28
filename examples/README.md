# Quire.Package 示例

从 Quire 仓库根目录运行。示例项目只引用 `Quire.Package`；示例包已开启加密，块大小为 256 KiB：

```powershell
$env:QUIRE_KEY = "your-password"
dotnet run --project quire-cli -c Release -- package -project examples/quire.project.yml
```

## 基础示例

`Program.cs` 展示打开包、分别枚举文件与目录、独立读取八种 Meta 类型、完整读取小文件、延迟加载，以及流和偏移读取。**基础模式当前保留了本地调试参数覆盖**，会使用 `Program.Main` 中写定的目录和密码；调整或移除该覆盖后，下面的参数才会生效：

```powershell
dotnet run --project examples/Quire.Package.Examples.csproj -c Release -- examples/dist $env:QUIRE_KEY
```

`Lazy<Task<AssetData>>` 首次访问 `Value` 时加载并缓存结果，所有使用者完成后释放一次 `AssetData`。释放后不得继续使用其 `Data`，Meta 可以独立保留。高频访问同一文件可复用 `OpenRead` 返回的 `AssetReader`；`ReadAt` / `ReadAtAsync` 不改变流位置，同一读取器不能并发执行多个操作。

## 大文件分块异步读取

命令格式为 `--large-file PACKAGE_DIRECTORY ASSET_PATH [PASSWORD]`。此模式在基础模式的本地参数覆盖前分派，独立使用实际命令行参数：

```powershell
dotnet run --project examples/Quire.Package.Examples.csproj -c Release -- --large-file examples/dist common/data/settings.txt $env:QUIRE_KEY
```

`PACKAGE_DIRECTORY` 是包含包的磁盘目录；`ASSET_PATH` 是包内路径，使用 `/`，例如 `common/data/settings.txt`。将其替换为任意大文件的包内路径即可；空文件也适用。读取密码须与打包密码相同；若配置 `encryption.enabled: false`，打包时不需要环境变量，读取时省略密码。

`LargeFileExample.cs` 独立打开包，使用 `RandomAccess` 和 `MaxBufferedAssetBytes = 0`，只打开一次 `AssetReader`。它租用一个 256 KiB 的 `ArrayPool<byte>` 缓冲，顺序 `await ReadAsync`，按实际读取长度等待消费者处理完成，并用 `long` 累计字节数。示例消费者通过 `IncrementalHash` 按块计算 SHA-256，不保留整个文件；按 Ctrl+C 可取消，缓冲始终在 `finally` 中清零归还。

- 接入自己的异步处理逻辑时使用 `ReadFileInChunksAsync` 的消费回调。必须等待回调完成后再复用缓冲；回调不能保存传入的内存或在返回后继续访问它。
- 调用方读取缓冲不随文件大小增长；包索引、解压、解密和消费者仍可能需要额外内存。`MaxBufferedAssetBytes = 0` 限制完整缓冲读取，不是整个进程的内存上限。
- 256 KiB 与示例包的块大小一致。从文件开头顺序读取、缓冲大小为包块大小的整数倍时，完整块可直接解码到调用方缓冲，减少部分块的中间复制。1 MiB 是当前所有合法包块大小的整数倍，但占用更多应用缓冲；应按实际负载测量。
- `RandomAccess` 提供操作系统异步 I/O；`MemoryMapped` 仍在调用线程上复制，缺页可能阻塞。取消令牌传入打开、读取和消费者；解压、解密等同步 CPU 工作不会在单块处理中被立即打断。
- 默认顺序读取和消费，不使用 `Task.Run` 或一次性创建全部块的 `Task.WhenAll`。需要并发处理多个文件时限制并发数，并为每个并发操作分别持有读取器和缓冲。

只需把文件复制到目标流时可直接使用 `PackageSet.CopyToAsync`。示例输出计算得到的摘要，不与包内保存的整文件摘要比较；需要完整校验所有物理包中的记录（包括被覆盖的历史资源）时使用 `VerifyAsync`。

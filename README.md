# Quire

**面向 .NET 应用的资源打包、增量发布与运行时加载工具。**

Quire 将普通文件及其元数据打包为 `.asset` 文件，在应用中通过逻辑路径读取。它适合游戏资源、桌面应用内容和其他需要分包、压缩、加密或增量更新的资源集。

| 组件 | 用途 |
| --- | --- |
| **quire-cli** | 使用 YAML 构建资源包，管理发布基线和 Tag，查看、校验及解包 |
| **Quire** | C# 类库，加载基础包与补丁，查询目录、读取内容和元数据 |

构建端使用 CLI，应用端只需引用类库。`Quire` 类库没有外部 NuGet 依赖；LZ4 源码内嵌，zlib、加密与签名使用 .NET 提供的实现。

[快速开始](#快速开始) · [C# 接入](#c-接入) · [增量发布](#增量发布) · [项目配置](#项目配置) · [GUI 与自动化](#gui-与自动化) · [从源码构建](#从源码构建)

## 能力

- **资源编排**：多个源目录、多个资源包、逻辑路径前缀、文件匹配规则及 `.assetignore`。
- **增量发布**：按内容识别新增、更新和删除；先生成候选包，测试后再提交基线；保留每次构建的文件变更记录。
- **压缩与认证**：LZ4 / zlib，始终压缩或按收益压缩；可启用 AES-256-GCM 加密与 ECDSA P-256 签名。
- **目录浏览**：一次查询直接子目录和文件，提供数组与流式枚举接口。
- **按需读取**：同步与异步读取、流式读取、随机范围读取、内存映射及可配置索引预算；支持超过 4 GiB 的资源。
- **类型化元数据**：文件旁的 `.meta` 自动关联资源，支持字符串、整数、浮点数、布尔值及其数组。
- **完整性检查**：逐文件控制内容哈希校验，可检查已加载 Tag 的分包是否缺失，并查询最新 Tag 与构建时间。
- **GUI 接入**：`-progress` 输出结构化进度、查询结果和错误，无需解析终端文本。

## 安装

当前目标框架为 **.NET 10**。使用 .NET 工具安装 CLI 和从源码构建需要 .NET 10 SDK；应用项目需使用兼容的目标框架。

CLI 和类库面向 Windows、Linux、macOS；[持续集成](.github/workflows/ci.yml)分别构建并运行常规测试。CLI 引用微软官方 `System.Security.Cryptography.ProtectedData`，仅在 Windows 上用于 DPAPI；Linux/macOS 使用用户私有目录及文件权限保存本机密钥。跨平台部署使用 `.asset` 和导出的客户端密钥，Windows 本机发布凭据不能直接复制到其他系统，可用相同密码重新添加。

所用 NuGet 包源已提供对应包时，可直接安装：

```powershell
# 构建资源的机器
dotnet tool install --global quire-cli
quire-cli --help

# 在需要读取资源的 C# 项目目录执行
dotnet add package Quire
```

本地源码和 `.nupkg` 的使用方式见[从源码构建](#从源码构建)。CLI 与类库独立安装，客户端运行时不需要 CLI。

## 快速开始

准备如下资源目录，后续 CLI 命令在 `my-assets` 中执行：

```text
my-assets/
├── quire.project.yml
└── assets/
    ├── hello.txt
    └── hello.txt.meta
```

`hello.txt` 存放 `Hello, Quire!`；可选的 `hello.txt.meta` 内容为：

```properties
revision=1
language=en
```

创建 `quire.project.yml`：

```yaml
version: 1
project: my-assets
output: ./dist/{tag}
compression: lz4

packages:
  - name: Assets
    sources:
      - directory: ./assets
        prefix: assets
```

构建并检查第一份资源包：

```powershell
quire-cli package -tag v1
quire-cli inspect ./dist/v1
quire-cli verify ./dist/v1
```

产物为 `dist/v1/Assets.asset`，其中的文件通过 `assets/hello.txt` 访问。首次构建没有基线，因此打包全部资源；后续构建只处理相对已提交基线的变化。

尚未创建项目时，也可执行 `quire-cli init my-assets ./my-assets`。项目名必填，目标目录省略时使用当前目录。命令生成 `my-assets/quire.my-assets.yml` 及示例源目录；后续通过 `-f ./my-assets/quire.my-assets.yml` 选择配置，已有配置不会被覆盖。

## C# 接入

### 加载与读取

以下示例以资源项目目录为进程工作目录；接入应用时，将包目录替换为部署后的实际路径。

```csharp
using System.Text;
using Quire;

await using var bundle = await QuireBundle.OpenAsync("./dist/v1");

using var asset = await bundle.ReadAsync("assets/hello.txt");
Console.WriteLine(Encoding.UTF8.GetString(asset.Data.Span));

if (asset.Metadata.TryGetInt64("revision", out long revision))
    Console.WriteLine($"Revision: {revision}");
```

`Open` / `OpenAsync` 默认递归发现目录中的 `.asset` 文件，并合并覆盖关系。传入的是磁盘上的包目录，读取时使用的是包内逻辑路径。

`AssetData` 使用 `using` 释放；释放后不要继续持有或访问其 `Data` 及切片。完整读取默认上限为 **32 MiB**，更大的资源使用流式或范围读取。

### 目录浏览与查询

```csharp
AssetEntryInfo[] entries = await bundle.DirAsync("assets");

foreach (var entry in entries)
{
    if (entry.IsDirectory)
        Console.WriteLine($"Directory: {entry.Path}");
    else
        Console.WriteLine($"File: {entry.Path} ({entry.Length} bytes)");
}

if (bundle.TryGetFileInfo("assets/hello.txt", out var info))
    Console.WriteLine(info.Length);
```

| 场景 | 接口 |
| --- | --- |
| 获取当前目录的全部直接子项 | `Dir` / `DirAsync`，返回 `AssetEntryInfo[]` |
| 流式枚举直接子目录与文件 | `EnumerateEntries` / `EnumerateEntriesAsync` |
| 按类型枚举，可递归 | `EnumerateFiles` / `EnumerateDirectories` |
| 判断是否存在 | `FileExists` / `DirectoryExists` |
| 只读取元数据 | `ReadMetadata` / `ReadMetadataAsync` |

目录参数默认为空字符串，表示包内根目录。`AssetEntryInfo.Kind` 区分文件与目录；目录从资源路径推导，不保存空目录。

### 大文件与范围读取

```csharp
using AssetReader reader = bundle.OpenRead("assets/video.bin");
byte[] buffer = new byte[64 * 1024];

int count = await reader.ReadAtAsync(1024, buffer);
// 有效数据是 buffer[0..count]；ReadAtAsync 不改变 reader.Position。

await using var destination = File.Create("./video.bin");
await bundle.CopyToAsync("assets/video.bin", destination);
```

`AssetReader` 继承 `Stream`，支持顺序读取与定位。多个任务可以共享 `QuireBundle`，每个并发任务应使用独立读取器。频繁读取同一文件的不同范围时，复用读取器可复用其块缓冲。

### 校验与包信息

完整读取可按文件选择是否额外计算 SHA-256：

```csharp
using var config = await bundle.ReadAsync(
    "assets/config.bin", verifyContentHash: true);

using var texture = await bundle.ReadAsync(
    "assets/texture.bin", verifyContentHash: false);
```

关闭内容哈希校验不会关闭签名绑定的认证、AES-GCM 认证或格式检查。流式和范围读取不执行整文件 SHA-256；需要完整核验时使用 `Verify` / `VerifyAsync`。

```csharp
await using var checkedBundle = await QuireBundle.OpenAsync("./dist", new ReadOptions
{
    VerifyPackageChain = true
});

if (checkedBundle.LatestTag is { } tag)
    Console.WriteLine($"{tag.Name} · {tag.BuildTimeUtc:O}");
```

`VerifyPackageChain` 默认关闭。开启后，检查**已发现的每个 Tag 自己的分包清单**，按 `PackageUid` 判断缺包，文件名仅用于错误提示。它不要求父 Tag 存在，也无法发现一个完全没有放入目录的 Tag。CLI 的 `verify` 执行内容校验，不启用这个清单选项。

`LatestTag` 来自已加载的包，空集合时为 `null`，与 CLI 的 `commit` 状态无关。完整读取限制、索引预算、缓存和读取后端可通过 [ReadOptions](Quire/ReadOptions.cs) 调整。

## 增量发布

发布流程是 **构建候选 → 校验与应用测试 → 提交基线**。`package` 始终比较当前文件与上一次 `commit` 的基线，打包成功不会自动推进基线。

第一份 `v1` 通过应用测试后：

```powershell
quire-cli commit v1
```

修改源文件后，生成下一份候选：

```powershell
quire-cli diff
quire-cli package -tag v2
quire-cli verify ./dist/v2

# 将 v1 基础包与 v2 补丁一起完成应用测试后
quire-cli commit v2

quire-cli tags
quire-cli changes v2
```

- `diff` 查看当前资源相对基线的新增、更新和删除。
- `changes v2` 查看构建 `v2` 时记录的变化，不扫描当前源文件。
- `remove v2` 仅用于放弃未提交候选：删除候选记录，保留输出文件。
- 删除源文件会生成删除记录；加载补丁后，该资源从可见索引中移除，旧包不会被修改。

应用部署所需的基础包与补丁，并把它们所在的目录传给 `QuireBundle.Open`。包按构建时间叠加，新记录覆盖旧记录。加载器读取目录中的所有匹配包，不根据 CLI 基线筛选候选；新增补丁文件后，需要重新打开集合。

构建状态保存在配置文件旁的 `.quire/<project>/`，例如上面的项目使用 `.quire/my-assets/`。继续增量构建时应保留这些状态。客户端不需要 `.quire/`，Quire 也不负责源文件版本管理、资源下载或部署。

### 全量配置与补丁配置

同一目录的 `quire.project.yml` 和 `quire.patch.yml` 设置相同的 `project`，例如 `project: my-assets`，即可共用基线。通过 `-f` 切换：

```powershell
quire-cli package -f ./quire.patch.yml -tag v3
```

只要最终逻辑路径、内容和有效 Meta 一致，改变分包方式、输出位置、压缩策略或密钥不会重打未变资源。新写入的资源使用本次配置。

**补丁配置仍需扫描完整的当前资源集，并保持逻辑路径一致。** 不要只配置已修改的文件；基线中存在、当前扫描却不存在的路径会被视为删除。

## 项目配置

以下配置展示常用选项。相对路径默认以 YAML 所在目录为基准；`package` / `diff` 可用 `-root` 改变相对资源目录的解析基准。

```yaml
version: 1
project: my-assets
output: ./dist/{tag}
suffix: asset
compression: lz4
compression-policy: adaptive
secret-key: none

build:
  jobs: 0
  memory-mib: 128
  block-size: 262144

packages:
  - name: Assets
    sources:
      - directory: ./assets
        prefix: assets
        include:
          - "**/*"
        exclude:
          - "**/*.tmp"
```

| 配置 | 说明 |
| --- | --- |
| `project` | 必填项目标识；状态保存在 `.quire/<project>/`，缺失、空字符串或纯空白均报错 |
| `output` | 最终输出目录，默认 `./dist`，不会自动追加 Tag 目录 |
| `suffix` | 文件后缀，默认 `asset` |
| `compression` | `none`、`lz4`、`zlib`，默认 `lz4`，可在单个包中覆盖 |
| `compression-policy` | `adaptive` 只保留更小的压缩结果；`always` 始终压缩，可按包覆盖 |
| `secret-key` | 本机全局密钥名；`none` 或省略表示不加密、不签名 |
| `build.jobs` | 压缩/加密工作线程数；`0` 根据 CPU 和缓冲预算自动选择 |
| `build.memory-mib` | 构建管线缓冲预算，默认 128 MiB，不是整个进程的内存上限 |
| `build.block-size` | 块大小，支持 64 KiB～1 MiB 的二次幂，默认 256 KiB |

`output` 和包的 `name` 支持 `{tag}`、`{tagid}`、`{yyyy}`、`{MM}`、`{dd}`、`{HH}`、`{mm}`、`{ss}`、`{fff}`、`{date}`、`{time}`、`{timestamp}`。例如 `name: "Patch-{tag}"` 或 `output: ./dist/{date}/{tag}`；日期时间使用同一次构建的本地时间。已有同名产物不会被覆盖，未变更的包不会生成空文件。

`include` / `exclude` 相对各自的源目录匹配：`*.png` 和 `./*.png` 只匹配第一层，`**/*.png` 包含子目录。`prefix` 只决定包内路径。还可以在项目或源目录中使用 `.assetignore`：

```gitignore
**/*.psd
**/*.tmp
draft/
```

### 同一仓库管理多个项目

多个 YAML 可以放在仓库根目录，用 `project` 区分基线和 Tag 历史。例如 `quire.game-a.yml` 设置 `project: game-a`，`quire.game-b.yml` 设置 `project: game-b`：

```text
repository/
├── quire.game-a.yml
├── quire.game-b.yml
└── .quire/
    ├── game-a/
    └── game-b/
```

也可以直接初始化并选择项目：

```powershell
quire-cli init game-a
quire-cli init game-b
quire-cli package -f ./quire.game-a.yml -tag v1
quire-cli package -f ./quire.game-b.yml -tag v1
quire-cli tags -f ./quire.game-a.yml
```

`init` 为每个项目配置 `./assets/<project>/` 下的示例源目录，以及 `output: ./dist/<project>`。手动配置的 `output` 按原值使用，不会自动追加项目名；不同项目应配置各自的输出位置。资源目录可以指向仓库外。

项目名应使用稳定的名称，建议小写字母、数字和连字符；也支持合法的 Unicode 名称。名称不能包含路径分隔符、`{`、`}` 或文件名非法字符，不能以 `.` 或 `-` 开头，也不能使用设备名。

同目录下，相同 `project` 的配置共享状态，配置文件改名不影响基线。修改 `project` 会选择另一套状态。**不兼容旧版缺少 `project` 的配置及未分项目的 `.quire/` 状态，不会自动读取或迁移旧基线。** 用 Git 跨机器同步发布状态时，应跟踪对应的 `.quire` 内容，并移除 `**/.quire/` 等忽略规则。

多源、多包和完整字段说明见[配置模板](quire-cli/Templates/quire.project.yml)。

### 切换资源所在目录

`package` 和 `diff` 支持 `-root DIRECTORY`，指定 YAML 中相对 `sources.directory` 的解析基准。例如上面的 `directory: ./assets` 配置可在两台机器上保持不变：

```powershell
# 机器 A：读取 D:/game_root/assets
quire-cli package -f ./quire.project.yml -root D:/game_root -tag v1
# 完成验证测试后提交，再同步仓库状态到机器 B
quire-cli commit v1 -f ./quire.project.yml

# 机器 B：读取 E:/new_root/assets，比较同一项目的基线
quire-cli diff -f ./quire.project.yml -root E:/new_root
quire-cli package -f ./quire.project.yml -root E:/new_root -tag v2
```

`-root` 支持绝对路径；填写相对路径时，相对于命令执行目录解析。省略时，相对资源目录仍以 YAML 所在目录为基准。**YAML 中已经是绝对路径的 `sources.directory` 始终按原路径读取，不受 `-root` 影响。**

`-root` 只改变资源位置：`-f` 的解析方式、输出目录和 `.quire/<project>/` 的位置不变。两台机器同步同一项目状态，并保持资源逻辑路径、内容和有效 Meta 一致时，切换物理根目录不会重置索引或产生内容变更。历史查询、提交和包读取等其他命令不接受 `-root`。

### 文件元数据

资源 `player.png` 的元数据文件为 `player.png.meta`，采用 `property=value`：

```properties
name=player
revision=1
scale=1.0
enabled=true
tags=["hero","blue"]
frames=[1,2,3]
weights=[0.25,0.75]
switches=[true,false]
```

对应 string、int64、double、bool 及四种数组。可用 `code:string=001` 显式指定类型；读取时使用 `Metadata.GetString`、`TryGetInt64`、`GetStringArray` 等类型化接口。

仅修改有效 Meta 时，可以生成仅含元数据的补丁。空 `.meta` 表示存在空元数据；仅删除 `.meta` 不会清空之前已发布的元数据。

## 密钥与加密

密钥管理独立于项目，同一用户的所有项目可以引用同一名称：

```powershell
quire-cli keys add game "replace-with-a-long-random-password"
quire-cli keys list
quire-cli keys show game
quire-cli keys export game ./client.key
```

项目通过 YAML 选择密钥：

```yaml
project: my-assets
secret-key: game
```

指定名称后，打包使用 AES-256-GCM 加密并以 ECDSA P-256 签名；名称不存在时打包报错。`none` 或省略时不加密、不签名。密码不写入 YAML，打包时也无需再次提供。

客户端接入：

```csharp
var key = new AssetKey(File.ReadAllBytes("./client.key"));
// 也可使用 new AssetKey(clientKeyBase64)。

await using var encryptedBundle = await QuireBundle.OpenAsync("./dist", new ReadOptions
{
    Key = key,
    VerifyPackageChain = true
});
```

`keys show KEYNAME` 只输出一行 128 字符的 Base64 客户端密钥，可直接用于 `new AssetKey(string)`；不写文件，也不需要项目配置。`keys list` 显示 `FINGERPRINT KEYNAME CLIENTKEY`，其中 `CLIENTKEY` 是相同的字符串。

`keys export` 输出 96 字节二进制客户端密钥，包含 AES 主密钥和验签公钥，不含密码或签名私钥。设置可信 `Key` 或 `KeyProvider` 后，加载器会拒绝未签名包。`keys remove KEYNAME` 删除本机条目，不影响已导出的客户端密钥。

相同密码可重新派生相同密钥，名称不参与派生。请使用高熵密码，并从可信渠道向客户端提供密钥。Windows 的发布凭据保存在当前用户的 `%LOCALAPPDATA%\Quire\keys\named-v2`，使用 DPAPI 保护；Unix 使用仅所有者可访问的文件权限。原始密码不持久化。

CLI 读取加密包时显式指定客户端密钥文件：

```powershell
quire-cli verify ./dist -key ./client.key
quire-cli unpackage ./dist ./unpacked -key ./client.key
```

## CLI 命令

| 命令 | 用途 |
| --- | --- |
| `init PROJECT [DIRECTORY]` | 按项目名创建 `quire.<project>.yml` 及示例源目录 |
| `package [-tag NAME]` | 基于正式基线构建候选；省略名称时自动生成 |
| `diff` | 查看当前源文件与基线的差异 |
| `tags` | 列出历史 Tag、状态与父子关系 |
| `changes NAME` | 查看某次构建记录的文件变化 |
| `commit NAME` | 将候选提交为正式基线 |
| `remove NAME` | 删除未提交候选记录，保留产物 |
| `inspect DIRECTORY` | 查看资源包及合并后的资源列表 |
| `verify DIRECTORY` | 完整校验资源包内容 |
| `unpackage SOURCE DEST` | 解包合并后的资源与 Meta，目标目录须为空或不存在 |
| `keys add/list/show/remove/export` | 管理当前用户的全局命名密钥；`show KEYNAME` 输出客户端密钥字符串 |

构建及历史命令可通过 `-f FILE` 指定配置位置，默认 `./quire.project.yml`；`keys` 不接受 `-f`。仅 `package` / `diff` 支持 `-root DIRECTORY`，用于切换相对资源目录的基准。读取命令使用 `-key FILE` 和 `-suffix EXT`，不读取项目配置。`package` / `unpackage` 可用 `-jobs N` 指定工作线程数。

完整用法：

```powershell
quire-cli --help
```

## GUI 与自动化

给操作或查询命令增加无参数开关 `-progress`，即可逐行接收结构化事件：

```powershell
quire-cli package -f ./quire.project.yml -tag v2 -progress
quire-cli tags -progress
quire-cli changes v2 -progress
quire-cli keys list -progress
```

stdout 使用 UTF-8 NDJSON，每行是一个 JSON 对象。事件包括 `started`、`progress`、`item`，以及唯一的正常终态 `completed` / `failed` / `canceled`。进度约每 200 ms 采样，包含当前阶段、文件数、字节数、速率和 ETA；大查询结果逐项输出。

GUI 启动 CLI 子进程，**同时异步读取 stdout 和 stderr**，用 `progress` 更新界面、用 `item` 填充列表。收到 `completed` 且进程退出码为 `0` 才表示成功；失败为 `1`，取消为 `130`。错误提供可读原因，不输出调用栈。

不加 `-progress` 时，CLI 显示面向人的终端进度。百分比和 ETA 属于当前阶段，未知总量时不会生成虚假的全局百分比。

完整事件字段和 C# 子进程接入示例见 [CLI 事件协议](docs/cli-protocol.md)。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet build Quire.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet run --project tests -c Release --no-build
dotnet pack Quire.slnx -c Release --no-build -p:GeneratePackageOnBuild=false -o artifacts/packages
```

生成 `Quire.1.0.0.nupkg` 与 `quire-cli.1.0.0.nupkg`。本地安装 CLI，例如：

```powershell
dotnet tool install quire-cli --tool-path ./artifacts/tools --add-source ./artifacts/packages --version 1.0.0
```

应用可把 `artifacts/packages` 配置为 NuGet 包源，或直接引用 `Quire/Quire.csproj`。版本号以实际生成的包为准。

CLI 也可发布为 Native AOT 可执行文件；例如 Windows x64，需要相应的本机编译工具链：

```powershell
dotnet publish quire-cli/quire-cli.csproj -c Release -r win-x64 -p:PublishAot=true -p:GeneratePackageOnBuild=false -o artifacts/cli
```

## 文档与示例

| 文档 | 内容 |
| --- | --- |
| [示例项目](examples/README.md) | 常用读取方式、Meta、大文件示例 |
| [配置模板](quire-cli/Templates/quire.project.yml) | 多目录、多包与配置字段说明 |
| [CLI 事件协议](docs/cli-protocol.md) | GUI、脚本和其他宿主的进度与管理接口 |
| [资源包格式](FORMAT.md) | 二进制布局、压缩、认证和删除记录 |
| [测试说明](tests/README.md) | 验证范围与大文件测试方法 |
| [性能测试](benchmarks/README.md) | 基准运行方式与测量结果 |

当前资源包格式为 **v6**，不兼容旧格式；YAML 的 `version: 1` 是独立的配置版本。Quire 运行时只读，删除补丁隐藏索引记录，不提供原包修改或空间回收接口。

内嵌 LZ4 源码保留其 [LZ4](Lz4/LICENSE.LZ4.txt) 与 [K4os](Lz4/LICENSE.K4os.txt) 许可证，许可证随类库和 NuGet 包发布。

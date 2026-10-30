# Quire.Package

Quire.Package 是面向应用程序的资源打包与读取工具。它将多个源目录中的文件和可选 Meta 整理成 `.asset` 资源包，支持压缩、加密、超大文件以及发布后的增量修补。

项目提供两个独立的安装产物：

| 名称 | 类型 | 用途 |
| --- | --- | --- |
| `quire-cli` | .NET CLI 工具，也可发布为 Native AOT 可执行文件 | 初始化项目、扫描文件、构建全量或增量包、查看、校验和解包 |
| `Quire.Package` | NuGet 类库，命名空间为 `Quire.Package` | 在应用程序中加载资源包目录、读取内容和 Meta、按需读取及并发读取 |

应用程序只需引用 `Quire.Package`；资源构建在开发机或构建服务器上通过 `quire-cli` 完成。两个发布项目均无第三方 NuGet 运行时依赖；LZ4 核心源码内嵌，许可证随程序集和 NuGet 发布。

主要能力：

- 使用 `quire.project.yml` 将多个目录编排为多个资源包，支持多级路径前缀、包含/排除模式和 `.assetignore`。
- 每次构建记录一个 Tag，可基于指定 Tag 生成内容变更或仅 Meta 变更的补丁。
- 采用 64 位长度与偏移，支持超过 4 GiB 的文件；流式读取无需把整个文件放入内存。
- 支持 LZ4 压缩和可选 AES-256-GCM 内容加密。
- Meta 支持 string、int64、double、bool 及对应数组，读取时使用明确类型的 API。
- 按目录加载基础包和补丁，支持并发读取、并发解包，以及可选内存映射读取。

资源包后缀为 `.asset`，头部 magic 为 8 字节 ASCII `PK.QUIRE`，当前文件格式版本为 2，TagID 为 8 位小写十六进制字符串。旧格式包和旧 GUID 构建记录均视为解析失败，不提供兼容或自动转换。资源包不提供写入、删除、移动、保存或空间回收 API。

阅读入口：[安装 CLI](#安装-quire-cli) · [安装类库](#安装-quirepackage) · [快速上手](#快速上手) · [CLI 命令](#cli-命令和参数) · [C# API](#quirepackage-使用指南) · [源码构建与发布](#从源码构建和发布)

## 安装 quire-cli

使用 .NET 工具安装方式时，需要 .NET 10 SDK；SDK 已包含运行所需的 .NET 运行时。可以用 `dotnet --list-sdks` 查看 SDK。安装 Native AOT 可执行文件后，运行 CLI 不需要 .NET SDK 或运行时。

以下 NuGet 安装命令适用于本项目对应版本已经发布到所用包源的情况；尚未发布时，可使用下文的本地 `.nupkg` 安装方式。源码中的当前版本为 `1.0.0`，指定版本时请以实际发布版本为准。

### 全局安装

```powershell
dotnet tool install --global quire-cli
quire-cli -h
```

安装后可在任意目录调用 `quire-cli`。全局工具目录需要在 `PATH` 中：Windows 通常为 `%USERPROFILE%\.dotnet\tools`，Linux/macOS 通常为 `$HOME/.dotnet/tools`。首次安装后可重新打开终端。

升级或卸载：

```powershell
dotnet tool update --global quire-cli
dotnet tool uninstall --global quire-cli
```

需要固定版本时，在安装或更新命令后加 `--version 1.0.0`。

### 项目本地安装

在使用资源工具的项目根目录执行；已有工具清单时省略第一条命令：

```powershell
dotnet new tool-manifest
dotnet tool install quire-cli
dotnet tool run quire-cli -- -h
```

将 `.config/dotnet-tools.json` 纳入版本管理，团队成员或构建服务器取得项目后执行：

```powershell
dotnet tool restore
dotnet tool run quire-cli -- package -project ./quire.project.yml -jobs 5
```

本地安装的工具通过 `dotnet tool run quire-cli -- ...` 调用；下文简写为 `quire-cli ...` 的命令可以替换成这种形式。

### 从本地 NuGet 包安装

已有本项目的 `.nupkg` 时，将其放入本地包目录。以下示例假设工具包位于 `./artifacts/packages`；从源码生成包的方法见[从源码构建和发布](#从源码构建和发布)。

```powershell
dotnet tool install quire-cli --tool-path ./.tools --add-source ./artifacts/packages --version 1.0.0

# Windows PowerShell
./.tools/quire-cli.exe -h
```

Linux/macOS 使用 `./.tools/quire-cli -h`。`--tool-path` 指定的目录不会自动加入 `PATH`，因此使用该目录中的完整命令路径。

如果只希望从本地包目录安装，可创建 `nuget.local.config`，并将上面安装命令的 `--add-source` 替换为 `--configfile ./nuget.local.config`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="QuireLocal" value="./artifacts/packages" />
  </packageSources>
</configuration>
```

配置中的相对包目录以这个 NuGet 配置文件所在目录为基准。

### 使用 Native AOT 可执行文件

取得与操作系统和架构匹配的 Native AOT 发布产物后，可以直接运行。例如 Windows x64：

```powershell
./quire-cli.exe -h
./quire-cli.exe init ./MyAssets
```

Linux x64：

```bash
chmod +x ./quire-cli
./quire-cli -h
./quire-cli init ./MyAssets
```

可以将可执行文件放入 `PATH` 中的目录，之后直接使用 `quire-cli`。自行编译 Native AOT 所需的 SDK、C++ 工具链和发布命令见[Native AOT 发布](#native-aot-发布)。

## 安装 Quire.Package

消费项目需要面向 .NET 10 或兼容的更高版本。类库不依赖 CLI，程序部署时只需携带类库及要加载的 `.asset` 文件。

### 从 NuGet 安装

在使用该类库的 C# 项目目录中执行：

```powershell
dotnet add package Quire.Package
```

也可以指定项目和版本：

```powershell
dotnet add ./MyGame/MyGame.csproj package Quire.Package --version 1.0.0
```

对应的项目引用为：

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Quire.Package" Version="1.0.0" />
</ItemGroup>
```

在 Visual Studio 中，也可通过“管理 NuGet 程序包”选择发布了本项目的包源，搜索并安装 `Quire.Package`。C# 代码使用 `using Quire.Package;`。

### 使用本地包或源码引用

包尚未上传到 NuGet 源时，可先在本仓库目录生成类库包，再安装到消费项目：

```powershell
dotnet pack Quire.Package/Quire.Package.csproj -c Release -p:GeneratePackageOnBuild=false -o artifacts/packages
dotnet add ./MyGame/MyGame.csproj package Quire.Package --source ./artifacts/packages --version 1.0.0
```

将 `./MyGame/MyGame.csproj` 替换为实际消费项目路径。后续构建或换一台机器还原时，也需要能访问这个包源；可以在消费项目的 `NuGet.Config` 中配置本地目录或私有 NuGet 源。

若消费项目与源码一同开发，可以直接添加项目引用，省去打 NuGet 包的步骤。在本仓库目录执行：

```powershell
dotnet add ./MyGame/MyGame.csproj reference ./Quire.Package/Quire.Package.csproj
```

同一消费项目选择 NuGet 引用或项目引用中的一种即可。仓库中的 [examples 项目](examples/Quire.Package.Examples.csproj) 使用项目引用。

## 快速上手

下面的命令始终在 `MyAssets` 的上一级目录执行，假设 `quire-cli` 已安装并能直接调用。

### 1. 初始化资源项目

```powershell
quire-cli init ./MyAssets
```

初始化会生成带完整注释的 `MyAssets/quire.project.yml` 和三个空源目录：

```text
MyAssets/
  quire.project.yml
  assets/
    common/
    shared/
    locale/
```

默认配置把 `common` 和 `shared` 合并到 `common.asset`，把 `locale` 放入 `locale.asset`。没有资源变更的包不会生成空 `.asset` 文件。

### 2. 添加资源和可选 Meta

创建 `MyAssets/assets/common/settings.txt`：

```text
resolution=1920x1080
quality=high
```

在旁边创建 `settings.txt.meta`：

```text
name=settings
revision=1
scale=1.0
enabled=true
tags=["configuration","example"]
levels=[1,2,3]
weights=[0.25,0.75]
switches=[true,false]
```

Meta 可省略。此例的源文件经过 `prefix: common/data` 映射，包内路径是 `common/data/settings.txt`，读取时使用这个逻辑路径。

### 3. 打包、检查和解包

```powershell
quire-cli package -project ./MyAssets/quire.project.yml -jobs 5
quire-cli inspect ./MyAssets/dist
quire-cli verify ./MyAssets/dist
quire-cli unpackage ./MyAssets/dist ./Unpacked -jobs 5
```

`package` 输出本次 TagID 和资源包路径。文件位于 `MyAssets/dist/<TagID>/common.asset`，构建记录位于 `MyAssets/.quire/`。解包后可以在 `Unpacked/common/data/settings.txt` 及其 `.meta` 中看到还原结果；解包目标需要不存在或为空。

### 4. 在 C# 程序中读取

创建一个消费项目并安装类库；本地包使用前一节的 `--source` 方式：

```powershell
dotnet new console -n QuireReader -f net10.0
dotnet add ./QuireReader/QuireReader.csproj package Quire.Package
```

将 `QuireReader/Program.cs` 替换为：

```csharp
using System.Text;
using Quire.Package;

string directory = args.Length > 0 ? args[0] : "./MyAssets/dist";
using var assets = await PackageSet.OpenAsync(directory);

const string path = "common/data/settings.txt";
AssetInfo info = assets.GetFileInfo(path);
Console.WriteLine($"{info.Path}: {info.Length} bytes");

Metadata meta = await assets.ReadMetadataAsync(path);
if (meta.TryGetString("name", out var name))
    Console.WriteLine($"Name: {name}");
if (meta.TryGetInt64("revision", out var revision))
    Console.WriteLine($"Revision: {revision}");

using AssetData file = await assets.ReadAsync(path);
Console.WriteLine(Encoding.UTF8.GetString(file.Data.Span));
```

在同一工作目录运行：

```powershell
dotnet run --project ./QuireReader/QuireReader.csproj -- ./MyAssets/dist
```

## CLI 命令和参数

`quire-cli` 自身的参数采用单横线；`dotnet` 的参数仍使用它自己的语法。查看帮助使用 `quire-cli -h`。

| 命令 | 用途 | 示例 |
| --- | --- | --- |
| `init` | 初始化完整的多包配置和源目录 | `quire-cli init ./MyAssets` |
| `package` | 构建资源包，不带基准 Tag 时为全量构建 | `quire-cli package -project ./MyAssets/quire.project.yml -jobs 5` |
| `diff` | 与指定 Tag 比较，列出本次需打包的内容或 Meta | `quire-cli diff -project ./MyAssets/quire.project.yml -base TAG_ID` |
| `tags` | 查看该项目的本地构建记录 | `quire-cli tags -project ./MyAssets/quire.project.yml` |
| `inspect` | 查看目录中的物理包和最终有效资源路径 | `quire-cli inspect ./MyAssets/dist` |
| `verify` | 校验目录中的所有包，包括被后续包覆盖的内容 | `quire-cli verify ./MyAssets/dist` |
| `unpackage` | 并发解包最终有效资源及 Meta | `quire-cli unpackage ./MyAssets/dist ./Unpacked -jobs 5` |

示例中的 `TAG_ID` 需要替换为 `package` 或 `tags` 输出的 8 位 TagID，也可以使用能够唯一匹配该项目构建记录的十六进制前缀（不区分大小写）。

| 参数 | 适用命令 | 默认值或要求 |
| --- | --- | --- |
| `-project PATH` | `package`、`diff`、`tags` | 可选，默认当前工作目录中的 `quire.project.yml` |
| `-base TAG` | `package`、`diff` | `package` 可选；`diff` 必填；接受完整 8 位 TagID 或唯一前缀，共 1–8 位十六进制字符 |
| `-jobs N` | `package`、`diff`、`unpackage` | 可选，默认逻辑处理器数与 8 的较小值，范围 1–256；打包时还受 YAML 内存预算约束 |
| `-pwd VALUE` | `package`、`diff`、`inspect`、`verify`、`unpackage` | 可选，直接提供非空密码；加密包需要有效密码来源 |
| `-pwd-env NAME` | 同上 | 可选，从指定环境变量读取密码，与 `-pwd` 互斥 |

`init` 的目录参数可省略，默认当前目录；`inspect` 和 `verify` 必须指定一个资源包目录；`unpackage` 必须依次指定源包目录和目标目录。读取类命令不依赖 `quire.project.yml`。

`package -jobs` 控制压缩/加密工作线程；`diff` 接受相同的构建选项，但不会执行压缩或写包；`unpackage -jobs` 控制同时解包的文件数。路径含空格时使用引号，例如 `-project "./Game Assets/quire.project.yml"`。

`package`、`unpackage` 和 `verify` 默认显示控制台进度面板：当前阶段、进度条与百分比、已完成/总文件数、读写速率（B/s、MiB/s 等）、累计读写大小、已用时间和预计剩余时间（ETA）。交互终端约每秒刷新 10 次，单个大文件也会按块更新。百分比与 ETA 针对当前阶段；扫描、打开包或发布等尚无总量的阶段显示动态指示符，速率样本不足时 ETA 显示 `--:--`。完成时的速率为整个命令的平均值。

读写大小统计应用实际传输的数据，包括打包的源文件哈希读取、临时表读写和产物复核；压缩后写入量与原始内容进度分别计算。它不是操作系统的物理磁盘吞吐量，也不计文件预分配、对齐空洞或构建记录等辅助操作。解包文件数包含导出的 `.meta`，校验文件数包含被覆盖的历史记录。

进度写入 stderr，命令结果仍写入 stdout，方便脚本读取。stderr 重定向、`TERM=dumb` 或 `CI=true` / `CI=1` 时自动使用无控制字符的文本日志，按阶段切换和每 5 秒输出；设置 `NO_COLOR` 可禁用颜色。取消或失败会结束进度显示并保留原有错误退出码。

`memory-mib`、`block-size` 由 YAML 配置；源文件读取和数据写入并发使用内部默认值，没有对应的公开 CLI 参数。

命令成功时退出码为 `0`；已处理的参数、文件或数据错误为 `1`；按 Ctrl+C 取消为 `130`。

## 项目配置

```powershell
quire-cli init ./MyAssets
quire-cli package -project ./MyAssets/quire.project.yml -jobs 5
```

`init` 不指定目录时使用当前目录。它创建 `quire.project.yml` 和配置引用的 `assets/common`、`assets/shared`、`assets/locale` 空目录；模板包含全部配置字段、一个合并两个源目录的包、另一个独立包、过滤规则及使用说明。已有配置不会被覆盖，已有资源文件会保留。添加资源后执行 `package` 即可构建。

所有相对路径以 `quire.project.yml` 所在目录为基准。源目录可以在项目目录之外。`prefix` 支持 `common/data` 等多级相对路径，也可以是空字符串。

```yaml
version: 1
output: ./dist
compression: true

build:
  memory-mib: 128
  block-size: 262144

encryption:
  enabled: false
  key-environment: QUIRE_KEY

packages:
  - name: common
    sources:
      - directory: ./assets/common
        prefix: common/data
      - directory: ../shared/config
        prefix: config
  - name: textures
    compression: true
    sources:
      - directory: ./assets/images
        prefix: game/textures
        include:
          - "**/*.png"
        exclude:
          - "**/draft/**"
```

压缩仅有 true/false，开启后使用独立 LZ4 块，压缩无收益的块保存原始数据。不同分包可以覆盖全局同一路径；同一分包内重复映射报错。

YAML 支持两空格缩进的块映射、块序列、普通/单引号/双引号标量和注释。不支持锚点、别名、自定义标签、多行标量或流式集合，未知设置和不支持的语法会报错。没有 `state` 或 `metadata.format` 设置。

可直接运行 [API 基础示例](examples/README.md)，演示打开目录、读取文件、读取八种 Meta 类型、延迟加载与流式按需读取：

```powershell
$env:QUIRE_KEY = "your-password"
dotnet run --project quire-cli -c Release -- package -project examples/quire.project.yml
dotnet run --project examples/Quire.Package.Examples.csproj -c Release -- examples/dist $env:QUIRE_KEY
```

仓库内的示例配置已开启加密，以上打包和读取使用同一个密码；`init` 生成的新项目仍默认关闭加密。

## 固定 property=value Meta

`player.png` 对应 `player.png.meta`。没有 sidecar 表示没有 Meta，空 sidecar 表示存在空 Meta。

```text
name=player
offsetX=-16
scale=1.0
visible=true
tags=["hero","blue"]
frames=[1,2,3]
weights=[0.25,0.75]
enabled=[true,false]
code:string=001
emptyIds:int64[]=[]
```

只支持 string、int64、double、bool 及其各自数组。每行一个属性，使用第一个 `=` 分隔；空行和以 `#` 开始的整行注释被忽略。值内的 `=`、`#` 保留。字符串可以不加引号；双引号字符串和数组里的字符串使用标准 JSON 字符串转义，但不接受 JSON 对象作为另一种 Meta 文件格式。

未标注的整数是 int64，小数和指数形式是 double，true/false 是 bool，其余普通文本是 string。显式类型使用 `name:type=value`。未标注空数组是 string[]，其他空数组必须标注类型。数值数组提升为 double[] 时不允许整数精度丢失。禁止重复键、非有限浮点数、对象、嵌套数组、混合类型数组和 null 元素。字符串 `null` 本身可以作为普通文本。

源 sidecar 和编码后的 Meta 分别限制为 1 MiB，键名最多 1024 UTF-8 字节。编码按 UTF-8 键名排序，调整排版或行顺序不会产生补丁。运行时使用专用二进制字段表，不依赖序列化器或反射。

## .assetignore

项目根目录的 `.assetignore` 应用于每个源目录的相对路径；源目录及子目录中的规则追加生效。支持 `*`、`?`、`**`、`!`、根锚定 `/`、目录后缀 `/`、整行注释及开头的 `\#` / `\!` 转义。

```gitignore
**/*.tmp
**/*.psd
!textures/keep.psd
draft/
```

已排除的目录直接剪枝；重新包含子文件需要先包含其父目录。规则区分大小写。当前项目的 `.quire/`、输出目录、项目配置和规则文件不作为资源。符号链接和 junction 不参与扫描。忽略资源时其 Meta 一并忽略；单独忽略 sidecar 等价于本次源没有 Meta。未被忽略的孤立 sidecar 报错，扫描/权限错误使构建失败。

## Tag 和增量构建

```powershell
quire-cli package -project ./quire.project.yml
quire-cli package -project ./quire.project.yml -base TAG_ID
quire-cli package -project ./quire.project.yml -pwd "your-password"
quire-cli diff -project ./quire.project.yml -base TAG_ID
quire-cli tags -project ./quire.project.yml
quire-cli inspect ./dist
quire-cli verify ./dist -pwd "your-password"
```

每次成功构建生成长度固定为 8 的小写十六进制 TagID，例如 `72745fcb`。TagID 使用随机 32 位值；每次打包都会持有项目构建锁和输出目录锁，与全部已有 Tag 比较，并保留已存在的输出目录和暂存目录名称，发生碰撞就重新生成，避免覆盖已有构建。此唯一性检查以当前项目的本地构建记录和输出目录为范围；共享输出目录的项目也不能同时分配 Tag 和发布产物。

指定 `-base` 时，以该 Tag 的有效资源清单比较内容 SHA-256、规范化 Meta 摘要、分包位置和编码策略。删除源文件不生成删除记录；仅删除 sidecar 不移除旧 Meta。完整内容替换同时替换该资源的 Meta 状态。Meta-only 记录替换整份 Meta，不合并字段。编码策略或加密凭据变化会重新写入相关内容。

`-base` 支持完整 8 位 TagID 或至少一位的唯一十六进制前缀，不区分大小写。例如已有 Tag 为 `72745fcb`、`cb7d4e65`、`7fcb7655` 时，可使用 `package -base 72`、`package -base c`、`package -base 7f`；`diff` 同样支持。`-base 7` 匹配多条记录时会报错并提示补充字符；不存在的前缀也会报错。

`tags` 按构建时间升序输出预留列宽、左对齐的表格，`TAG` 和 `BASE` 均显示完整的 8 位 ID。`BUILD TIME` 为本地时区的构建时间，格式为 `yyyy-MM-dd HH:mm:ss`；`OUTPUT FILES` 为本次生成的 `.asset` 文件数，`CONTENT` 为写入完整内容的资源数，`META ONLY` 为仅更新 Meta 的资源数，`BASE` 为基准 TagID，全量构建显示 `NULL`。无构建记录时仍显示表头。

`.quire/tags/` 中的构建记录必须使用 8 位 TagID；旧 GUID 目录或记录会导致解析失败，不会被忽略或迁移。项目 YAML 的 `version: 1` 是配置格式版本，与资源包格式版本 2 无关。

增量是文件级的；大文件内容变化会重新写入整个文件。扫描默认重新计算内容摘要，不仅依赖时间戳。实际打包再次计算摘要，源内容变化时失败。发布构建应使用稳定的源目录；各文件的读取检查不等同于文件系统原子快照。

构建状态固定在配置文件旁的 `.quire/`：

```text
.quire/
  tags/<TagId>/tag.json       构建记录
  tags/<TagId>/files.idx      完整有效清单
  temp/                      临时索引
  key.salt                   加密凭据指纹的派生盐（加密项目）
  build.lock                 项目构建互斥
dist/<TagId>/<name>.asset
```

Tag 记录保存继承后有效的资源状态，不建立运行时依赖图。同一构建的分包共享 TagId 和 UTC 时间。本地时间比已有 Tag 小时递增到最新 Tag 之后；跨机器构建应保持系统时钟一致。

包先写入临时文件，完成校验、头部及持久化刷新后重命名。分包在暂存目录构建完毕后发布整个 Tag 输出目录，最后提交本地 Tag。失败不发布 Tag；若输出已完成但 Tag 提交失败，CLI 报告保留的完整产物位置。无变化构建仍创建 Tag，但不输出空补丁。

## 解包

```powershell
quire-cli unpackage ./dist ./unpacked
quire-cli unpackage ./encrypted-dist ./unpacked -pwd "your-password" -jobs 8
quire-cli unpackage ./encrypted-dist ./unpacked -pwd-env QUIRE_KEY -jobs 4
```

`package` 替代原来的 `build` 命令，原命令名不再接受。`unpackage` 的两个位置参数依次是资源包目录和目标目录，不需要项目配置。源目录递归加载 `.asset`，按包头时间和相对文件名顺序应用覆盖，导出最终有效的资源视图。解包完整增量版本时，需要将基础包和相关补丁一起放在源目录中；仅有 Meta-only 记录而无内容的路径不会被导出。

文件按包内路径还原，例如 `common/data/player.png` 输出为 `unpacked/common/data/player.png`。有 Meta 时生成对应的 `player.png.meta`，仍使用 `property=value` 格式，以类型标注保留八种类型、空数组及浮点精度；空 Meta 导出为空 sidecar，没有 Meta 则不生成 sidecar。注释、原始排版和源目录分组不保存在包中，因此不会还原。再次打包时，可以将解包目录设为源目录并使用空 `prefix`，避免重复添加路径前缀。

目标必须是不存在或为空的目录，且与源目录互不包含。解包通过流式读取写入目标旁的暂存目录，同时验证每个导出文件的完整 SHA-256 和加密块认证；全部成功后重命名发布。每个导出文件只读取一遍，内存不随单个资源文件大小增长，支持超过 4 GiB 的文件。取消或失败会等待并发任务退出并清理暂存内容，已有目标文件不会被覆盖。源、目标及其祖先目录中的符号链接/junction 不受支持；当前文件系统无法表示的名称或文件/目录冲突会报错。

`unpackage -jobs N` 控制同时解包的资源文件数，范围为 1–256，默认取逻辑处理器数与 8 的较小值。每个任务独立读取、解压、解密、校验并写出文件及 Meta，单文件仍按块顺序流式处理。`-jobs 1` 可用于顺序解包。解包校验最终有效资源；如需检查被覆盖的历史内容，使用 `verify`。

## Quire.Package 使用指南

以下代码以快速上手中生成的资源为例。先打开目录，后续片段沿用该片段的 `assets`、`path` 和 `info`；实际项目可按需选取。完整可运行程序见 [examples/Program.cs](examples/Program.cs)。

### 打开目录、枚举和查询

```csharp
using Quire.Package;

using var assets = await PackageSet.OpenAsync("./MyAssets/dist");
const string path = "common/data/settings.txt";

foreach (AssetPackageInfo package in assets.Packages)
    Console.WriteLine($"{package.TagId} | {package.BuildTimeUtc:O} | {package.FilePath}");

foreach (AssetInfo asset in assets.EnumerateFiles("common/data", recursive: true))
    Console.WriteLine($"{asset.Path}: {asset.Length} bytes, meta={asset.HasMetadata}");

// 只枚举直接子目录；省略 recursive 参数则枚举全部后代目录。
foreach (string directory in assets.EnumerateDirectories("common/data", recursive: false))
    Console.WriteLine(directory);

if (!assets.TryGetFileInfo(path, out AssetInfo info))
    throw new FileNotFoundException("Resource was not found.", path);
```

同步打开使用 `PackageSet.Open(directory, options)`。集合应通过 `using` 或 `await using` 释放。打开目录会读取包头和必要索引，不会一次加载全部资源内容。

`AssetPackageInfo.TagId` 的类型为 `string`，返回完整的 8 位小写十六进制 ID。

默认递归扫描 `.asset` 文件；跳过 `.quire` 状态目录、以 `.tmp` 结尾的暂存目录和符号链接。`ReadOptions.Recursive=false` 可限制为当前目录。不可读的包、缺少密码或打开阶段发现的格式损坏会使打开失败，并报告具体文件；完整数据检查使用 `VerifyAsync`。

`EnumerateFiles()` 枚举所有有效文件；`EnumerateFiles("common/data", false)` 仅枚举该虚拟目录的直接子文件。`FileExists(path)` 仅判断是否存在，`GetFileInfo(path)` 在不存在时抛出 `FileNotFoundException`，`TryGetFileInfo` 则返回 `false`。查询只访问必要索引，不读取资源内容。

`EnumerateDirectories()` 枚举根目录的全部后代目录；`EnumerateDirectories("common/data", false)` 仅枚举指定目录的直接子目录。结果是完整包内路径，不带末尾 `/`，不包含所查询的目录自身。`DirectoryExists(directory)` 判断目录是否包含有效文件，根目录 `""` 始终存在。目录由有效文件推导，空目录和孤立 Meta-only 记录不会产生目录；跨包合并后，同一路径可以同时存在文件与目录。

文件按 UTF-8 路径排序；目录按追加 `/` 后的 UTF-8 前缀排序，例如 `a-b` 位于 `a` 之前。不存在的目录枚举为空。打开时自动在预算内建立统一文件索引与目录树，枚举直接遍历对应范围或子项；超预算时自动回退到逐包二分定位和有界范围合并。只为返回的路径创建字符串，不缓存全量路径对象。直接 `foreach` 使用具体枚举类型，避免接口装箱；LINQ / `IEnumerable` 仍可用，但会产生相应开销。枚举器在开始枚举时持有包引用，提前退出请使用 `foreach` 或显式 `Dispose`。

旧的 `Enumerate`、`Contains`、`GetInfo`、`TryGetInfo` 已移除，分别改用 `EnumerateFiles`、`FileExists`、`GetFileInfo`、`TryGetFileInfo`。

包内路径区分大小写，使用 `/` 分隔，包含打包配置的 `prefix`，例如 `common/data/settings.txt`。路径不能以 `/` 开头或结尾，不能包含反斜杠、空段、`.` 或 `..`；查询根目录使用 `EnumerateFiles()`。

### 读取文件内容

小文件可一次读入内存，同时取得最终生效的 Meta：

```csharp
using (AssetData file = await assets.ReadAsync(path))
{
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(file.Data.Span));
    Console.WriteLine($"Length: {file.Info.Length}, properties: {file.Metadata.Count}");
}
```

同步方式是 `assets.Read(path)`。`AssetData.Data` 是 `ReadOnlyMemory<byte>`，底层使用池化缓冲，必须在 `AssetData` 释放前消费完毕；需要长期持有独立副本时，在释放前调用 `file.Data.ToArray()`。`Metadata` 是独立不可变对象，可在释放 `AssetData` 后继续使用。

`Read/ReadAsync` 验证完整内容的 SHA-256，默认最多缓冲单个 32 MiB 文件。超过上限时改用流式或范围读取，也可按应用内存预算调整 `MaxBufferedAssetBytes`。

### 只读取 Meta

读取 Meta 不需要加载文件内容。对于可选属性，使用 `TryGet...`；缺少字段或类型不匹配时返回 `false`，可由调用方指定默认值：

```csharp
Metadata meta = await assets.ReadMetadataAsync(path);
if (meta.TryGetString("name", out var name))
    Console.WriteLine(name);
if (meta.TryGetInt64("revision", out var revision))
    Console.WriteLine(revision);
if (meta.TryGetBoolean("enabled", out var enabled))
    Console.WriteLine(enabled);
if (meta.TryGetStringArray("tags", out var tags))
    Console.WriteLine(string.Join(", ", tags));
if (meta.TryGetInt64Array("levels", out var levels))
    foreach (long level in levels)
        Console.WriteLine(level);

double scale = meta.TryGetDouble("scale", out double value) ? value : 1.0;
Console.WriteLine($"Scale: {scale}");
if (meta.TryGetValueType("name", out var type))
    Console.WriteLine($"Name type: {type}");
```

同步方式为 `assets.ReadMetadata(path)`。没有 Meta 时返回 `Metadata.Empty`；通过 `AssetInfo.HasMetadata` 可区分没有 Meta 和存在空 Meta。

| Meta 类型 | 读取方法 | 返回类型 |
| --- | --- | --- |
| string | `GetString` / `TryGetString` | `string` |
| int64 | `GetInt64` / `TryGetInt64` | `long` |
| double | `GetDouble` / `TryGetDouble` | `double` |
| bool | `GetBoolean` / `TryGetBoolean` | `bool` |
| string[] | `GetStringArray` / `TryGetStringArray` | `IReadOnlyList<string>` |
| int64[] | `GetInt64Array` / `TryGetInt64Array` | `ReadOnlySpan<long>` |
| double[] | `GetDoubleArray` / `TryGetDoubleArray` | `ReadOnlySpan<double>` |
| bool[] | `GetBooleanArray` / `TryGetBooleanArray` | `ReadOnlySpan<bool>` |

`TryGet` 的返回值为是否成功，表中的类型是其 `out` 参数类型。缺少字段或类型不匹配时返回 `false`，并将 `out` 设为默认值：字符串/字符串数组为 null，整数和浮点数为 0，布尔值为 false，数值/布尔数组为空 Span。成功读取字符串或字符串数组时，编译器知道结果非 null，无需使用 `!`。合法的 0、false、空字符串和空数组仍返回 `true`，应以方法返回值判断是否成功。

`TryGetValueType(key, out type)` 用于安全查询字段类型，字段不存在时返回 `false` 并将 `type` 设为默认值。`Contains(key)` 可判断属性是否存在，`Count` 和 `Keys` 可用于枚举。需要强制校验必填字段时使用 `Get...` / `GetValueType`，缺失或类型不匹配时会抛异常；API 不进行隐式类型转换。

数值字段访问不装箱、不分配；字符串按需解码并缓存在该 Meta 对象内。数值和布尔数组返回只读 Span，若需要跨 `await` 保存数组数据，可使用 `ToArray()` 创建副本。

### 懒加载完整数据

使用 `Lazy<Task<AssetData>>` 可以在首次需要时才开始完整读取：

```csharp
var deferred = new Lazy<Task<AssetData>>(() => assets.ReadAsync(path).AsTask());
Console.WriteLine(deferred.IsValueCreated); // false，此时未读取文件内容

using (AssetData file = await deferred.Value)
    Console.WriteLine($"Loaded {file.Data.Length} bytes on demand.");
```

首次访问 `Value` 时才启动读取，所以此时集合必须仍然打开。`Lazy` 缓存的是同一个 Task 和同一个 `AssetData`；多处共享时，由统一的所有者在最后一次使用后释放一次，释放后不能再次使用缓存的 `Data`。只需要局部内容时，可直接使用下面的 `OpenRead`。

### 大文件、流和随机范围读取

`OpenRead` 返回继承 `Stream` 的 `AssetReader`，支持 Seek，并通过 `Info` 提供文件信息；创建时解析一次路径，实际读取时才按需读取、解密和解压资源块。长度和偏移使用 `long`，无需为大文件分配同等大小的内存。

```csharp
using (Stream stream = assets.OpenRead(path))
{
    byte[] header = new byte[16];
    int count = await stream.ReadAsync(header);
    Console.WriteLine(Convert.ToHexString(header.AsSpan(0, count)));

    stream.Seek(0, SeekOrigin.Begin);
    // 需要确定长度的数据时可使用 stream.ReadExactlyAsync(...)。
}

// 每次范围读取有独立位置，适合并发读取同一资源的不同区间。
byte[] range = new byte[4096];
long offset = Math.Min(1024L, info.Length);
int rangeCount = await assets.ReadAtAsync(path, offset, range);
Console.WriteLine($"Read {rangeCount} bytes at offset {offset}.");

// 高频读取同一文件：复用读取器、调用方缓冲和解码块缓存。
using (AssetReader reader = assets.OpenRead(path))
{
    int count = reader.ReadAt(0, range);
    int nextCount = await reader.ReadAtAsync(count, range);
    // 两次 ReadAt 均不改变 reader.Position。
    Console.WriteLine($"Read {count + nextCount} bytes.");
}

// 流式写出整个文件，缓冲大小不随资源长度增长。
using (FileStream output = File.Create("./settings-copy.txt"))
    await assets.CopyToAsync(path, output);
```

读取方法返回实际字节数；到达文件尾部时返回 `0`，只处理有效范围内的数据。对超过 4 GiB 的资源也使用相同 API，将示例路径替换为实际包内路径即可。同步接口为 `ReadAt` 和 `CopyTo`。

完整的[大文件分块异步示例](examples/README.md#大文件分块异步读取)通过 `--large-file` 运行，演示固定池化缓冲、逐块等待消费者、Ctrl+C 取消和流式 SHA-256；文件大小不受完整缓冲读取上限影响。

常驻索引下，文件查询与未分块文件的同步 `PackageSet.ReadAt` 不创建托管对象。反复读取压缩或加密文件时，优先复用 `AssetReader`；它复用缓冲、密码上下文与最近的部分解码块，完整块可直接读入、解密或解压到目标缓冲。预热后的缓存命中路径可实现零分配；首次缓冲申请、页缓存未命中、密钥组切换和真正挂起的异步 I/O 仍可能分配。完整 `Read` 仍返回需要释放的 `AssetData` 对象。实测条件和原始数据见 [枚举与读取热路径基准](benchmarks/README.md)。

局部读取、流读取和 `CopyToAsync` 验证块结构及加密认证，不执行整个文件的 SHA-256 比较。需要完整检查时调用 `await assets.VerifyAsync()`，它会读取所有物理包中的内容，包括被后续包覆盖的历史资源。

### 并发读取

同一个 `PackageSet` 可供多个任务共享；每项读取独立持有缓冲或流。示例将并行度限制为 4：

```csharp
await Parallel.ForEachAsync(
    assets.EnumerateFiles("common/data").Where(item => item.Length <= 1024 * 1024),
    new ParallelOptions { MaxDegreeOfParallelism = 4 },
    async (item, token) =>
    {
        using AssetData file = await assets.ReadAsync(item.Path, token);
        Console.WriteLine($"{item.Path}: {file.Data.Length} bytes");
    });
```

同一流不能同时执行多个 Read、Seek 或 Dispose；并发任务应分别调用 `OpenRead` 或使用 `ReadAtAsync`。等待任务结束后再释放集合。关闭集合会拒绝新操作，已经打开的流或正在进行的枚举继续持有底层文件租约。异步读取 API 支持传入 `CancellationToken`。

### 密码和读取选项

加密包可在打开目录时提供密码：

```csharp
var options = new ReadOptions
{
    Password = Environment.GetEnvironmentVariable("GAME_ASSET_KEY"),
    Recursive = true,
    ReadMode = AssetReadMode.RandomAccess,
    MaxMergedIndexBytes = 64L * 1024 * 1024,
    MaxBufferedAssetBytes = 32 * 1024 * 1024,
    MaxResidentIndexBytes = 64 * 1024 * 1024,
    PageCacheBytes = 8 * 1024 * 1024,
    MetadataCacheBytes = 4 * 1024 * 1024
};
using var encryptedAssets = await PackageSet.OpenAsync("./encrypted-dist", options);
```

未加密的目录不需要设置 `Password`。不同包使用不同密码时，可设置 `PasswordProvider` 回调，根据 `AssetPackageInfo.FilePath` 或 `TagId` 返回密码。

| 选项 | 默认值 | 作用范围与设置建议 |
| --- | --- | --- |
| `ReadMode` | `RandomAccess` | 文件内容和未命中的索引页使用的 I/O 后端。热缓存下频繁小块读取可选 `MemoryMapped`；需要真正异步 I/O 时选 `RandomAccess`，详见下表 |
| `MaxMergedIndexBytes` | 整个集合 64 MiB | 自动构建统一索引的数组数据区预算，独立于原包索引。超预算或数组容量时自动回退到逐包查询；0 不分配统一索引数组。不含数组对象头、少量管理对象和构建暂存 |
| `Password` | null | 所有加密包共用的密码；未加密包无需设置 |
| `PasswordProvider` | null | 按包返回密码，优先于 `Password`；返回 null 时回退到 `Password`。不同包使用不同密码时设置 |
| `Recursive` | true | 递归发现磁盘目录内的 `.asset` 包；设为 false 仅打开当前磁盘目录。与 `EnumerateFiles` / `EnumerateDirectories` 的包内递归参数互不影响 |
| `MaxBufferedAssetBytes` | 32 MiB | 每次 `Read/ReadAsync` 的完整文件长度上限；超出时抛异常。大文件优先使用 `OpenRead` / `ReadAt` / `CopyTo`，无需调高；0 仍允许空文件 |
| `MaxResidentIndexBytes` | 每包 64 MiB | 完整路径与文件记录索引不超过此值时整体载入；超过则整体改用分页，不会只保留前 64 MiB。高频查询且内存充足时可提高；0 使非空索引分页 |
| `PageCacheBytes` | 每包 8 MiB | 分别取预算的三分之一用于路径索引、Meta 和块表，每部分按 64 KiB 页向下取整。分页索引或频繁跨块读取可提高；0 不保留页，但每次未命中仍须读取、分配和解密页 |
| `MetadataCacheBytes` | 整个集合 4 MiB | 已解析 Meta 按编码字节长度计费的缓存预算；高频重复读取 Meta 时可提高。0 不保留非空编码的 Meta；解码后的字符串、数组和对象开销不计入预算 |

这些限制不是进程总内存上限。并发结果、池化缓冲的取整容量、解码对象、在途页和调用者保留的结果另占内存。`MaxResidentIndexBytes` 与 `PageCacheBytes` 都是每包预算：20 个各有 40 MiB 索引的包会常驻约 800 MiB 索引，而不是整个集合共用 64 MiB。常驻索引建立后不再使用该包的路径页缓存；Meta 和块表仍有独立页缓存。打开时还会遍历全部文件记录和路径做格式、排序校验，分页模式也会进行这项校验。

| 访问场景 | 建议模式 | 原因与边界 |
| --- | --- | --- |
| 资源集经常重复访问，热缓存下大量同步小块读取 | `MemoryMapped` | 本机此负载下更快，减少逐次读取的系统调用；适合在工作线程上进行资源读取 |
| 首次加载、冷盘、较大资源集，需要用异步 I/O 释放调用线程 | `RandomAccess` + 异步 API | 文件 I/O 未完成时可挂起；`MemoryMapped` 的 `ReadAsync` 可能同步完成，也可能因缺页阻塞调用线程 |
| 许多并发任务读取不同文件或区间 | 从 `RandomAccess` + 有界并发开始 | 可显式控制在途 I/O；两种模式最终吞吐仍取决于磁盘、工作集和访问模式，需用实际负载比较 |
| 同一压缩/加密块内反复读取 | 任一模式，优先复用 `AssetReader` | 命中读取器解码缓存后基本不再访问文件后端；复用收益通常比切换后端更重要 |
| 顺序扫描、解包或完整校验大文件 | 从 `RandomAccess` + 流式读取开始 | 内存不随单个文件大小增长；本次小块热缓存基准不能证明顺序扫描时哪种模式更快 |
| 仅做文件查询或目录枚举，且路径索引常驻 | 两种模式通常差异很小 | 查询已在托管内存中的索引，选择后端主要影响打开过程、页缓存未命中及内容读取 |

`MemoryMapped` 映射文件的虚拟地址空间，不会一次将整个包复制进托管堆，也不保证全部驻留物理内存；实际触碰的页仍占用工作集。两种模式都能利用操作系统文件缓存。映射模式也仍有索引解密和内容复制/解码缓冲，不等同于零复制。低延迟线程应避免直接承受首次缺页；真正的后端异步不意味着整个调用中的路径查找和 CPU 解码都不会同步执行。

例如，已预热的客户端资源读取可使用：

```csharp
var options = new ReadOptions
{
    ReadMode = AssetReadMode.MemoryMapped,
    MaxResidentIndexBytes = 64 * 1024 * 1024
};
using var assets = PackageSet.Open("./dist", options);
using AssetReader reader = assets.OpenRead("common/data/texture.bin");
byte[] buffer = new byte[4096];
int read = reader.ReadAt(0, buffer);
```

同一文件的后续范围读取复用 `reader` 与 `buffer`。每个并发操作使用独立读取器；选择 `RandomAccess` 时，使用 `OpenAsync`、`ReadAtAsync` 等异步 API 才能利用异步 I/O。相关实测见[读取基准](benchmarks/README.md)。

### 现有索引与统一索引的内存取舍

每个包包含按 UTF-8 路径排序的文件索引，记录大小固定为 128 字节，路径集中存放为 UTF-8 字节。集合打开时会自动在预算内合并覆盖关系，建立统一的有序文件表、目录树和直接子文件链。统一索引引用原包的路径与记录，不复制所有路径字符串；它和原有索引是叠加关系。

统一索引默认使用整个集合 64 MiB 的数组数据区预算，由 `MaxMergedIndexBytes` 调整。构建前先计数；超出预算或支持的数组容量时，自动使用逐包查询，文件和目录 API 的结果保持一致。应用可通过 `IndexInfo.IsMerged` 查看是否已建立统一索引，通过 `IndexInfo.IsFallback` 观察回退。

I/O 或认证失败、损坏索引和取消不会被自动回退掩盖，仍使打开失败。自动索引策略与 `ReadMode` 独立，随机访问和内存映射后端都能使用统一索引。CLI 构建后的物理包校验以及 `verify` 命令会在内部跳过不需要的统一索引构建。

统一索引中的 `FileExists`、`GetFileInfo`、按路径的 `ReadAt` 和 `OpenRead` 只查一份有序表，文件定位约为 `O(log F)`；目录定位约为 `O(log D)`。定位目录后，递归文件枚举遍历连续文件范围，直接文件枚举沿直接子文件链，目录枚举遍历目录节点。目录枚举不再扫描其范围内的文件来推导目录，也不逐包合并重复路径。不存在独立空目录记录，孤立 Meta-only 条目不会建立文件或目录；文件与目录同名仍可同时存在。

自动回退到逐包查询后，按路径查找最坏约为 `O(P × log n)`，其中 P 为包数、n 为每包记录数；枚举在各包定位前缀范围后合并候选项，递归目录枚举需要检查范围内文件。两种策略在索引常驻或命中页缓存时都不需要索引文件 I/O；未命中的分页索引才读取并解密所需页。已经打开的 `AssetReader` 保存内容记录，后续读取不再解析路径，但新内容块仍可能需要读取块描述符页。

通常直接使用默认打开方式，再按需查看索引诊断信息：

```csharp
using var assets = PackageSet.Open("./dist");
PackageIndexInfo index = assets.IndexInfo;
Console.WriteLine($"Merged index: {index.IsMerged}, fallback: {index.IsFallback}");
Console.WriteLine($"Files: {index.FileCount}, directories: {index.DirectoryCount}");
Console.WriteLine($"Index array payload: {index.MemoryBytes} bytes");
```

`IndexInfo.IsMerged` 为 true 表示当前使用统一索引。回退到逐包查询时，`MemoryBytes` 为 0，两个计数为 null；统一索引计数只包括最终有效文件，`DirectoryCount` 不含根目录。空集合仍有一个根节点，其数据区为 40 字节。

现有索引的精确主体大小为：

```text
每包索引字节数 = 128 × 该包物理记录数 + 该包所有路径的 UTF-8 字节数之和
```

按平均路径 48 字节估算，单包 1 万 / 10 万 / 100 万条记录的索引分别约 1.68 / 16.78 / 167.85 MiB。默认 64 MiB 门限约能整体容纳 38.1 万条这种记录；100 万条的单包默认使用分页。被补丁覆盖的历史记录仍占各自包的索引空间，不能用最终可见文件数直接估算所有包的总索引。上述数字不含缓存容器、Meta、块表、读取缓冲和文件内容工作集。

统一索引使用三份精确大小的数组：每个可见文件 24 字节（内容/Meta 的包编号与 64 位物理记录号）、每个文件 4 字节的直接子文件链编号，以及每个目录节点 40 字节。数组数据区占用准确为：

```text
IndexInfo.MemoryBytes = 28 × 可见文件数 + 40 × (DirectoryCount + 1)
```

| 可见文件数 | 目录数（假设为文件数的 10%） | 统一文件/目录索引额外主体 |
| --- | --- | --- |
| 1 万 | 1,000 | 320,040 字节，约 0.31 MiB |
| 10 万 | 1 万 | 3,200,040 字节，约 3.05 MiB |
| 100 万 | 10 万 | 32,000,040 字节，约 30.52 MiB |

以上是已实现数组布局的数据区计算，不包含原有包索引、数组对象头和少量管理对象。统一索引使用二分查找，没有额外哈希表或常驻路径字符串。为了精确分配，构建会进行两遍有序合并，暂存按包数和路径深度增长；不会先为所有物理记录创建 List 再复制到最终数组。第一遍超出预算便停止并自动回退，两遍均支持取消。最终节点编号受 .NET 数组容量限制，原包记录号和数据偏移仍为 64 位。

统一索引会增加打开时间，重复查询可以摊销这项成本。若原包 UTF-8 索引按页加载，路径比较和获取文件记录仍可能触发页读取；希望查询完全在内存中，应为必要的原包索引设置足够的 `MaxResidentIndexBytes`。索引建立与查询的实测比较见 [统一索引基准](benchmarks/README.md)。

另有一份 [.NET x64 索引表示内存测量](benchmarks/README.md#索引表示的内存测量)，用于核实结构体、数组和字典的量级。以 10 万项、48 字符 ASCII 路径为例：紧凑文件引用数组约 24 字节/项；若改为字符串字典保存完整的内容与 Meta 解析记录，字典本身约 265 字节/项，再加约 120 字节/路径字符串，合计约 385 字节/文件，尚未包含目录索引和原有包索引。因此更推荐紧凑数组和路径复用；这些数值是指定数据规模下的分配测量，不是任意规模的固定常驻开销。

### 基础包和补丁的加载顺序

排序先按包头时间，再按相对文件路径的 Ordinal 顺序。较晚内容记录覆盖内容与 Meta，较晚 Meta-only 记录只覆盖 Meta。找不到任何内容的孤立 Meta-only 路径不会成为可读资源。文件与目录分别枚举，目录由有效文件路径推导，不维护空目录记录。

未出现在补丁中的路径保持已加载的状态。例如先加载 A(a=1)、B(a=2)，再加载基于 A 生成且省略 a 的 C，结果仍为 a=2。运行库不会推断应移除哪些包；连续发布通常以上次发布 Tag 为比较基准。

集合在打开时固定包列表；目录中新增补丁后，需要重新打开集合才能读取新版本。不要原地修改正在使用的 `.asset` 文件。

## 加密与路径保护

CLI 参数统一使用单横线，例如 `-project ./quire.project.yml`、`-jobs 5`。`package`、`diff`、`unpackage`、`inspect` 和 `verify` 均支持直接传入 `-pwd "your-password"`，也可以使用 `-pwd-env VARIABLE`，两者不能同时指定。支持 `-pwd=VALUE` 语法，以 `-` 开头的密码使用这种写法。显式密码选项不能是空值，CLI 不会在日志或构建记录中保存密码。

`package` 和 `diff` 的密码选项覆盖 `quire.project.yml` 的 `encryption.key-environment`；未指定时继续从配置的环境变量读取。是否加密仍由 `encryption.enabled` 控制，在关闭加密的项目中传入密码选项会报错。读取和解包命令无需项目配置，直接使用传入的密码来源。

资源内容可选 AES-256-GCM。密码只在打开每个物理包时通过 PBKDF2-SHA256（600000 次）派生；各包使用独立随机盐。内容、索引和 Meta 使用独立域和分组子密钥。不同包密码可通过 `PasswordProvider(AssetPackageInfo)` 提供。

未加密时，路径、字段名和 Meta 值仍通过公开参数派生的 AES-GCM 密钥编码，避免磁盘区域直接出现明文。这个公开模式是可逆保护，不是保密或发布者认证。持有读取器的人可以恢复这些数据。真正保密需要启用加密并提供外部密码；公开 TagId、时间及必要布局字段用于加载。

## 并行构建与性能

`package -jobs` 控制压缩/加密工作线程；源文件读取并发固定为内部默认值 4，数据写入并发为 1，不单独暴露 CLI 参数。单个大文件也会分块进入共同的工作队列。`quire.project.yml` 的 `build.memory-mib` 限制工作缓冲配置，默认 128 MiB，范围 8–65536；资源清单、路径和源 Meta 的内存随记录数增长，另行计算。所选并发与块大小超出预算时会报错。

`build.block-size` 支持 65536、131072、262144、524288、1048576，默认 262144。这两个设置只从 YAML 读取，不提供对应 CLI 参数。改变块大小会重写相关资源，单纯改变内存预算不会产生内容补丁。数据偏移和块表均支持 64 位；当前实现支持 little-endian .NET 平台，超大包建议 64 位进程。

读取后端基准和结论见 [benchmarks/README.md](benchmarks/README.md)，字节布局见 [FORMAT.md](FORMAT.md)。

默认 `ReadMode = AssetReadMode.RandomAccess`。实测热缓存下未加密随机读取获益明显的场景可显式使用 `AssetReadMode.MemoryMapped`。映射按需由操作系统调页，资源内容仍通过受控缓冲访问；映射后端的异步读取可能同步完成，页错误也可能阻塞调用线程。加密并发与冷盘场景应按实际负载选择。

## 从源码构建和发布

在仓库根目录使用 .NET 10 SDK。解决方案和项目路径如下：

```powershell
dotnet build Quire.slnx -c Release
dotnet run --project quire-cli -c Release -- -h
```

### 生成类库包和托管工具包

类库包用于应用程序引用；托管工具包可跨平台使用，运行时需要 .NET 10：

```powershell
dotnet pack Quire.Package/Quire.Package.csproj -c Release -p:GeneratePackageOnBuild=false -o artifacts/packages
dotnet pack quire-cli/quire-cli.csproj -c Release -p:GeneratePackageOnBuild=false -p:PublishAot=false -o artifacts/packages
dotnet tool install quire-cli --tool-path artifacts/quire-tool --add-source artifacts/packages --version 1.0.0
./artifacts/quire-tool/quire-cli.exe -h
```

默认版本的产物为 `Quire.Package.1.0.0.nupkg` 和 `quire-cli.1.0.0.nupkg`。Linux/macOS 使用无 `.exe` 后缀的工具路径。命令显式关闭构建时自动打包，由当前 `pack` 命令统一生成产物。

`PublishAot=false` 只是选择托管工具包的构建方式。**从 .NET 10 开始，NuGet tool 支持 Native AOT**，但需要按操作系统和架构生成对应包。

### 生成 Native AOT NuGet 工具包

以下示例仅发布 Windows x64，使用单独的输出目录，避免与上一节同版本的托管工具包混在一起。需要先安装下一节列出的原生编译工具链。

```powershell
# 平台实现包：quire-cli.win-x64.1.0.0.nupkg
dotnet pack quire-cli/quire-cli.csproj -c Release -r win-x64 -p:GeneratePackageOnBuild=false -p:PublishAot=true -p:ToolPackageRuntimeIdentifiers=win-x64 -p:ToolEntryPoint=quire-cli.exe -o artifacts/aot-packages

# 入口包：quire-cli.1.0.0.nupkg，记录支持的平台并指向对应实现包
dotnet pack quire-cli/quire-cli.csproj -c Release -p:GeneratePackageOnBuild=false -p:PublishAot=true -p:ToolPackageRuntimeIdentifiers=win-x64 -o artifacts/aot-packages

dotnet tool install quire-cli --tool-path artifacts/quire-aot-tool --add-source artifacts/aot-packages --version 1.0.0
./artifacts/quire-aot-tool/quire-cli.exe -h
```

项目会将原生文件重命名为 `quire-cli.exe`，因此 Windows 平台包显式设置 `ToolEntryPoint=quire-cli.exe`。Linux/macOS 平台包使用 `ToolEntryPoint=quire-cli`；托管工具包不设置这个值。

多平台发布时，可在 CLI 项目中设置统一的 `ToolPackageRuntimeIdentifiers`，例如 `win-x64;linux-x64;osx-arm64`，替代上面命令中的单平台属性。分别在对应系统上执行带 `-r` 的打包命令，收集各平台实现包，最后执行不带 `-r` 的命令生成入口包。列表应只包含实际构建和发布的平台；所有实现包和入口包必须使用相同版本。需要覆盖更多平台时，也可按微软指南提供 `any` 托管回退包。

发布到 NuGet 源时，先发布各平台实现包，再发布入口包。使用者仍运行 `dotnet tool install --global quire-cli`，.NET 10 SDK 会选择匹配的平台包。原生工具的执行无需 .NET 运行时，但通过 `dotnet tool` 安装和管理它仍需要 SDK。官方说明见 [RID-specific .NET tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools)。

### Native AOT 发布

也可以直接生成独立原生可执行文件，通过文件分发，无需 NuGet tool 安装：

```powershell
dotnet publish quire-cli/quire-cli.csproj -c Release -r win-x64 -p:GeneratePackageOnBuild=false -p:PublishProfile=NativeAot -o artifacts/native/win-x64
./artifacts/native/win-x64/quire-cli.exe -h
```

产物为 `quire-cli.exe`，运行无需安装 .NET SDK 或运行时。在 Linux 本机使用 `-r linux-x64`，产物名为 `quire-cli`；macOS 根据架构使用 `osx-arm64` 或 `osx-x64`。Native AOT 需要在对应操作系统上构建：

- Windows：Visual Studio Installer 中安装“使用 C++ 的桌面开发”工作负载，包括 MSVC 工具和 Windows SDK。`Platform linker not found` 表示缺少原生链接工具链。
- Linux：安装 clang 和 zlib 开发包。
- macOS：安装 Xcode Command Line Tools。

完整平台条件见 [Native AOT 编译前提](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)。CLI 的 JSON 读写使用编译期生成的类型信息，默认关闭 JSON 反射序列化；项目模板嵌入可执行文件。Native AOT 本身生成原生可执行文件，不需要额外启用 `PublishSingleFile`。上述命令只生成本地发布产物，上传 NuGet 包或分发可执行文件是独立步骤。

# CLI 与 GUI 接入

[返回项目首页](../README.md)

`quire-cli` 可以作为 GUI、编辑器或自动化脚本的子进程。添加 `-progress` 后，CLI 将进度、查询记录和最终结果输出为结构化事件，调用方负责展示界面。

## 调用方式

```powershell
quire-cli init game -progress
quire-cli package -f ./quire.game.yml -tag v1 -progress
quire-cli tags -f ./quire.game.yml -progress
quire-cli changes v1 -f ./quire.game.yml -progress
quire-cli verify ./dist/game -progress
quire-cli keys list -progress
```

`-progress` 是无参数开关，不接受 `-progress=true`。它适用于项目初始化、打包、查询、提交、删除候选、包读取和密钥管理；帮助命令仍输出普通帮助文本。

`init PROJECT [DIRECTORY]` 要求通过参数提供项目名，不读取交互输入。成功结果的 `path` 为生成的 `quire.<project>.yml` 绝对路径，后续命令将该路径传给 `-f`。配置中的 `project` 也为必填，缺失、空字符串或纯空白均报错；所有状态保存在 `.quire/<project>/`。`-f` 未指定时仍读取 `./quire.project.yml`。

仅 `package` / `diff` 接受 `-root DIRECTORY`，GUI 可用它传入本机资源根目录。它只用于解析 YAML 中相对的 `sources.directory`，绝对资源路径保持原样；`-root` 自身若是相对路径，则以子进程工作目录为基准。省略时仍以 YAML 所在目录解析资源。输出和 `.quire/<project>/` 位置不变，事件结构也不变。

密钥命令采用固定的位置参数，开关放在末尾：

```powershell
quire-cli keys add game "your-password" -progress
quire-cli keys show game -progress
quire-cli keys export game ./client.key -progress
```

密码或导出路径本身即使为 `-progress`，仍按位置参数处理；额外的末尾开关才启用结构化输出。密钥命令不接受项目配置参数 `-f`。

`keys show KEYNAME -progress` 通过 `completed.result` 返回 `fingerprint`、`name` 和 `clientKey`，不发送 `item`。其中 `clientKey` 是 128 字符的 Base64 客户端密钥。省略 `-progress` 时只输出该密钥的一行文本，不写文件。

## 输出和生命周期

- **stdout**：UTF-8 NDJSON，每行一个完整 JSON 对象，以换行符结束。没有表格、进度条或 ANSI 控制字符。
- **stderr**：可读诊断信息，不输出异常调用栈。
- **读取方式**：同时异步读取两条流，逐行解析 stdout，并等待进程退出。

例如，查询一个没有历史记录的项目：

```json
{"type":"started","version":1,"command":"tags"}
{"type":"completed","result":{"count":0}}
```

| `type` | 字段 | 含义 |
| --- | --- | --- |
| `started` | `version`、`command` | 协议版本，目前为 `1`；`command` 是顶层命令，例如 `package` 或 `keys` |
| `progress` | 见下方进度字段 | 当前阶段的进度快照 |
| `item` | `kind`、`data` | 一条查询结果 |
| `completed` | `result` | 命令成功，附带最终结果或汇总 |
| `failed` | `error.code`、`error.message` | 命令失败，附带稳定错误码和可读原因 |
| `canceled` | `error.code`、`error.message` | 命令取消；错误码为 `canceled` |

正常流程以一个终态事件结束：`completed`、`failed` 或 `canceled`，之后不再发送进度。操作较短或查询无需扫描文件时，可以没有 `progress` 事件。

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 失败 |
| `130` | 取消 |

**同时收到 `completed` 并确认退出码为 `0`，才能认定成功。** 进程被强制终止、崩溃或输出管道中断时，可能没有完整的终态事件。解析器应按事件类型读取所需字段，允许未使用的附加字段。

错误示例：

```json
{"type":"failed","error":{"code":"invalid_argument","message":"-jobs requires an integer value."}}
```

错误码包括 `not_found`、`access_denied`、`invalid_data`、`authentication_failed`、`invalid_argument`、`io_error`、`invalid_operation` 和 `operation_failed`。GUI 根据 `code` 分类处理，使用 `message` 展示具体原因，无需匹配异常类型或调用栈。

## 进度字段

普通进度每 200 ms 合并采样；阶段切换时更新，终态输出前停止采样。工作线程按文件或数据块累计计数，采样时才生成事件。

| 字段 | 类型 / 单位 | 含义 |
| --- | --- | --- |
| `stage` | 字符串 | 当前阶段的稳定标识 |
| `completedBytes`、`totalBytes` | 整数 / 字节 | 当前阶段已完成和总工作量；总量未知为 `null` |
| `completedFiles`、`totalFiles` | 整数 | 当前阶段已完成和总文件数；总数未知为 `null` |
| `readBytes`、`writtenBytes` | 整数 / 字节 | 本次操作累计的应用读写量，不随阶段重置 |
| `readBytesPerSecond`、`writtenBytesPerSecond` | 数值 / 字节每秒 | 近期采样得到的读写速率 |
| `elapsedSeconds` | 数值 / 秒 | 从进度采集开始经过的时间 |
| `stageElapsedSeconds` | 数值 / 秒 | 当前阶段已用时间 |
| `stageFraction` | 数值 `0..1` 或 `null` | 当前阶段完成比例；未知时为 `null` |
| `stageEtaSeconds` | 数值 / 秒或 `null` | 当前阶段预计剩余时间；无法估算时为 `null` |

阶段切换会重置阶段工作量和比例。GUI 应使用阶段标签与阶段进度条；`hashing` 的 `100%` 不代表整个打包命令已经完成。扫描时尚不知道总量，可以展示 `completedFiles` 和动态指示符。

读写量包含应用实际传输的数据，例如源文件哈希读取、临时表和产物校验；它不是操作系统的物理磁盘吞吐量。压缩后的读写量与原始内容工作量不同，不能混用两组计数计算百分比。

当前阶段标识：

| 标识 | 含义 |
| --- | --- |
| `preparing`、`processing` | 准备或通用处理 |
| `discovering` | 发现源文件 |
| `hashing` | 计算源文件摘要 |
| `writing` | 写入资源包 |
| `opening`、`indexing` | 打开包、建立读取索引 |
| `preparing_verification`、`verifying` | 准备校验、校验内容 |
| `publishing` | 完成输出和构建记录 |
| `preparing_extraction`、`extracting` | 准备解包、提取文件 |
| `publishing_extraction` | 将解包结果移入目标目录 |

不同命令使用不同阶段，不保证每次都出现全部阶段。

## 查询记录与最终结果

查询列表通过多个 `item` 事件逐项输出，记录在 `data` 内：

```json
{"type":"item","kind":"change","data":{"package":"Assets","path":"assets/config.json","kind":"updated"}}
```

| `item.kind` | 来源 | `data` 字段 |
| --- | --- | --- |
| `tag` | `tags` | 下方 Tag 记录的全部字段；状态为 `candidate`、`committed` 或 `baseline` |
| `change` | `diff`、`changes` | `package`、`path`、`kind`；变化类型为 `added`、`updated`、`deleted` |
| `package` | `inspect`、`verify` | `filePath`、`tagId`、`tagName`、`tagUid`、`packageUid`、`buildTimeUtc`、`entryCount`、`isEncrypted`、`isSigned` |
| `asset` | `inspect` | `path`、`kind`、`length`、`hasMetadata`；当前查询输出文件，`kind` 为 `file` |
| `key` | `keys list` | `fingerprint`、`name`、`clientKey`；客户端密钥为 Base64 字符串 |

Tag 记录包含以下字段：

| 字段 | 含义 |
| --- | --- |
| `tagId`、`tagName` | 内部 Tag ID 和用户可见名称 |
| `parentTagId` | 构建所基于的 Tag ID；首次构建为 `null` |
| `status` | 当前状态，由操作决定 |
| `buildTimeUtc` | ISO 8601 格式的 UTC 构建时间 |
| `contentChanges`、`metadataChanges`、`deletedChanges` | 内容、仅 Meta 和删除记录的数量 |
| `addedFiles`、`updatedFiles`、`removedFiles` | 按资源路径统计的新增、更新和删除数量 |
| `outputs` | 本次生成的包路径数组，相对项目配置所在目录 |

`completed.result` 随命令变化：

| 命令 | 最终结果 |
| --- | --- |
| `init` | `{ "path": "配置文件的绝对路径" }` |
| `package` | Tag 记录，`status` 为 `candidate` |
| `commit` | Tag 记录，`status` 为 `baseline` |
| `remove` | 被移除的 Tag 记录，`status` 为 `removed`；包文件保留 |
| `tags`、`keys list` | `{ "count": 记录数 }` |
| `diff`、`changes` | `{ "added": 新增数, "updated": 更新数, "deleted": 删除数 }` |
| `inspect` | `{ "packages": 包数量, "assets": 合并后文件数 }` |
| `verify` | `{ "count": 已校验的包数量 }` |
| `unpackage` | `{ "destination": "目标绝对路径", "assets": 文件数, "metadataFiles": Meta文件数 }` |
| `keys add` | `{ "fingerprint": "指纹", "name": "名称", "clientKey": null }` |
| `keys show` | `{ "fingerprint": "指纹", "name": "名称", "clientKey": "Base64客户端密钥" }`；不发送 `item` |
| `keys remove` | `{ "name": "名称" }` |
| `keys export` | `{ "path": "客户端密钥文件的绝对路径" }` |

## C# 子进程示例

下面的 .NET 10 示例同时读取 stdout 和 stderr，逐行处理事件，并检查终态与退出码。`onEvent` 在 `JsonDocument` 释放前同步处理当前事件；更新 GUI 时，先取出所需的字符串和数值，再按界面框架派发更新。

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;

await PackageAsync("./quire.project.yml", message =>
{
    if (message.GetProperty("type").GetString() == "progress")
    {
        string stage = message.GetProperty("stage").GetString()!;
        JsonElement fraction = message.GetProperty("stageFraction");
        string percent = fraction.ValueKind == JsonValueKind.Null
            ? "..." : $"{fraction.GetDouble():P1}";
        Console.WriteLine($"{stage}: {percent}");
    }
});

static async Task PackageAsync(string projectFile, Action<JsonElement> onEvent)
{
    var start = new ProcessStartInfo("quire-cli")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };
    foreach (string argument in new[]
    {
        "package", "-f", Path.GetFullPath(projectFile), "-progress"
    })
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start)
        ?? throw new InvalidOperationException("无法启动 quire-cli。");
    string? terminal = null, failure = null;
    bool started = false;
    Exception? eventError = null;
    Task<string> diagnostics = process.StandardError.ReadToEndAsync();

    async Task ReadEventsAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            // 解析或界面处理失败后仍排空管道，避免子进程等待输出空间。
            if (eventError is not null) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                JsonElement message = document.RootElement;
                string? type = message.GetProperty("type").GetString();
                if (terminal is not null)
                    throw new InvalidDataException("CLI 在终态后继续发送事件。");
                if (!started)
                {
                    if (type != "started" || message.GetProperty("version").GetInt32() != 1)
                        throw new InvalidDataException("缺少 started 事件或协议版本不受支持。");
                    started = true;
                }
                else if (type == "started")
                    throw new InvalidDataException("CLI 重复发送 started 事件。");
                if (type is "completed" or "failed" or "canceled")
                {
                    terminal = type;
                    if (type != "completed")
                        failure = message.GetProperty("error").GetProperty("message").GetString();
                }
                onEvent(message);
            }
            catch (Exception error) { eventError = error; }
        }
    }

    Task events = ReadEventsAsync();
    await Task.WhenAll(events, diagnostics, process.WaitForExitAsync());

    if (eventError is not null)
        throw new InvalidDataException("无法处理 CLI 事件。", eventError);
    if (process.ExitCode != 0 || terminal != "completed")
    {
        string detail = failure ?? (await diagnostics).Trim();
        throw new InvalidOperationException(string.IsNullOrEmpty(detail)
            ? $"CLI 未成功完成，退出码：{process.ExitCode}。" : detail);
    }
}
```

当前 CLI 通过 Ctrl+C 支持协作取消，尚未提供 stdin 取消指令。`Process.Kill` 是强制终止，不代表事务回滚；中断后可通过 `tags` 查询实际构建状态，再决定重试或清理。

# Quire.Package 基础示例

从仓库的 GameResource.Package 目录运行：

```powershell
dotnet run --project quire-cli -c Release -- package -project examples/quire.project.yml
dotnet run --project examples/Quire.Package.Examples.csproj -c Release -- examples/dist
```

示例项目只引用 `Quire.Package`。`Program.cs` 依次展示：打开资源包目录、枚举有效路径、独立读取八种类型的 Meta、完整读取小文件、延迟加载完整数据，以及用流和偏移按需读取内容。

`Lazy<Task<AssetData>>` 在首次访问 `Value` 时加载并缓存结果，所有使用者完成后释放一次 `AssetData`。`OpenRead` 适合超大文件和只需读取局部内容的场景。`AssetData.Data` 在释放后不可继续使用，Meta 可以独立保留；同一个流不应同时执行多个读操作，并行读取请分别打开流或使用 `ReadAtAsync`。

需要读取加密的示例包时，将密码作为第二个位置参数传入。示例配置默认不加密，资源文件后缀保持 `.asset`。

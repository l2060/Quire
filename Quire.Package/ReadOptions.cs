namespace Quire.Package;
/// <summary>包文件的数据读取后端。</summary>
public enum AssetReadMode
{
    /// <summary>默认后端；同步读取使用随机访问 I/O，异步读取使用操作系统的异步文件 I/O。</summary>
    /// <remarks>适合作为通用选择，尤其是需要异步等待磁盘读取或进行并发读取时。</remarks>
    RandomAccess,
    /// <summary>通过只读内存映射访问包文件，适合系统缓存已预热后的高频小范围读取。</summary>
    /// <remarks>
    /// 映射后端在调用线程上复制数据，异步方法也可能同步完成；缺页时仍可能阻塞该线程。
    /// 映射不消除解压、解密或复制成本，也不保证所有负载都比随机访问 I/O 更快。
    /// </remarks>
    MemoryMapped
}

// Internal overrides for package verification and comparative testing.
// Public callers always use the automatic strategy.
internal enum AssetIndexMode
{
    Auto,
    PerPackage,
    Merged
}

/// <summary>包集合的读取方式、物理包发现范围和内存预算；大小参数均以字节为单位。</summary>
/// <remarks>
/// 文件和目录索引自动管理，无需选择模式。打开集合时在预算内合并覆盖关系，建立统一索引；
/// 超过预算或数组容量时自动使用各包已有索引。统一索引使路径查询不再逐包查找，目录枚举直接访问目录节点。
/// 统一索引引用原有包索引中的路径和记录，原索引分页时仍可能读取所需索引页。
/// 使用 <see cref="PackageSet.IndexInfo"/> 检查实际状态、数组数据区占用和是否发生自动回退。
/// 内存预算必须非负，且按各参数注明的作用域分别计算，并非进程总内存上限。
/// 活动读取器的缓冲区、并发请求、对象开销和操作系统文件缓存或映射不包含在这些预算内。
/// </remarks>
public sealed record ReadOptions
{
    /// <summary>数据读取后端，默认值为 <see cref = "AssetReadMode.RandomAccess"/>。</summary>
    /// <remarks>
    /// 一般使用默认值；系统缓存预热后的频繁小范围读取可测量 <see cref = "AssetReadMode.MemoryMapped"/> 的收益。
    /// 内存映射的异步方法仍会在调用线程上复制数据，缺页时可能阻塞。
    /// </remarks>
    public AssetReadMode ReadMode
    {
        get;
        init;
    } = AssetReadMode.RandomAccess;
    /// <summary>整个集合用于统一索引数组数据区的最大字节数，默认值为 64 MiB。</summary>
    /// <remarks>
    /// 预算包括文件引用、直接子文件编号和目录节点，根目录也计入；不包含原有包索引、
    /// 数组对象头、少量管理对象以及按包数和路径深度增长的构建暂存。
    /// 构建先计数再精确分配，不为全部物理记录创建临时列表或路径字符串。
    /// 超过预算或数组容量时自动使用各包已有索引；设为 0 时不构建统一索引。
    /// 此限制不影响 MaxResidentIndexBytes 和 PageCacheBytes。
    /// </remarks>
    public long MaxMergedIndexBytes
    {
        get;
        init;
    } = 64L * 1024 * 1024;
    /// <summary>加密包的默认密码；默认值为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 优先使用 <see cref = "PasswordProvider"/> 的非 null 返回值，回调未设置或返回 null 时才使用本属性。
    /// 加密包要求非空密码；未加密包忽略密码及密码回调。
    /// </remarks>
    public string? Password
    {
        get;
        init;
    }
    /// <summary>打开加密包时按包信息提供密码的回调；默认值为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 适用于不同包使用不同密码的集合。返回 null 时回退到 <see cref = "Password"/>；
    /// 返回空字符串不会回退，而会因密码为空导致打开失败。未加密包不调用此回调。
    /// </remarks>
    public Func<AssetPackageInfo, string?>? PasswordProvider
    {
        get;
        init;
    }
    /// <summary>是否递归扫描传给 Open 或 OpenAsync 的物理目录以发现包文件；默认值为 true。</summary>
    /// <remarks>
    /// 此参数只控制磁盘上的包发现范围，与 EnumerateFiles、EnumerateDirectories 的 recursive 参数独立。
    /// 设为 false 时仅打开指定物理目录中的包文件。
    /// </remarks>
    public bool Recursive
    {
        get;
        init;
    } = true;
    /// <summary>Read、ReadAsync 单次完整读取允许的文件内容长度上限；默认值为 32 MiB。</summary>
    /// <remarks>
    /// 这是每个结果的长度限制，不是全部结果或并发读取的总预算，也不包含元数据和对象开销。
    /// 超过上限时应使用 OpenRead、ReadAt 或 CopyTo 等流式或范围读取 API。
    /// 设为 0 时仍可完整读取空文件，非空文件的完整读取会被拒绝。
    /// </remarks>
    public int MaxBufferedAssetBytes
    {
        get;
        init;
    } = 32 * 1024 * 1024;
    /// <summary>每个包允许整段常驻内存的目录索引大小阈值；默认值为 64 MiB。</summary>
    /// <remarks>
    /// 目录索引大小等于条目数乘以 128 字节，再加全部路径的 UTF-8 字节数。
    /// 整段大小不超过阈值时一次性加载，否则通过页缓存按需读取；不会只常驻索引的一部分。
    /// 设为 0 可让非空索引走分页路径。多个包分别应用此阈值，常驻索引与页缓存预算独立。
    /// 无论是否常驻，打开包时仍会遍历目录索引进行验证。
    /// </remarks>
    public int MaxResidentIndexBytes
    {
        get;
        init;
    } = 64 * 1024 * 1024;
    /// <summary>每个包用于保留认证页面的缓存预算；默认值为 8 MiB。</summary>
    /// <remarks>
    /// 预算等分给目录、元数据、数据块描述符三个区段，每份按 64 KiB 页数量向下取整。
    /// 常驻目录索引不使用其页缓存份额，该份额不会转给其他区段。
    /// 设为 0 禁止保留页面，但读取时仍需加载和认证页面；不会关闭常驻索引或操作系统缓存。
    /// 此预算不包括文件内容、缓存管理对象或读取过程中的临时缓冲区。
    /// </remarks>
    public int PageCacheBytes
    {
        get;
        init;
    } = 8 * 1024 * 1024;
    /// <summary>整个 PackageSet 共享的元数据对象缓存预算；默认值为 4 MiB。</summary>
    /// <remarks>
    /// 预算按元数据的编码字节数计算，不包含元数据对象、已解码的字符串或数组等额外内存。
    /// 超过预算的单个结果仍会返回，但不会加入缓存；设为 0 禁止缓存从包中读取的元数据。
    /// 没有元数据的文件仍共享 Metadata.Empty。此缓存与每个包的元数据页缓存独立。
    /// </remarks>
    public int MetadataCacheBytes
    {
        get;
        init;
    } = 4 * 1024 * 1024;
    internal AssetIndexMode IndexMode
    {
        get;
        init;
    } = AssetIndexMode.Auto;
    internal Internal.IOperationProgress? Progress
    {
        get;
        init;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(ReadMode) || !Enum.IsDefined(IndexMode) || MaxMergedIndexBytes < 0 || MaxBufferedAssetBytes < 0 || MaxResidentIndexBytes < 0 || PageCacheBytes < 0 || MetadataCacheBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadOptions), "Memory limits cannot be negative.");
    }
}

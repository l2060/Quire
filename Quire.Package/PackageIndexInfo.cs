namespace Quire.Package;
/// <summary>打开集合时确定的索引状态；计数与预算均为整个集合的值。</summary>
/// <param name = "IsMerged">是否已建立统一文件索引和目录树。</param>
/// <param name = "MemoryBytes">统一索引数组数据区字节数，不含原有包索引、对象头和构建暂存；未建立统一索引时为 0。</param>
/// <param name = "FileCount">统一索引中的可见文件数；未建立统一索引时为 null。</param>
/// <param name = "DirectoryCount">统一索引中不含根目录的目录数；未建立统一索引时为 null。</param>
/// <param name = "IsFallback">是否因预算或数组容量限制自动回退为逐包索引。</param>
public readonly record struct PackageIndexInfo(
    bool IsMerged,
    long MemoryBytes,
    int? FileCount,
    int? DirectoryCount,
    bool IsFallback);

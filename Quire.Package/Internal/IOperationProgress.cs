namespace Quire.Package.Internal;

// Synchronous, thread-safe reporting shared by the CLI and its internal read paths.
// Work bytes describe logical stage progress; I/O bytes count actual transferred data.
internal interface IOperationProgress
{
    void StartStage(string stage, long totalBytes = -1, long totalFiles = -1);
    void Advance(long bytes = 0, long files = 0);
    void AddRead(long bytes);
    void AddWritten(long bytes);
}

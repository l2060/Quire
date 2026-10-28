using Quire.Package.Internal;

namespace Quire.Cli.Progress;

// Does not own the underlying stream; counts only reads that have completed.
internal sealed class ProgressReadStream(Stream source, IOperationProgress progress, bool advance = false) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }

    private int Report(int count)
    {
        if (count > 0)
        {
            progress.AddRead(count);
            if (advance) progress.Advance(count);
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => Report(source.Read(buffer));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void Flush() => source.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

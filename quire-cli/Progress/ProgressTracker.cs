using System.Diagnostics;
using Quire.Internal;

namespace Quire.Cli.Progress;

internal readonly record struct ProgressSnapshot(
    string Stage,
    long StageVersion,
    long CompletedBytes,
    long? TotalBytes,
    long CompletedFiles,
    long? TotalFiles,
    long ReadBytes,
    long WrittenBytes,
    double ReadBytesPerSecond,
    double WrittenBytesPerSecond,
    double ElapsedSeconds,
    double StageElapsedSeconds,
    double? StageFraction,
    double? StageEtaSeconds);

/// <summary>Collects operation counters independently of output and presentation.</summary>
internal sealed class ProgressTracker : IOperationProgress
{
    private const double SampleWindowSeconds = 2;
    private const double MinimumSampleSeconds = 0.1;
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Sample[] _samples = new Sample[32];
    private string _stage = "Preparing";
    private long _stageVersion, _totalBytes = -1, _totalFiles = -1;
    private long _bytes, _files, _read, _written;
    private double _stageStarted;
    private int _first, _count = 1;

    public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            _stage = stage;
            _stageVersion++;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _bytes = _files = 0;
            _stageStarted = Elapsed();
            _first = 0;
            _count = 1;
            _samples[0] = new(_stageStarted, _read, _written, 0, 0);
        }
    }

    // These paths only update counters. They never allocate, format or write output.
    public void Advance(long bytes = 0, long files = 0)
    {
        lock (_gate) { _bytes += bytes; _files += files; }
    }

    public void AddRead(long bytes) { lock (_gate) _read += bytes; }
    public void AddWritten(long bytes) { lock (_gate) _written += bytes; }

    internal ProgressSnapshot Snapshot()
    {
        lock (_gate)
        {
            double now = Elapsed();
            while (_count > 1 && now - _samples[_first].Time > SampleWindowSeconds)
            {
                _first = (_first + 1) % _samples.Length;
                _count--;
            }
            Sample first = _samples[_first];
            double duration = now - first.Time;
            double readRate = 0, writeRate = 0, byteRate = 0, fileRate = 0;
            if (duration >= MinimumSampleSeconds)
            {
                readRate = (_read - first.Read) / duration;
                writeRate = (_written - first.Written) / duration;
                byteRate = (_bytes - first.Bytes) / duration;
                fileRate = (_files - first.Files) / duration;
            }

            int last = (_first + _count - 1) % _samples.Length;
            if (now - _samples[last].Time >= MinimumSampleSeconds)
            {
                if (_count == _samples.Length)
                {
                    _first = (_first + 1) % _samples.Length;
                    _count--;
                }
                _samples[(_first + _count) % _samples.Length] = new(now, _read, _written, _bytes, _files);
                _count++;
            }

            double? fraction = _totalBytes > 0 ? Math.Clamp((double)_bytes / _totalBytes, 0, 1)
                : _totalFiles > 0 ? Math.Clamp((double)_files / _totalFiles, 0, 1)
                : _totalBytes == 0 || _totalFiles == 0 ? 1 : null;
            // Reading the last byte does not mean that its checksum/file has been accepted.
            if (fraction >= 0.999 && (_bytes < _totalBytes || _files < _totalFiles)) fraction = 0.999;
            double? eta = _totalBytes > _bytes && byteRate > 0 ? (_totalBytes - _bytes) / byteRate
                : _totalFiles > _files && fileRate > 0 ? (_totalFiles - _files) / fileRate
                : fraction == 1 ? 0 : null;

            return new(_stage, _stageVersion, _bytes, _totalBytes >= 0 ? _totalBytes : null,
                _files, _totalFiles >= 0 ? _totalFiles : null, _read, _written,
                readRate, writeRate, now, now - _stageStarted, fraction, eta);
        }
    }

    private double Elapsed() => Stopwatch.GetElapsedTime(_started).TotalSeconds;
    private readonly record struct Sample(double Time, long Read, long Written, long Bytes, long Files);
}

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Quire.Internal;

namespace Quire.Cli.Progress;

// One command owns its output and terminal event. Work callbacks only update counters;
// JSON serialization happens at stage boundaries or on the sampling timer.
internal sealed class CommandOutput : IOperationProgress, IDisposable
{
    private readonly object _gate = new();
    private readonly string _command;
    private readonly TextWriter _output;
    private readonly CancellationTokenSource _cancellation;
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly Utf8JsonWriter _json;
    private char[] _characters = new char[1024];
    private ProgressTracker? _tracker;
    private ConsoleProgress? _console;
    private Timer? _timer;
    private Action<Utf8JsonWriter>? _result;
    private Exception? _outputError;
    private bool _terminal;
    private volatile bool _stopped;

    internal bool Structured { get; }
    internal CancellationToken Token => _cancellation.Token;

    internal CommandOutput(string command, bool structured, CancellationToken token)
    {
        _command = command;
        Structured = structured;
        _output = Console.Out;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _json = new Utf8JsonWriter(_buffer);
    }

    internal void Started()
    {
        if (!Structured) return;
        lock (_gate)
        {
            Begin("started");
            _json.WriteNumber("version", 1);
            _json.WriteString("command", _command);
            End();
        }
    }

    internal IOperationProgress StartProgress()
    {
        if (!Structured)
        {
            _console ??= new ConsoleProgress(_command) { ErrorHandler = CaptureOutputError };
            if (_console.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return _console;
        }
        if (_tracker is null)
        {
            _tracker = new ProgressTracker();
            _timer = new Timer(_ => EmitProgress(), null, 200, 200);
        }
        return this;
    }

    public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
    {
        _tracker!.StartStage(stage, totalBytes, totalFiles);
        EmitProgress();
        ThrowOutputError();
    }
    public void Advance(long bytes = 0, long files = 0) => _tracker!.Advance(bytes, files);
    public void AddRead(long bytes) => _tracker!.AddRead(bytes);
    public void AddWritten(long bytes) => _tracker!.AddWritten(bytes);

    internal void Item<T>(string kind, T value, JsonTypeInfo<T> type)
    {
        if (!Structured) return;
        Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Begin("item");
            _json.WriteString("kind", kind);
            _json.WritePropertyName("data");
            JsonSerializer.Serialize(_json, value, type);
            End();
        }
    }

    internal void Result<T>(T value, JsonTypeInfo<T> type)
    {
        // Results are small and retained once per command; large listings use Item.
        if (Structured) _result = writer => JsonSerializer.Serialize(writer, value, type);
    }

    internal void FinishProgress()
    {
        // End the terminal frame before human result lines are written to stdout.
        if (_console?.Error is { } before) ExceptionDispatchInfo.Capture(before).Throw();
        _console?.Complete();
        if (_console?.Error is { } after) ExceptionDispatchInfo.Capture(after).Throw();
    }

    internal async Task CompleteAsync()
    {
        StopSampling();
        ThrowOutputError();
        if (_console?.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        if (!Structured) { FinishProgress(); return; }
        await WriteTerminalAsync(() =>
        {
            lock (_gate)
            {
                ThrowOutputError();
                if (_tracker is not null) WriteProgress();
                Begin("completed");
                _json.WritePropertyName("result");
                if (_result is null) { _json.WriteStartObject(); _json.WriteEndObject(); }
                else _result(_json);
                _terminal = true;
                End();
            }
        }).ConfigureAwait(false);
    }

    internal async Task<Exception> FailAsync(Exception error)
    {
        StopSampling();
        if (error is OperationCanceledException && Volatile.Read(ref _outputError) is { } outputError)
            error = outputError;
        _console?.Fail(error is OperationCanceledException);
        // Limit the entire terminal write, including a blocked pipe's Write/Flush.
        if (Structured && Volatile.Read(ref _outputError) is null)
        {
            try
            {
                await WriteTerminalAsync(() =>
                {
                    lock (_gate)
                    {
                        if (_terminal || _outputError is not null) return;
                        _terminal = true;
                        Begin(error is OperationCanceledException ? "canceled" : "failed");
                        _json.WriteStartObject("error");
                        _json.WriteString("code", CliError.Code(error));
                        _json.WriteString("message", CliError.Message(error));
                        _json.WriteEndObject();
                        End();
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception) { /* Preserve the operation failure if the output pipe is closed. */ }
        }
        return error;
    }

    private async Task WriteTerminalAsync(Action write)
    {
        Task task = Task.Run(write, CancellationToken.None);
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var error = new IOException("The progress output is not responding. Ensure the caller is reading standard output.");
            CaptureOutputError(error);
            throw error;
        }
    }

    private void EmitProgress()
    {
        if (_stopped || !Monitor.TryEnter(_gate)) return;
        try
        {
            if (_stopped || _terminal || _outputError is not null || _tracker is null) return;
            WriteProgress();
        }
        catch (Exception error)
        {
            CaptureOutputError(error);
        }
        finally { Monitor.Exit(_gate); }
    }

    private void WriteProgress()
    {
        var snapshot = _tracker!.Snapshot();
        Begin("progress");
        _json.WriteString("stage", StageId(snapshot.Stage));
        _json.WriteNumber("completedBytes", snapshot.CompletedBytes);
        Number("totalBytes", snapshot.TotalBytes);
        _json.WriteNumber("completedFiles", snapshot.CompletedFiles);
        Number("totalFiles", snapshot.TotalFiles);
        _json.WriteNumber("readBytes", snapshot.ReadBytes);
        _json.WriteNumber("writtenBytes", snapshot.WrittenBytes);
        _json.WriteNumber("readBytesPerSecond", snapshot.ReadBytesPerSecond);
        _json.WriteNumber("writtenBytesPerSecond", snapshot.WrittenBytesPerSecond);
        _json.WriteNumber("elapsedSeconds", snapshot.ElapsedSeconds);
        _json.WriteNumber("stageElapsedSeconds", snapshot.StageElapsedSeconds);
        Number("stageFraction", snapshot.StageFraction);
        Number("stageEtaSeconds", snapshot.StageEtaSeconds);
        End();
    }

    private void Number(string name, long? value)
    { if (value is { } number) _json.WriteNumber(name, number); else _json.WriteNull(name); }
    private void Number(string name, double? value)
    { if (value is { } number) _json.WriteNumber(name, number); else _json.WriteNull(name); }

    private static string StageId(string stage) => stage switch
    {
        "Preparing" => "preparing",
        "Discovering" => "discovering",
        "Hashing" => "hashing",
        "Writing" => "writing",
        "Opening packages" => "opening",
        "Building merged index" => "indexing",
        "Preparing verification" => "preparing_verification",
        "Verifying" => "verifying",
        "Publishing" => "publishing",
        "Preparing extraction" => "preparing_extraction",
        "Extracting" => "extracting",
        "Publishing extraction" => "publishing_extraction",
        _ => "processing"
    };

    private void Begin(string type)
    {
        _buffer.Clear();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteString("type", type);
    }

    private void End()
    {
        _json.WriteEndObject();
        _json.Flush();
        int size = Encoding.UTF8.GetCharCount(_buffer.WrittenSpan);
        if (_characters.Length < size) _characters = new char[Math.Max(size, _characters.Length * 2)];
        int count = Encoding.UTF8.GetChars(_buffer.WrittenSpan, _characters);
        try
        {
            _output.Write(_characters.AsSpan(0, count));
            _output.Write('\n');
            _output.Flush();
        }
        catch (Exception error) { CaptureOutputError(error); throw; }
    }

    private void CaptureOutputError(Exception error)
    {
        Interlocked.CompareExchange(ref _outputError, error, null);
        try { _cancellation.Cancel(); }
        catch (Exception) { /* Keep the original rendering/output error. */ }
    }

    private void ThrowOutputError()
    {
        if (Volatile.Read(ref _outputError) is { } error) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void StopSampling()
    {
        _stopped = true;
        _timer?.Dispose();
    }

    public void Dispose()
    {
        StopSampling();
        _console?.Dispose();
        // Timer callbacks check _stopped under the gate. Never wait on terminal I/O.
        if (Monitor.TryEnter(_gate))
        {
            try { _json.Dispose(); _cancellation.Dispose(); }
            finally { Monitor.Exit(_gate); }
        }
    }
}

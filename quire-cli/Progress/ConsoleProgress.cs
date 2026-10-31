using System.Globalization;
using System.Runtime.InteropServices;
using Quire.Internal;

namespace Quire.Cli.Progress;

internal sealed class ConsoleProgress : IOperationProgress, IDisposable
{
    private static readonly string[] SizeUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private readonly object _lifecycle = new(), _renderGate = new();
    private readonly ProgressTracker _tracker = new();
    private readonly string _command;
    private readonly TextWriter _writer;
    private readonly Terminal _terminal;
    private readonly Func<int> _width;
    private readonly Timer? _timer;
    private double _lastPlain = double.NegativeInfinity;
    private bool _drawn;
    private volatile bool _finished, _disposed, _outputFailed;
    private int _lastWidth;
    private string? _status;
    private Exception? _error;
    private Action<Exception>? _errorHandler;

    // Non-I/O rendering failures are observed by the command boundary, not thrown on a Timer thread.
    internal Exception? Error => Volatile.Read(ref _error);
    internal Action<Exception>? ErrorHandler
    {
        get => Volatile.Read(ref _errorHandler);
        set => Volatile.Write(ref _errorHandler, value);
    }

    internal ConsoleProgress(string command)
        : this(command, Console.Error, DetectTerminal(), () => Console.WindowWidth, true) { }

    internal ConsoleProgress(string command, TextWriter writer, bool interactive, bool color, Func<int> width, bool autoRefresh = true)
        : this(command, writer, new Terminal(interactive, true, color, 0, 0, false), width, autoRefresh) { }

    private ConsoleProgress(string command, TextWriter writer, Terminal terminal, Func<int> width, bool autoRefresh)
    {
        _command = command; _writer = writer; _terminal = terminal; _width = width;
        RenderSafely(true);
        if (autoRefresh) _timer = new Timer(_ => Refresh(), null, 100, 100);
    }

    public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
    {
        if (_finished || _disposed) return;
        _tracker.StartStage(stage, totalBytes, totalFiles);
        RenderSafely(true);
    }

    public void Advance(long bytes = 0, long files = 0) => _tracker.Advance(bytes, files);
    public void AddRead(long bytes) => _tracker.AddRead(bytes);
    public void AddWritten(long bytes) => _tracker.AddWritten(bytes);

    internal void Refresh()
    {
        if (!_disposed && !_finished) RenderSafely(false);
    }

    internal void Complete() => Finish("Done");
    internal void Fail(bool canceled = false) => Finish(canceled ? "Canceled" : "Failed");

    private void Finish(string status)
    {
        lock (_lifecycle)
        {
            if (_finished || _disposed) return;
            _status = status;
            _finished = true;
        }
        RenderSafely(true);
    }

    private void RenderSafely(bool force)
    {
        // Skip a frame instead of waiting for another renderer holding the output lock.
        if (_disposed || _outputFailed || !Monitor.TryEnter(_renderGate)) return;
        try
        {
            if (!_disposed && !_outputFailed) Render(force);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Human-readable progress is optional when its log pipe closes.
            _outputFailed = true;
        }
        catch (Exception error)
        {
            Volatile.Write(ref _error, error);
            _outputFailed = true;
            try { Volatile.Read(ref _errorHandler)?.Invoke(error); }
            catch { /* Keep the original rendering error if its cancellation callback fails. */ }
        }
        finally { Monitor.Exit(_renderGate); }
    }

    private void Render(bool force)
    {
        ProgressSnapshot snapshot = _tracker.Snapshot();
        double now = snapshot.ElapsedSeconds;
        if (!_terminal.Interactive && !force && now - _lastPlain < 5) return;
        _lastPlain = now;

        bool finished = _finished, done = _status == "Done";
        double? fraction = done ? 1 : snapshot.StageFraction;
        string percent = fraction is { } value ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";
        string files = snapshot.TotalFiles is { } totalFiles
            ? $"{snapshot.CompletedFiles.ToString("N0", CultureInfo.InvariantCulture)}/{totalFiles.ToString("N0", CultureInfo.InvariantCulture)} files"
            : snapshot.CompletedFiles > 0 || snapshot.Stage == "Discovering"
                ? $"{snapshot.CompletedFiles.ToString("N0", CultureInfo.InvariantCulture)} files discovered" : "";
        string work = snapshot.TotalBytes is > 0
            ? $"{FormatSize(snapshot.CompletedBytes)} / {FormatSize(snapshot.TotalBytes.Value)}" : "";
        string timing = $"Elapsed {FormatTime(Math.Floor(now))}";
        if (!finished && fraction.HasValue)
            timing += $" | Stage ETA {FormatTime(snapshot.StageEtaSeconds ?? double.NaN)}";

        string metrics;
        if (finished)
        {
            metrics = snapshot.ReadBytes > 0 ? $"Read {FormatSize(snapshot.ReadBytes)}" : "";
            if (snapshot.WrittenBytes > 0) Append(ref metrics, $"Write {FormatSize(snapshot.WrittenBytes)}");
        }
        else
        {
            bool writing = snapshot.Stage is "Writing" or "Extracting" or "Publishing extraction";
            metrics = writing && snapshot.WrittenBytesPerSecond > 0
                ? $"Write {FormatSize(snapshot.WrittenBytesPerSecond)}/s"
                : snapshot.ReadBytesPerSecond > 0 ? $"Read {FormatSize(snapshot.ReadBytesPerSecond)}/s"
                : snapshot.WrittenBytesPerSecond > 0 ? $"Write {FormatSize(snapshot.WrittenBytesPerSecond)}/s" : "";
        }
        string details = files;
        Append(ref details, metrics);
        Append(ref details, timing);

        if (!_terminal.Interactive)
        {
            string line = $"[{_command}] {_status ?? snapshot.Stage}";
            Append(ref line, percent);
            Append(ref line, work);
            Append(ref line, details);
            _writer.WriteLine(line);
        }
        else
        {
            int width;
            try { int columns = _width(); width = columns > 0 ? Math.Clamp(columns - 1, 1, 240) : 99; }
            catch (Exception error) when (error is IOException or PlatformNotSupportedException) { width = 99; }
            string mark = finished ? done ? (_terminal.Unicode ? "✓" : "+") : "!"
                : _terminal.Unicode ? "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[(int)(now * 10 % 10)].ToString() : "|/-\\"[(int)(now * 10 % 4)].ToString();
            string title = $"{mark} {_command} {(_terminal.Unicode ? "·" : "|")} {_status ?? snapshot.Stage}";
            string body;
            if (fraction is { } progress)
            {
                string suffix = percent;
                if (width >= 70) Append(ref suffix, work);
                int barWidth = Math.Clamp(width - suffix.Length - 5, 4, 40);
                int filled = (int)(progress * barWidth);
                string bar = new string(_terminal.Unicode ? '━' : '=', filled) + new string(_terminal.Unicode ? '─' : '-', barWidth - filled);
                body = $"[{bar}] {suffix}";
            }
            else
                body = files.Length > 0 ? files : finished ? $"Stage: {snapshot.Stage}" : "Working...";
            if (!fraction.HasValue) details = metrics.Length == 0 ? timing : metrics + " | " + timing;

            string[] lines = [title, body, details];
            // Start a fresh frame on resize so previous wrapped lines are not treated as this frame.
            if (_drawn && width == _lastWidth) _writer.Write("\u001b[2A\r");
            else if (_drawn) _writer.WriteLine();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Length > width ? lines[i][..width] : lines[i];
                _writer.Write("\u001b[2K\r");
                if (_terminal.Color) _writer.Write(i == 0 ? done ? "\u001b[32m" : finished ? "\u001b[31m" : "\u001b[36m" : "\u001b[90m");
                _writer.Write(line);
                if (_terminal.Color) _writer.Write("\u001b[0m");
                if (i != lines.Length - 1 || finished) _writer.WriteLine();
            }
            _drawn = true; _lastWidth = width;
        }
        _writer.Flush();
    }

    private static void Append(ref string text, string value)
    {
        if (value.Length > 0) text = text.Length == 0 ? value : text + " | " + value;
    }

    internal static string FormatSize(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < SizeUnits.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
    }

    internal static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "--:--";
        long value = (long)Math.Min(Math.Ceiling(seconds), TimeSpan.MaxValue.TotalSeconds);
        return value >= 3600 ? $"{value / 3600:00}:{value / 60 % 60:00}:{value % 60:00}" : $"{value / 60:00}:{value % 60:00}";
    }

    public void Dispose()
    {
        if (!_finished) Finish("Stopped");
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer?.Dispose();
        if (_terminal.RestoreMode) SetConsoleMode(_terminal.Handle, _terminal.OriginalMode);
    }

    private static Terminal DetectTerminal()
    {
        bool interactive = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb"
            && Environment.GetEnvironmentVariable("CI") is not ("true" or "1");
        bool unicode = Console.OutputEncoding.CodePage is 65001 or 1200;
        bool color = Environment.GetEnvironmentVariable("NO_COLOR") is null;
        nint handle = 0; uint mode = 0; bool restore = false;
        if (interactive && OperatingSystem.IsWindows())
        {
            handle = GetStdHandle(-12);
            if (GetConsoleMode(handle, out mode) == 0) interactive = false;
            else if ((mode & 4) == 0) { restore = SetConsoleMode(handle, mode | 4) != 0; interactive = restore; }
        }
        return new(interactive, unicode, color, handle, mode, restore);
    }

    private readonly record struct Terminal(bool Interactive, bool Unicode, bool Color, nint Handle, uint OriginalMode, bool RestoreMode);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    private static extern int GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")]
    private static extern int SetConsoleMode(nint handle, uint mode);
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Quire.Package.Internal;

namespace Quire.Cli.Progress;

internal sealed class ConsoleProgress : IOperationProgress, IDisposable
{
    private readonly object _gate = new();
    private readonly string _command;
    private readonly TextWriter _writer;
    private readonly Terminal _terminal;
    private readonly Func<int> _width;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<Sample> _samples = new();
    private readonly Timer? _timer;
    private string _stage = "Preparing";
    private long _totalBytes = -1, _totalFiles = -1, _bytes, _files, _read, _written;
    private double _lastPlain = double.NegativeInfinity;
    private bool _drawn, _finished, _disposed, _outputFailed;
    private int _lastWidth;
    private string? _status;

    internal ConsoleProgress(string command)
        : this(command, Console.Error, DetectTerminal(), () => Console.WindowWidth, true) { }

    internal ConsoleProgress(string command, TextWriter writer, bool interactive, bool color, Func<int> width, bool autoRefresh = true)
        : this(command, writer, new Terminal(interactive, true, color, 0, 0, false), width, autoRefresh) { }

    private ConsoleProgress(string command, TextWriter writer, Terminal terminal, Func<int> width, bool autoRefresh)
    {
        _command = command; _writer = writer; _terminal = terminal; _width = width;
        _samples.Enqueue(new(0, 0, 0, 0, 0));
        Render(true);
        if (autoRefresh) _timer = new Timer(_ => Refresh(), null, 100, 100);
    }

    public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
    {
        lock (_gate)
        {
            if (_finished || _disposed) return;
            _stage = stage; _totalBytes = totalBytes; _totalFiles = totalFiles; _bytes = _files = 0;
            _samples.Clear();
            _samples.Enqueue(new(_clock.Elapsed.TotalSeconds, _read, _written, 0, 0));
            Render(true);
        }
    }

    public void Advance(long bytes = 0, long files = 0)
    {
        lock (_gate) { _bytes += bytes; _files += files; }
    }
    public void AddRead(long bytes) { lock (_gate) _read += bytes; }
    public void AddWritten(long bytes) { lock (_gate) _written += bytes; }

    internal void Refresh()
    {
        lock (_gate) { if (!_disposed && !_finished) Render(false); }
    }

    internal void Complete() => Finish("Done");
    internal void Fail(bool canceled = false) => Finish(canceled ? "Canceled" : "Failed");

    private void Finish(string status)
    {
        lock (_gate)
        {
            if (_finished || _disposed) return;
            _finished = true; _status = status;
            Render(true);
        }
    }

    private void Render(bool force)
    {
        if (_outputFailed) return;
        double now = _clock.Elapsed.TotalSeconds;
        while (_samples.Count > 1 && now - _samples.Peek().Time > 2) _samples.Dequeue();
        Sample first = _samples.Peek();
        double duration = now - first.Time;
        double readRate = duration >= 0.1 ? (_read - first.Read) / duration : 0;
        double writeRate = duration >= 0.1 ? (_written - first.Written) / duration : 0;
        double workRate = duration >= 0.1 ? (_bytes - first.Bytes) / duration : 0;
        double fileRate = duration >= 0.1 ? (_files - first.Files) / duration : 0;
        _samples.Enqueue(new(now, _read, _written, _bytes, _files));
        if (!_terminal.Interactive && !force && now - _lastPlain < 5) return;
        _lastPlain = now;

        bool done = _status == "Done";
        double? fraction = done ? 1 : _totalBytes > 0 ? Math.Clamp((double)_bytes / _totalBytes, 0, 1)
            : _totalFiles > 0 ? Math.Clamp((double)_files / _totalFiles, 0, 1)
            : _totalFiles == 0 || _totalBytes == 0 ? 1 : null;
        if (!done && fraction >= 0.999 && (_totalFiles > _files || _totalBytes > _bytes)) fraction = 0.999;
        string percent = fraction is { } value ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : " --.-%";
        string eta = done ? "00:00" : _finished ? "--:--"
            : _totalBytes > _bytes && workRate > 0 ? FormatTime((_totalBytes - _bytes) / workRate)
            : _totalFiles > _files && fileRate > 0 ? FormatTime((_totalFiles - _files) / fileRate)
            : fraction == 1 ? "00:00" : "--:--";
        string files = _totalFiles >= 0 ? $"{_files}/{_totalFiles} files" : "";
        string work = _totalBytes > 0 ? $"{FormatSize(_bytes)} / {FormatSize(_totalBytes)}" : files;
        // Completion rates are command averages; live rates use the most recent two seconds.
        if (_finished && now > 0) { readRate = _read / now; writeRate = _written / now; }
        string read = $"Read {FormatSize(readRate)}/s | {FormatSize(_read)}";
        string written = $"Write {FormatSize(writeRate)}/s | {FormatSize(_written)}";
        string timing = $"Elapsed {FormatTime(Math.Floor(now))} | ETA {eta}";
        try
        {
            if (!_terminal.Interactive)
            {
                _writer.WriteLine($"[{_command}] {_status ?? _stage} | {percent} | {files} | {read} | {written} | {timing}");
            }
            else
            {
                int width;
                try { int columns = _width(); width = columns > 0 ? Math.Clamp(columns - 1, 1, 240) : 99; }
                catch (Exception e) when (e is IOException or PlatformNotSupportedException) { width = 99; }
                string mark = _finished ? done ? (_terminal.Unicode ? "✓" : "+") : "!"
                    : _terminal.Unicode ? "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[(int)(now * 10) % 10].ToString() : "|/-\\"[(int)(now * 10) % 4].ToString();
                string title = $"{mark} QUIRE / {_command.ToUpperInvariant()}  {(_terminal.Unicode ? "·" : "|")}  {_status ?? _stage}";
                int barWidth = Math.Clamp(width - percent.Length - files.Length - 7, 4, 42);
                int filled = fraction is { } f ? (int)(f * barWidth) : -1;
                string bar = filled >= 0
                    ? new string(_terminal.Unicode ? '━' : '=', filled) + new string(_terminal.Unicode ? '─' : '-', barWidth - filled)
                    : new string(_terminal.Unicode ? '─' : '-', barWidth);
                if (filled < 0)
                {
                    int position = (int)(now * 8) % barWidth;
                    bar = bar[..position] + (_terminal.Unicode ? '━' : '=') + bar[(position + 1)..];
                }
                string[] lines = width >= 90
                    ? [title, $"[{bar}] {percent}  {files}", $"{read}    {written}", $"{timing}  |  {work}"]
                    : [title, $"[{bar}] {percent}  {files}", read, written, timing];
                // Start a fresh frame on resize, so old wrapped lines are never overwritten as unrelated output.
                if (_drawn && width == _lastWidth) _writer.Write($"\u001b[{lines.Length - 1}A\r");
                else if (_drawn) _writer.WriteLine();
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Length > width ? lines[i][..width] : lines[i];
                    _writer.Write("\u001b[2K\r");
                    if (_terminal.Color) _writer.Write(i <= 1 ? done ? "\u001b[32m" : _finished ? "\u001b[31m" : "\u001b[36m" : "\u001b[90m");
                    _writer.Write(line);
                    if (_terminal.Color) _writer.Write("\u001b[0m");
                    if (i != lines.Length - 1 || _finished) _writer.WriteLine();
                }
                _drawn = true; _lastWidth = width;
            }
            _writer.Flush();
        }
        // Progress output must not turn a successful package operation into a failure (e.g. a closed log pipe).
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _outputFailed = true; }
    }

    internal static string FormatSize(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    internal static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "--:--";
        long value = (long)Math.Min(Math.Ceiling(seconds), TimeSpan.MaxValue.TotalSeconds);
        return value >= 3600 ? $"{value / 3600:00}:{value / 60 % 60:00}:{value % 60:00}" : $"{value / 60:00}:{value % 60:00}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_finished) Finish("Stopped");
            _disposed = true;
            _timer?.Dispose();
            if (_terminal.RestoreMode) SetConsoleMode(_terminal.Handle, _terminal.OriginalMode);
        }
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

    private readonly record struct Sample(double Time, long Read, long Written, long Bytes, long Files);
    private readonly record struct Terminal(bool Interactive, bool Unicode, bool Color, nint Handle, uint OriginalMode, bool RestoreMode);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    private static extern int GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")]
    private static extern int SetConsoleMode(nint handle, uint mode);
}

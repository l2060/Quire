using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Quire.Cli;
using Quire.Cli.Configuration;
using Quire.Cli.Progress;

internal static class CliProgressProtocolTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("structured progress streams a package result and management records", WorkflowAsync);
        test("structured reads stream package and asset records without human output", ReadCommandsAsync);
        test("structured failures and cancellation produce one terminal event", FailuresAsync);
        test("structured global key commands preserve positional passwords", KeysAsync);
        test("keys show emits a single structured result and preserves its positional key argument", KeyShowAsync);
        test("CLI processes terminate with readable errors and structured failure records", ProcessFailuresAsync);
        test("a failed terminal flush never emits a second terminal record", TerminalFlushFailureAsync);
        test("a blocked terminal flush has a bounded failure path", BlockedTerminalFlushAsync);
        test("progress output failure cancels work and preserves the original error", ProgressWriteFailureAsync);
    }

    private static async Task WorkflowAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("unchanged", [1, 2, 3]);
        fixture.Put("changed", new byte[2 * 1024 * 1024 + 7]);
        fixture.Put("removed", [4, 5]);
        var package = await CaptureAsync(["package", "-f", fixture.ProjectPath, "-tag", "protocol-初版", "-progress"]);
        AssertTerminal(package, "completed");
        Check.Equal("package", package.Events[0].GetProperty("command").GetString());
        JsonElement result = package.Events[^1].GetProperty("result");
        Check.Equal("protocol-初版", result.GetProperty("tagName").GetString());
        Check.Equal("candidate", result.GetProperty("status").GetString());
        Check.Equal(JsonValueKind.Null, result.GetProperty("parentTagId").ValueKind);
        Check.Equal(3, result.GetProperty("contentChanges").GetInt32());
        Check.Equal(1, result.GetProperty("outputs").GetArrayLength());
        string tagId = result.GetProperty("tagId").GetString()!;
        Check.True(tagId.Length > 0);
        Check.True(package.Events.Any(IsProgress), "A package operation must emit progress snapshots.");
        AssertProgress(package.Events.Where(IsProgress));
        Check.True(string.IsNullOrWhiteSpace(package.StandardError), "Machine mode must not render terminal progress.");

        var commit = await CaptureAsync(["commit", "protocol-初版", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(commit, "completed");
        var tags = await CaptureAsync(["tags", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(tags, "completed");
        JsonElement tag = Items(tags, "tag").Single();
        Check.Equal(tagId, tag.GetProperty("tagId").GetString());
        Check.Equal("baseline", tag.GetProperty("status").GetString());
        Check.Equal("protocol-初版", tag.GetProperty("tagName").GetString());

        fixture.Put("changed", [8, 9]);
        fixture.Put("added/中文", [10]);
        File.Delete(Path.Combine(fixture.Input, "removed"));
        var diff = await CaptureAsync(["diff", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(diff, "completed");
        var changes = Items(diff, "change").ToArray();
        Check.Equal(3, changes.Length);
        foreach (var (path, kind) in new[]
        {
            ("common/data/changed", "updated"),
            ("common/data/added/中文", "added"),
            ("common/data/removed", "deleted")
        })
        {
            JsonElement change = changes.Single(item => item.GetProperty("path").GetString() == path);
            Check.Equal("main", change.GetProperty("package").GetString());
            Check.Equal(kind, change.GetProperty("kind").GetString());
        }
        var historical = await CaptureAsync(["changes", "protocol-初版", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(historical, "completed");
        Check.Equal(3, Items(historical, "change").Count());
        Check.True(Items(historical, "change").All(item => item.GetProperty("kind").GetString() == "added"));
        Check.True(!Items(historical, "change").Any(item => item.GetProperty("path").GetString() == "common/data/added/中文"));
    }

    private static async Task ReadCommandsAsync()
    {
        using var fixture = new Fixture(false, true);
        fixture.Put("content", [11, 12, 13]);
        fixture.Text("content.meta", "revision=1");
        await fixture.Build();
        var inspect = await CaptureAsync(["inspect", fixture.Output, "-key", fixture.ClientKeyPath, "-progress"]);
        AssertTerminal(inspect, "completed");
        Check.Equal(1, Items(inspect, "package").Count());
        Check.Equal(1, Items(inspect, "asset").Count());
        Check.Equal("common/data/content", Items(inspect, "asset").Single().GetProperty("path").GetString());
        var verify = await CaptureAsync(["verify", fixture.Output, "-key", fixture.ClientKeyPath, "-progress"]);
        AssertTerminal(verify, "completed");
        AssertProgress(verify.Events.Where(IsProgress));
        string destination = Path.Combine(fixture.Root, "restored");
        var extract = await CaptureAsync(["unpackage", fixture.Output, destination, "-key", fixture.ClientKeyPath, "-progress"]);
        AssertTerminal(extract, "completed");
        AssertProgress(extract.Events.Where(IsProgress));
        Check.Bytes([11, 12, 13], File.ReadAllBytes(Path.Combine(destination, "common/data/content")));
        Check.True(File.Exists(Path.Combine(destination, "common/data/content.meta")));
    }

    private static async Task FailuresAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("content", [1]);
        var missingConfig = await CaptureAsync(["package", "-f", Path.Combine(fixture.Root, "absent.yml"), "-progress"]);
        AssertTerminal(missingConfig, "failed");
        Check.True(missingConfig.Error is FileNotFoundException);
        AssertReadableError(missingConfig.Events[^1]);

        string yaml = File.ReadAllText(fixture.ProjectPath);
        File.WriteAllText(fixture.ProjectPath, yaml.Replace("secret-key: none", "secret-key: " + fixture.KeyName, StringComparison.Ordinal));
        var missingKey = await CaptureAsync(["package", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(missingKey, "failed");
        Check.True(missingKey.Error is FileNotFoundException);
        Check.True(missingKey.Events[^1].GetProperty("error").GetProperty("message").GetString()!.Contains(fixture.KeyName, StringComparison.Ordinal));
        File.WriteAllText(fixture.ProjectPath, yaml);

        File.AppendAllText(fixture.ProjectPath, "invalid-setting: true\n");
        var invalidConfig = await CaptureAsync(["package", "-f", fixture.ProjectPath, "-progress"]);
        AssertTerminal(invalidConfig, "failed");
        Check.True(invalidConfig.Error is InvalidDataException);
        AssertReadableError(invalidConfig.Events[^1]);
        File.WriteAllText(fixture.ProjectPath, yaml);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellation = await CaptureAsync(["package", "-f", fixture.ProjectPath, "-progress"], canceled.Token);
        AssertTerminal(cancellation, "canceled");
        Check.True(cancellation.Error is OperationCanceledException);
        Check.True(!Directory.Exists(fixture.Output));
    }

    private static async Task KeysAsync()
    {
        using var fixture = new Fixture(false, true);
        var list = await CaptureAsync(["keys", "list", "-progress"]);
        AssertTerminal(list, "completed");
        JsonElement key = Items(list, "key").Single(item => item.GetProperty("name").GetString() == fixture.KeyName);
        Check.Equal(SecretKeyStore.Fingerprint(TestKeys.Client), key.GetProperty("fingerprint").GetString());
        Check.Equal(TestKeys.Client.ExportBase64(), key.GetProperty("clientKey").GetString());
        string exported = Path.Combine(fixture.Root, "exported.key");
        var export = await CaptureAsync(["keys", "export", fixture.KeyName, exported, "-progress"]);
        AssertTerminal(export, "completed");
        Check.Bytes(TestKeys.Client.Export(), File.ReadAllBytes(exported));
        var remove = await CaptureAsync(["keys", "remove", fixture.KeyName, "-progress"]);
        AssertTerminal(remove, "completed");
        Check.True(!File.Exists(SecretKeyStore.GetStoragePath(fixture.KeyName)));

        // An option-shaped password is still positional. The extra trailing flag
        // independently enables the structured protocol.
        using var plain = new Fixture(false);
        var ordinary = await CaptureTextAsync(["keys", "add", plain.KeyName, "-progress"]);
        Check.True(ordinary.Error is null);
        Check.True(!ordinary.StandardOutput.Contains("\"type\"", StringComparison.Ordinal));
        using var structured = new Fixture(false);
        var added = await CaptureAsync(["keys", "add", structured.KeyName, "-progress", "-progress"]);
        AssertTerminal(added, "completed");
        using var first = SecretKeyStore.Load(plain.KeyName);
        using var second = SecretKeyStore.Load(structured.KeyName);
        Check.Bytes(first.ClientKey.Export(), second.ClientKey.Export());
    }

    private static async Task KeyShowAsync()
    {
        using var fixture = new Fixture(false, true);
        string requestedName = fixture.KeyName.ToUpperInvariant();
        Check.True(!CommandLine.UsesProgress(["keys", "show", requestedName]));
        Check.True(!CommandLine.UsesProgress(["keys", "show", "-progress"]));
        Check.True(CommandLine.UsesProgress(["keys", "show", requestedName, "-progress"]));
        Check.True(CommandLine.UsesProgress(["keys", "show", "-progress", "-progress"]));
        Check.True(!CommandLine.UsesProgress(["keys", "show", "-progress", requestedName]));
        var shown = await CaptureAsync(["keys", "show", requestedName, "-progress"]);
        AssertTerminal(shown, "completed");
        Check.Equal(2, shown.Events.Length);
        Check.True(string.IsNullOrEmpty(shown.StandardError));
        JsonElement result = shown.Events[^1].GetProperty("result");
        Check.Equal(requestedName, result.GetProperty("name").GetString());
        Check.Equal(SecretKeyStore.Fingerprint(TestKeys.Client), result.GetProperty("fingerprint").GetString());
        Check.Equal(TestKeys.Client.ExportBase64(), result.GetProperty("clientKey").GetString());

        var missing = await CaptureAsync(["keys", "show", fixture.KeyName + "-missing", "-progress"]);
        AssertTerminal(missing, "failed");
        Check.True(missing.Error is FileNotFoundException);
        AssertReadableError(missing.Events[^1]);
        var positional = await CaptureTextAsync(["keys", "show", "-progress"]);
        Check.True(positional.Error is ArgumentException);
        Check.True(string.IsNullOrEmpty(positional.StandardOutput));
        var invalid = await CaptureAsync(["keys", "show", "-progress", "-progress"]);
        AssertTerminal(invalid, "failed");
        Check.True(invalid.Error is ArgumentException);
    }

    private static async Task ProcessFailuresAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("content", [21, 22, 23]);
        var success = await RunProcessAsync(["package", "-f", fixture.ProjectPath, "-tag", "process", "-progress"]);
        Check.Equal(0, success.ExitCode);
        var events = ParseEvents(success.StandardOutput);
        AssertTerminal(new(events, null, success.StandardError), "completed");
        string output = Path.Combine(fixture.Root, events[^1].GetProperty("result").GetProperty("outputs")[0].GetString()!);
        byte[] corrupt = File.ReadAllBytes(output);
        corrupt[^1] ^= 1;
        File.WriteAllBytes(output, corrupt);

        string missingConfig = Path.Combine(fixture.Root, "absent.yml");
        string missingKeyConfig = Path.Combine(fixture.Root, "missing-key.yml");
        File.WriteAllText(missingKeyConfig, File.ReadAllText(fixture.ProjectPath)
            .Replace("secret-key: none", "secret-key: " + fixture.KeyName, StringComparison.Ordinal));
        foreach (string[] args in new string[][]
        {
            ["package", "-f", missingConfig, "-progress"],
            ["package", "-f", missingKeyConfig, "-progress"],
            ["package", "-f", fixture.ProjectPath, "-jobs", "invalid", "-progress"],
            ["verify", fixture.Output, "-progress"]
        })
        {
            var failure = await RunProcessAsync(args);
            Check.Equal(1, failure.ExitCode);
            var failed = ParseEvents(failure.StandardOutput);
            AssertTerminal(new(failed, new Exception("Expected process failure."), failure.StandardError), "failed");
            AssertReadableError(failed[^1]);
            AssertNoStackTrace(failure.StandardError);
        }
        var plain = await RunProcessAsync(["package", "-f", missingConfig]);
        Check.Equal(1, plain.ExitCode);
        Check.True(!string.IsNullOrWhiteSpace(plain.StandardError), "Normal CLI errors must explain the failure.");
        AssertNoStackTrace(plain.StandardError);
    }

    private static bool IsProgress(JsonElement element) => element.GetProperty("type").GetString() == "progress";

    private static async Task TerminalFlushFailureAsync()
    {
        using var writer = new TerminalFlushFailureWriter();
        using var output = CreateOutput(writer);
        output.Started();
        IOException error = await ExpectIOExceptionAsync(output.CompleteAsync());
        Check.True(ReferenceEquals(writer.Failure, error), "The original output exception must be preserved.");
        Check.True(output.Token.IsCancellationRequested);
        Exception final = await output.FailAsync(error).WaitAsync(TimeSpan.FromSeconds(6));
        Check.True(ReferenceEquals(error, final));
        JsonElement[] events = ParseEvents(writer.ToString());
        Check.Equal(2, events.Length);
        Check.Equal("started", events[0].GetProperty("type").GetString());
        Check.Equal("completed", events[1].GetProperty("type").GetString());
    }

    private static async Task BlockedTerminalFlushAsync()
    {
        using var writer = new BlockingTerminalWriter();
        using var output = CreateOutput(writer);
        output.Started();
        Task completion = output.CompleteAsync();
        try
        {
            await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            IOException error = await ExpectIOExceptionAsync(completion);
            Check.True(output.Token.IsCancellationRequested);
            Exception final = await output.FailAsync(error).WaitAsync(TimeSpan.FromSeconds(6));
            Check.True(ReferenceEquals(error, final));
            Check.True(!writer.Exited.Task.IsCompleted, "The timeout must return while the output is still blocked.");
        }
        finally
        {
            writer.Release.Set();
            if (writer.Entered.Task.IsCompleted)
                await writer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        JsonElement[] events = ParseEvents(writer.ToString());
        Check.Equal(2, events.Length);
        Check.Equal("completed", events[^1].GetProperty("type").GetString());
    }

    private static async Task ProgressWriteFailureAsync()
    {
        using var writer = new ProgressWriteFailureWriter();
        using var output = CreateOutput(writer);
        output.Started();
        var progress = output.StartProgress();
        writer.FailWrites = true;
        IOException? failure = null;
        try { progress.StartStage("Hashing", 1000, 1); }
        catch (IOException error) { failure = error; }
        Check.True(ReferenceEquals(writer.Failure, failure));
        Check.True(output.Token.IsCancellationRequested);
        Exception final = await output.FailAsync(new OperationCanceledException(output.Token)).WaitAsync(TimeSpan.FromSeconds(6));
        Check.True(ReferenceEquals(writer.Failure, final), "Cancellation must not hide the underlying output error.");
        JsonElement[] events = ParseEvents(writer.ToString());
        Check.Equal(1, events.Length);
        Check.Equal("started", events[0].GetProperty("type").GetString());
    }

    private static CommandOutput CreateOutput(TextWriter writer)
    {
        TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(writer);
            return new CommandOutput("package", structured: true, default);
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task<IOException> ExpectIOExceptionAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (IOException error) { return error; }
        throw new Exception("Expected an IOException from the progress output.");
    }

    private static IEnumerable<JsonElement> Items(Invocation invocation, string kind) => invocation.Events
        .Where(item => item.GetProperty("type").GetString() == "item" && item.GetProperty("kind").GetString() == kind)
        .Select(item => item.GetProperty("data"));

    private static void AssertTerminal(Invocation invocation, string expected)
    {
        Check.True(invocation.Events.Length >= 2, "Expected a started event and a terminal event.");
        Check.Equal("started", invocation.Events[0].GetProperty("type").GetString());
        Check.Equal(1, invocation.Events[0].GetProperty("version").GetInt32());
        Check.Equal(expected, invocation.Events[^1].GetProperty("type").GetString());
        Check.Equal(1, invocation.Events.Count(item => item.GetProperty("type").GetString() is "completed" or "failed" or "canceled"));
        Check.True(expected != "completed" || invocation.Error is null, invocation.Error?.Message);
    }

    private static void AssertProgress(IEnumerable<JsonElement> progress)
    {
        long read = 0, written = 0;
        double elapsed = 0;
        foreach (var item in progress)
        {
            string stage = item.GetProperty("stage").GetString()!;
            Check.True(stage.Length > 0 && stage.All(c => char.IsAsciiLetterLower(c) || c is '-' or '_'));
            Check.True(item.GetProperty("completedBytes").GetInt64() >= 0);
            Check.True(item.GetProperty("completedFiles").GetInt64() >= 0);
            foreach (string name in new[] { "totalBytes", "totalFiles" })
            {
                JsonElement total = item.GetProperty(name);
                Check.True(total.ValueKind == JsonValueKind.Null || total.GetInt64() >= 0);
            }
            long nextRead = item.GetProperty("readBytes").GetInt64(), nextWritten = item.GetProperty("writtenBytes").GetInt64();
            double nextElapsed = item.GetProperty("elapsedSeconds").GetDouble();
            Check.True(nextRead >= read && nextWritten >= written && nextElapsed >= elapsed);
            (read, written, elapsed) = (nextRead, nextWritten, nextElapsed);
            foreach (string name in new[] { "readBytesPerSecond", "writtenBytesPerSecond", "stageElapsedSeconds" })
            {
                double value = item.GetProperty(name).GetDouble();
                Check.True(double.IsFinite(value) && value >= 0);
            }
            JsonElement fraction = item.GetProperty("stageFraction"), eta = item.GetProperty("stageEtaSeconds");
            Check.True(fraction.ValueKind == JsonValueKind.Null || fraction.GetDouble() is >= 0 and <= 1);
            Check.True(eta.ValueKind == JsonValueKind.Null || double.IsFinite(eta.GetDouble()) && eta.GetDouble() >= 0);
        }
    }

    private static void AssertReadableError(JsonElement terminal)
    {
        JsonElement error = terminal.GetProperty("error");
        Check.True(!string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()));
        string message = error.GetProperty("message").GetString()!;
        Check.True(!string.IsNullOrWhiteSpace(message));
        AssertNoStackTrace(message);
    }

    private static void AssertNoStackTrace(string text)
    {
        Check.True(!text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase));
        Check.True(!text.Contains("System.IO.FileNotFoundException", StringComparison.Ordinal));
        Check.True(!text.Contains("System.IO.InvalidDataException", StringComparison.Ordinal));
        Check.True(!text.Split('\n').Any(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal)));
    }

    private static async Task<Invocation> CaptureAsync(string[] args, CancellationToken token = default)
    {
        var capture = await CaptureTextAsync(args, token);
        return new(ParseEvents(capture.StandardOutput), capture.Error, capture.StandardError);
    }

    private static async Task<TextCapture> CaptureTextAsync(string[] args, CancellationToken token = default)
    {
        TextWriter originalOut = Console.Out, originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Exception? failure = null;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            try { Check.Equal(0, await CommandLine.RunAsync(args, token)); }
            catch (Exception exception) { failure = exception; }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        return new(output.ToString(), error.ToString(), failure);
    }

    private static JsonElement[] ParseEvents(string output)
    {
        Check.True(!output.Contains('\u001b'), "Structured output must not contain ANSI sequences.");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            Check.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static async Task<ProcessResult> RunProcessAsync(string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(CommandLine).Assembly.Location);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("Could not start the CLI process.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("CLI did not terminate and close its output streams within 15 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed record Invocation(JsonElement[] Events, Exception? Error, string StandardError);
    private sealed record TextCapture(string StandardOutput, string StandardError, Exception? Error);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TerminalFlushFailureWriter : StringWriter
    {
        internal IOException Failure { get; } = new("Simulated terminal flush failure.");
        public override void Flush()
        {
            if (ToString().Contains("\"type\":\"completed\"", StringComparison.Ordinal)) throw Failure;
            base.Flush();
        }
    }

    private sealed class BlockingTerminalWriter : StringWriter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);

        public override void Flush()
        {
            if (!ToString().Contains("\"type\":\"completed\"", StringComparison.Ordinal)) return;
            Entered.TrySetResult();
            try { Release.Wait(); }
            finally { Exited.TrySetResult(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release.Set();
                Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ProgressWriteFailureWriter : StringWriter
    {
        internal IOException Failure { get; } = new("Simulated progress write failure.");
        internal volatile bool FailWrites;
        public override void Write(ReadOnlySpan<char> value)
        {
            if (FailWrites) throw Failure;
            base.Write(value);
        }
    }
}

using Quire.Cli.Build;

internal static class BuildLockTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("build locks remain exclusive across awaits and can be reacquired", ExclusiveAsync);
        test("build locks use system temp and leave protected directories untouched", LocationAsync);
        test("equivalent directory paths share a build lock", EquivalentPathsAsync);
        test("directory links and parent links share a build lock", DirectoryLinksAsync);
    }

    private static async Task ExclusiveAsync()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Output);
        using (var first = BuildLock.Acquire(fixture.Input))
        {
            await Task.Yield();
            Check.Throws<IOException>(() => { using var duplicate = BuildLock.Acquire(fixture.Input); });
            using var independent = BuildLock.Acquire(fixture.Output);
            Check.True(first.Name != independent.Name);
        }
        using var reacquired = BuildLock.Acquire(fixture.Input);
        Check.Equal(0L, reacquired.Length);
    }

    private static Task LocationAsync()
    {
        using var fixture = new Fixture();
        string path = BuildLock.GetLockPath(fixture.Input);
        Check.Equal(Path.Combine(Path.GetTempPath(), "Quire", "locks"), Path.GetDirectoryName(path));
        string name = Path.GetFileNameWithoutExtension(path);
        Check.True(name.Length == 64 && name.All(char.IsAsciiHexDigit));
        Check.Equal(".lock", Path.GetExtension(path));
        using (var held = BuildLock.Acquire(fixture.Input))
            Check.Equal(0L, held.Length);
        Check.True(File.Exists(path), "Releasing the handle must preserve the lock file identity.");
        Check.Equal(0, Directory.GetFileSystemEntries(fixture.Input).Length);
        Check.Throws<DirectoryNotFoundException>(() => BuildLock.GetLockPath(Path.Combine(fixture.Root, "missing")));
        return Task.CompletedTask;
    }

    private static Task EquivalentPathsAsync()
    {
        using var fixture = new Fixture();
        string path = BuildLock.GetLockPath(fixture.Input);
        string[] equivalents =
        [
            fixture.Input + Path.DirectorySeparatorChar,
            Path.Combine(fixture.Input, "."),
            Path.Combine(fixture.Input, "..", "input"),
            Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.Input)
        ];
        using var held = BuildLock.Acquire(fixture.Input);
        foreach (string equivalent in equivalents)
        {
            Check.Equal(path, BuildLock.GetLockPath(equivalent));
            Check.Throws<IOException>(() => { using var duplicate = BuildLock.Acquire(equivalent); });
        }
        if (OperatingSystem.IsWindows())
        {
            Check.Equal(path, BuildLock.GetLockPath(fixture.Input.ToUpperInvariant()));
            Check.Throws<IOException>(() => { using var duplicate = BuildLock.Acquire(fixture.Input.ToUpperInvariant()); });
        }
        return Task.CompletedTask;
    }

    private static Task DirectoryLinksAsync()
    {
        using var fixture = new Fixture();
        string child = Path.Combine(fixture.Input, "child");
        Directory.CreateDirectory(child);
        string alias = Path.Combine(fixture.Root, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, fixture.Input);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Windows without Developer Mode requires elevation to create links.
            return Task.CompletedTask;
        }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
        {
            return Task.CompletedTask;
        }
        try
        {
            using var held = BuildLock.Acquire(fixture.Input);
            Check.Equal(BuildLock.GetLockPath(fixture.Input), BuildLock.GetLockPath(alias));
            Check.Throws<IOException>(() => { using var duplicate = BuildLock.Acquire(alias); });
            string aliasedChild = Path.Combine(alias, "child");
            using var childLock = BuildLock.Acquire(child);
            Check.Equal(BuildLock.GetLockPath(child), BuildLock.GetLockPath(aliasedChild));
            Check.Throws<IOException>(() => { using var duplicate = BuildLock.Acquire(aliasedChild); });
        }
        finally
        {
            Directory.Delete(alias);
        }
        return Task.CompletedTask;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Quire.Cli.Build;

internal static class BuildLock
{
    internal static FileStream Acquire(string protectedDirectory)
    {
        string path = GetLockPath(protectedDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Keep the empty file after release: deleting it lets a new handle lock a
        // different file while another process still owns the original handle.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
    }

    internal static string GetLockPath(string protectedDirectory)
    {
        string directory = Path.GetFullPath(protectedDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        // Resolve parent links as well as the final component, so aliases cannot
        // acquire independent locks for the same build state or output directory.
        directory = OperatingSystem.IsWindows() ? ResolveWindows(directory) : ResolveUnix(directory);
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (OperatingSystem.IsWindows())
            directory = directory.ToUpperInvariant();
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
        return Path.Combine(Path.GetTempPath(), "Quire", "locks", id + ".lock");
    }

    private static string ResolveWindows(string directory)
    {
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(directory, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw NativeError(directory);

        char[] buffer = new char[512];
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw NativeError(directory);
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[checked((int)length + 1)];
        }
    }

    private static string ResolveUnix(string directory)
    {
        IntPtr resolved = RealPath(directory, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw NativeError(directory);
        try
        {
            return Marshal.PtrToStringUTF8(resolved)!;
        }
        finally
        {
            Free(resolved);
        }
    }

    private static IOException NativeError(string directory) =>
        new($"Cannot resolve build lock directory '{directory}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}

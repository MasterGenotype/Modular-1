using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Modular.Core.Depots;

/// <summary>
/// The opt-in hard lock (pin layer 6): the immutable attribute on every file of a pinned build,
/// so nothing (including Steam, open or closed) can modify, replace or delete them.
/// Reading the attribute needs no privileges. Setting it goes through the root-owned
/// <c>modular-lock-helper</c> via pkexec and the <c>io.modular.build-lock</c> polkit action.
/// </summary>
public static class BuildLock
{
    public const string HelperPath = "/usr/lib/modular/modular-lock-helper";
    public const string PolicyPath = "/usr/share/polkit-1/actions/io.modular.build-lock.policy";

    public static bool HelperInstalled => File.Exists(HelperPath) && File.Exists(PolicyPath);

    /// <summary>Paths of the manifest's files that are immutable on disk right now.</summary>
    public static LockState GetState(string installPath, IEnumerable<string> relativePaths)
    {
        int locked = 0, unlocked = 0, missing = 0;
        foreach (var rel in relativePaths)
        {
            switch (IsImmutable(Path.Combine(installPath, rel)))
            {
                case true: locked++; break;
                case false: unlocked++; break;
                default: missing++; break;
            }
        }
        return new LockState(locked, unlocked, missing);
    }

    /// <returns>true/false for the attribute; null when the file can't be opened.</returns>
    public static bool? IsImmutable(string path)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        int fd = Native.open(path, Native.O_RDONLY | Native.O_NONBLOCK | Native.O_NOFOLLOW | Native.O_CLOEXEC);
        if (fd < 0)
            return null;
        try
        {
            int flags = 0;
            return Native.ioctl(fd, Native.FS_IOC_GETFLAGS, ref flags) == 0
                ? (flags & Native.FS_IMMUTABLE_FL) != 0
                : null;
        }
        finally
        {
            Native.close(fd);
        }
    }

    /// <summary>Locks or unlocks files below <paramref name="installPath"/> through the privileged helper.
    /// pkexec shows the desktop's polkit prompt (or a terminal prompt); it is never run silently.</summary>
    public static async Task<LockResult> SetAsync(
        string installPath, IEnumerable<string> relativePaths, bool locked, CancellationToken ct = default)
    {
        if (!HelperInstalled)
            throw new BuildLockUnavailableException(
                "The build-lock helper is missing. It ships with the Modular package (/usr/lib/modular); reinstall Modular, or on a source install run: sudo make install-lock-helper");

        var psi = new ProcessStartInfo("pkexec")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HelperPath);
        psi.ArgumentList.Add(locked ? "lock" : "unlock");
        psi.ArgumentList.Add(Path.GetFullPath(installPath));

        using var process = Process.Start(psi) ?? throw new BuildLockUnavailableException("pkexec did not start.");
        var payload = Encoding.UTF8.GetBytes(string.Join('\0', relativePaths) + "\0");
        await process.StandardInput.BaseStream.WriteAsync(payload, ct);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        // pkexec: 126 = authorization dismissed/denied, 127 = not authorized or no agent.
        if (process.ExitCode is 126 or 127)
            throw new BuildLockUnavailableException(
                "Administrator authorization was not given (prompt dismissed, or no polkit agent, e.g. in Game Mode).");
        if (process.ExitCode != 0)
            throw new BuildLockUnavailableException($"The lock helper failed ({process.ExitCode}): {stderr.Trim()}");

        var summary = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        int Read(string key) => summary.Split(' ')
            .Select(kv => kv.Split('='))
            .Where(kv => kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out _))
            .Select(kv => int.Parse(kv[1]))
            .FirstOrDefault();
        var skippedLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("skip ")).ToList();
        return new LockResult(Read("changed"), Read("unchanged"), Read("skipped"), skippedLines);
    }

    private static class Native
    {
        public const int O_RDONLY = 0, O_NONBLOCK = 0x800, O_NOFOLLOW = 0x20000, O_CLOEXEC = 0x80000;
        public const uint FS_IOC_GETFLAGS = 0x80086601;
        public const int FS_IMMUTABLE_FL = 0x10;

        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, uint request, ref int arg);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);
    }
}

public sealed record LockState(int Locked, int Unlocked, int Missing)
{
    public bool AnyLocked => Locked > 0;
    public bool FullyLocked => Locked > 0 && Unlocked == 0;
}

public sealed record LockResult(int Changed, int Unchanged, int Skipped, IReadOnlyList<string> SkipReasons);

public sealed class BuildLockUnavailableException(string message) : Exception(message);

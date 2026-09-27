using System.Runtime.InteropServices;

namespace Modular.Core.Deployment;

/// <summary>
/// Hardlink creation and inode identity, which .NET 8 exposes no managed API for.
/// </summary>
internal static class NativeLink
{
    private const int EXDEV = 18;   // Cross-device link
    private const int EPERM = 1;    // Operation not permitted
    private const int EACCES = 13;  // Permission denied
    private const int EMLINK = 31;  // Too many links
    private const int EOPNOTSUPP = 95;

    private const int ErrorNotSameDevice = 17;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const int ErrorInvalidFunction = 1;

    // Classic DllImport rather than LibraryImport: the source-generated variant needs
    // AllowUnsafeBlocks for the whole project, which is not worth three P/Invokes.
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(string existing, string link);

    [DllImport("libc", EntryPoint = "stat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Stat(string path, byte[] statBuffer);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string linkFileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>
    /// Creates a hardlink at <paramref name="linkPath"/> pointing at the same data as
    /// <paramref name="existingPath"/>.
    /// </summary>
    /// <param name="existingPath">Existing file.</param>
    /// <param name="linkPath">Path of the new link. Must not exist.</param>
    /// <exception cref="DeploymentMethodUnavailableException">
    /// The two paths are on different filesystems, or the filesystem or permissions do
    /// not allow the link.
    /// </exception>
    /// <exception cref="IOException">The link failed for any other reason.</exception>
    public static void CreateHardLink(string existingPath, string linkPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
                return;

            var error = Marshal.GetLastPInvokeError();

            throw error switch
            {
                ErrorNotSameDevice or ErrorAccessDenied or ErrorPrivilegeNotHeld or ErrorInvalidFunction =>
                    new DeploymentMethodUnavailableException(
                        DeploymentMethod.Hardlink,
                        $"Cannot hardlink '{linkPath}' (Win32 error {error}). Staging and the " +
                        "target must be on the same NTFS volume."),
                _ => new IOException($"CreateHardLink failed with Win32 error {error}.")
            };
        }

        if (Link(existingPath, linkPath) == 0)
            return;

        var errno = Marshal.GetLastPInvokeError();

        throw errno switch
        {
            EXDEV => new DeploymentMethodUnavailableException(
                DeploymentMethod.Hardlink,
                $"Cannot hardlink across filesystems: '{existingPath}' and '{linkPath}' " +
                "are on different devices."),
            EPERM or EACCES or EOPNOTSUPP or EMLINK => new DeploymentMethodUnavailableException(
                DeploymentMethod.Hardlink,
                $"Cannot hardlink '{linkPath}' (errno {errno}); the filesystem or its " +
                "permissions do not allow it."),
            _ => new IOException($"link() failed with errno {errno}.")
        };
    }

    /// <summary>
    /// Reads a stable device-and-inode identity for a file.
    /// </summary>
    /// <param name="path">File to inspect.</param>
    /// <returns>
    /// A <c>device:inode</c> string on Linux and macOS, or <c>null</c> when it could not
    /// be read — on Windows, or if <c>stat(2)</c> is unavailable. A null identity is not
    /// a failure: hardlink identity then rests on size and modification time, which still
    /// detects both an in-place edit and an atomic save (the latter as a changed mtime).
    /// </returns>
    public static string? TryGetInodeIdentity(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return null;

        try
        {
            // 256 bytes comfortably exceeds struct stat on every supported platform.
            var buffer = new byte[256];

            if (Stat(path, buffer) != 0)
                return null;

            // Only the two leading 64-bit words are read: st_dev then st_ino, which is
            // the layout on x86-64 and arm64 Linux and on macOS (where a 32-bit st_dev
            // is padded into the same eight bytes). A misread cannot corrupt anything —
            // it only makes purge treat the file as drifted and preserve it.
            var device = BitConverter.ToUInt64(buffer, 0);
            var inode = BitConverter.ToUInt64(buffer, 8);

            return inode == 0 ? null : $"{device}:{inode}";
        }
        catch (EntryPointNotFoundException)
        {
            // Older glibc exports __xstat rather than stat.
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }
}

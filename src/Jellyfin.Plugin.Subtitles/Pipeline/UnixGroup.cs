using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Jellyfin.Plugin.Subtitles.Pipeline;

/// <summary>
/// Reads and sets a file's group on Linux (SUB-24), which .NET has no API for: <c>statx</c> with <c>STATX_GID</c>, and
/// <c>chown</c> with the owner left as it is. The C library is found at run time (glibc, or musl on Alpine-based
/// images); where it or a function is missing, nothing is done. Never throws.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class UnixGroup
{
    // From <fcntl.h> and <linux/stat.h>; struct statx has the same layout on every architecture
    private const int AtFdCwd = -100;
    private const uint StatxGid = 0x10;
    private const int StatxSize = 256;
    private const int MaskOffset = 0;
    private const int GidOffset = 24;

    private static readonly string[] LibraryNames = ["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc.musl-armhf.so.1"];
    private static readonly Lazy<Functions> Libc = new(Load);

    /// <summary>
    /// A file's group.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="problem">Why it couldn't be read, if it couldn't.</param>
    /// <returns>The group id, or <c>null</c>.</returns>
    public static uint? GroupOf(string path, out string? problem)
    {
        var statx = Libc.Value.Statx;
        if (statx == IntPtr.Zero)
        {
            problem = "statx isn't available in this system's C library";
            return null;
        }

        var buffer = stackalloc byte[StatxSize];
        new Span<byte>(buffer, StatxSize).Clear();
        var name = NullTerminated(path);
        int result;
        fixed (byte* p = name)
        {
            result = ((delegate* unmanaged<int, byte*, int, uint, byte*, int>)statx)(AtFdCwd, p, 0, StatxGid, buffer);
        }

        if (result != 0)
        {
            problem = "statx failed (errno " + Marshal.GetLastSystemError() + ")";
            return null;
        }

        if ((*(uint*)(buffer + MaskOffset) & StatxGid) == 0)
        {
            problem = "the file system didn't report the group";
            return null;
        }

        problem = null;
        return *(uint*)(buffer + GidOffset);
    }

    /// <summary>
    /// Sets a file's group, keeping its owner. Allowed when the server's user owns the file and is a member of the group
    /// (or is privileged).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="gid">The group id.</param>
    /// <param name="problem">Why it couldn't be set (for example EPERM: the server's user isn't in that group).</param>
    /// <returns><c>true</c> if it was set.</returns>
    public static bool SetGroup(string path, uint gid, out string? problem)
    {
        var chown = Libc.Value.Chown;
        if (chown == IntPtr.Zero)
        {
            problem = "chown isn't available in this system's C library";
            return false;
        }

        var name = NullTerminated(path);
        int result;
        fixed (byte* p = name)
        {
            // An owner of (uid_t)-1 leaves it as it is
            result = ((delegate* unmanaged<byte*, uint, uint, int>)chown)(p, uint.MaxValue, gid);
        }

        problem = result == 0 ? null : "chown failed (errno " + Marshal.GetLastSystemError() + ")";
        return result == 0;
    }

    /// <summary>
    /// The server process's effective group, which new files get (for tests).
    /// </summary>
    /// <returns>The group id, or <c>null</c> if it couldn't be read.</returns>
    public static uint? EffectiveGroup()
    {
        var getegid = Libc.Value.Getegid;
        return getegid == IntPtr.Zero ? null : ((delegate* unmanaged<uint>)getegid)();
    }

    private static byte[] NullTerminated(string path)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, bytes);
        return bytes;
    }

    private static Functions Load()
    {
        foreach (var name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                return new Functions(Export(handle, "statx"), Export(handle, "chown"), Export(handle, "getegid"));
            }
        }

        return default;
    }

    private static IntPtr Export(IntPtr library, string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : IntPtr.Zero;

    // The C library stays loaded for the life of the process, so its handle is never freed
    private readonly record struct Functions(IntPtr Statx, IntPtr Chown, IntPtr Getegid);
}

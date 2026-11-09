using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZeyhDramTweaker.Core.Hardware;

/// <summary>
/// Minimal client for the PawnIO driver (https://pawnio.eu, signed, installed by the user or by
/// ZenTimings). It replaces WinRing0, which the 1.7.3 used and which Windows Defender now blocks as a
/// vulnerable driver. We only ever talk to PawnIO through its device I/O control interface and load
/// the official signed AMDFamily17 module (LGPL-2.1), which exposes read-only SMN access for us.
/// Requires administrator rights.
/// </summary>
public sealed class PawnIo : IDisposable
{
    const uint DeviceType = 0xA1B2u << 16;
    const uint IoctlLoadBinary = DeviceType | 0x2084;   // METHOD_BUFFERED, FILE_ANY_ACCESS, function 0x821
    const uint IoctlExecute = DeviceType | 0x2104;      // function 0x841
    const int FnNameLength = 32;

    readonly SafeFileHandle _handle;

    PawnIo(SafeFileHandle h) => _handle = h;

    /// <summary>Installed PawnIO version from its uninstall key, or null.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v) ? v : null;
        }
    }

    public static PawnIo OpenWithModule(string resourceName)
    {
        var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new PawnIoException(err == 5 ? "pawnio.admin" : "pawnio.missing", new Win32Exception(err));
        }
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(resourceName + " missing");
        var blob = new byte[s.Length];
        s.ReadExactly(blob);
        if (!DeviceIoControl(h, IoctlLoadBinary, blob, (uint)blob.Length, null, 0, out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new PawnIoException("pawnio.load", new Win32Exception(err));
        }
        return new PawnIo(h);
    }

    /// <summary>Runs a module function: 32-byte ASCII name followed by the 64-bit arguments.</summary>
    public ulong[] Execute(string function, ulong[] input, int outCount)
    {
        var inBuf = new byte[FnNameLength + input.Length * 8];
        var name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, inBuf, 0, Math.Min(name.Length, FnNameLength - 1));
        Buffer.BlockCopy(input, 0, inBuf, FnNameLength, input.Length * 8);
        var outBuf = new byte[outCount * 8];
        if (!DeviceIoControl(_handle, IoctlExecute, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out var ret, IntPtr.Zero))
            throw new PawnIoException("pawnio.exec", new Win32Exception(Marshal.GetLastWin32Error()));
        var result = new ulong[Math.Min(outCount, (int)(ret / 8))];
        Buffer.BlockCopy(outBuf, 0, result, 0, result.Length * 8);
        return result;
    }

    /// <summary>Like <see cref="Execute"/> but returns null instead of throwing (e.g. SMBus NACK on an empty slot).</summary>
    public ulong[]? TryExecute(string function, ulong[] input, int outCount)
    {
        try { return Execute(function, input, outCount); }
        catch (PawnIoException) { return null; }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, uint inSize, byte[]? outBuf, uint outSize, out uint returned, IntPtr overlapped);
}

/// <summary>Carries a localization key for the UI plus the Win32 cause.</summary>
public sealed class PawnIoException(string key, Exception inner) : Exception(key, inner)
{
    public string Key { get; } = key;
}

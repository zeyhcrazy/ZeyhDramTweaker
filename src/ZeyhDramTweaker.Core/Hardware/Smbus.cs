namespace ZeyhDramTweaker.Core.Hardware;

/// <summary>
/// AMD FCH SMBus (PIIX4-compatible) through the official PawnIO SmbusPIIX4 module. Transfers follow the
/// Linux i2c_smbus_xfer convention the module exposes: [addr7, read(1)/write(0), command, size, value],
/// size 0 = quick, 1 = byte, 2 = byte data, 3 = word data.
/// Holds the cross-vendor "Access_SMBUS.HTP.Method" mutex (HWiNFO, ZenTimings, RGB tools use it) while open,
/// and restores the previously selected SMBus port on dispose.
/// </summary>
public sealed class Smbus : IDisposable
{
    readonly PawnIo _io;
    readonly Mutex? _mutex;
    readonly bool _owns;
    readonly long _previousPort = -1;

    public Smbus(int timeoutMs = 3000)
    {
        const string name = @"Global\Access_SMBUS.HTP.Method";
        try { _mutex = new Mutex(false, name); }
        catch (UnauthorizedAccessException) { _mutex = Mutex.OpenExisting(name); }
        try { _owns = _mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { _owns = true; }
        if (!_owns) throw new PawnIoException("smbus.busy", new TimeoutException());
        try
        {
            _io = PawnIo.OpenWithModule("SmbusPIIX4.bin");
            _previousPort = (long)(_io.TryExecute("ioctl_piix4_port_sel", new[] { ulong.MaxValue }, 1)?.FirstOrDefault() ?? ulong.MaxValue);
        }
        catch { Release(); throw; }
    }

    public bool SelectPort(int port) => _io.TryExecute("ioctl_piix4_port_sel", new[] { (ulong)port }, 1) != null;

    public bool ReadByteData(byte addr, byte cmd, out byte value)
    {
        var r = _io.TryExecute("ioctl_smbus_xfer", new ulong[] { addr, 1, cmd, 2 }, 1);
        value = r is { Length: > 0 } ? (byte)r[0] : (byte)0;
        return r != null;
    }

    public bool ReadWordData(byte addr, byte cmd, out ushort value)
    {
        var r = _io.TryExecute("ioctl_smbus_xfer", new ulong[] { addr, 1, cmd, 3 }, 1);
        value = r is { Length: > 0 } ? (ushort)r[0] : (ushort)0;
        return r != null;
    }

    public bool WriteByteData(byte addr, byte cmd, byte value) =>
        _io.TryExecute("ioctl_smbus_xfer", new ulong[] { addr, 0, cmd, 2, value }, 0) != null;

    /// <summary>SMBus "send byte" (address + one byte), what the EE1004 page commands expect.</summary>
    public bool SendByte(byte addr, byte value) =>
        _io.TryExecute("ioctl_smbus_xfer", new ulong[] { addr, 0, value, 1 }, 0) != null;

    /// <summary>SMBus "receive byte"; on EE1004 address 0x36 it ACKs only while page 0 is selected.</summary>
    public bool ReceiveByte(byte addr) => _io.TryExecute("ioctl_smbus_xfer", new ulong[] { addr, 1, 0, 1 }, 1) != null;

    void Release()
    {
        if (_owns) try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }

    public void Dispose()
    {
        if (_previousPort >= 0) SelectPort((int)_previousPort);
        _io.Dispose();
        Release();
    }
}

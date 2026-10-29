using System.IO.Ports;

namespace Modbus.Core.Channels;

public sealed class SerialChannel : IByteChannel
{
    private readonly SerialPort _port;
    private readonly bool _removeEcho;

    private SerialChannel(SerialPort port, bool removeEcho)
    {
        _port = port;
        _removeEcho = removeEcho;
        Description = $"{port.PortName} {port.BaudRate}-{port.DataBits}-{ParityLetter(port.Parity)}-{(port.StopBits == StopBits.Two ? 2 : 1)}";
    }

    public string Description { get; }
    public bool FaultsOnTimeout => false;

    public static string[] AvailablePorts() =>
        SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    public static SerialChannel Open(ConnectionSettings s)
    {
        SerialPort port;
        try
        {
            port = new SerialPort(s.SerialPort, s.BaudRate, s.Parity, s.DataBits, s.StopBits)
            {
                Handshake = Handshake.None,
                ReadTimeout = 1000,
                WriteTimeout = 2000,
                DtrEnable = s.DtrEnable,
                RtsEnable = s.RtsEnable,
                ReadBufferSize = 8192,
            };
        }
        catch (ArgumentException ex)
        {
            throw new ModbusConnectionException($"The serial port driver did not accept the settings: {ex.Message}", 4, ex);
        }

        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new ModbusConnectionException($"{s.SerialPort} is in use by another program.", 1, ex);
        }
        catch (IOException ex)
        {
            port.Dispose();
            throw new ModbusConnectionException(
                $"{s.SerialPort} not available. Check if the selected baud rate {s.BaudRate} is supported. {ex.Message}", 1, ex);
        }
        catch (ArgumentException ex)
        {
            port.Dispose();
            throw new ModbusConnectionException($"{s.SerialPort} is not a valid serial port name.", 1, ex);
        }

        return new SerialChannel(port, s.RemoveEcho);
    }

    public void Write(byte[] data)
    {
        try
        {
            _port.Write(data, 0, data.Length);
            if (_removeEcho) DropEcho(data.Length);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            throw new IOException($"Write error: {ex.Message}", ex);
        }
    }

    private void DropEcho(int count)
    {
        // Converters without echo suppression return the sent bytes first. Allow ~20 bit times per byte + 100 ms.
        var ms = 100 + count * 20_000.0 / _port.BaudRate;
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        var got = 0;
        var tmp = new byte[count];
        try
        {
            while (got < count)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                got += Read(tmp, got, count - got, remaining);
            }
        }
        catch (TimeoutException)
        {
            // Fewer echoed bytes than expected; continue with the response.
        }
    }

    public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        try
        {
            _port.ReadTimeout = timeout.TotalMilliseconds >= int.MaxValue
                ? SerialPort.InfiniteTimeout
                : Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
            return _port.Read(buffer, offset, count);
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException("The serial port is closed. Disconnected USB/serial converter?", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Read error reported by the serial driver: {ex.Message}", ex);
        }
    }

    public void DiscardInput()
    {
        try { _port.DiscardInBuffer(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* reported by next read */ }
    }

    public void Dispose() => _port.Dispose();

    private static char ParityLetter(Parity p) => p switch
    {
        Parity.Even => 'E',
        Parity.Odd => 'O',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => 'N',
    };
}

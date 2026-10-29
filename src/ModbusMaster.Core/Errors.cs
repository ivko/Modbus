namespace ModbusMaster.Core;

/// <summary>Result codes shared by the GUI status line, the CLI and the automation interface.</summary>
/// <remarks>Numbers follow the ReadResult/WriteResult table of the automation interface.</remarks>
public enum ModbusResult
{
    Success = 0,
    Timeout = 1,
    CrcError = 2,
    ResponseError = 3,
    WriteError = 4,
    ReadError = 5,
    PortNotOpen = 6,
    DataUninitialized = 10,
    InsufficientBytes = 11,
    ByteCountError = 16,
    TransactionIdError = 19,
    IllegalFunction = 0x81,
    IllegalDataAddress = 0x82,
    IllegalDataValue = 0x83,
    ServerDeviceFailure = 0x84,
    Acknowledge = 0x85,
    ServerDeviceBusy = 0x86,
    NegativeAcknowledge = 0x87,
    GatewayPathUnavailable = 0x8A,
    GatewayTargetFailed = 0x8B,
}

/// <summary>Base class for transport, framing and timeout failures.</summary>
public class ModbusCommunicationException : Exception
{
    public ModbusCommunicationException(string message, ModbusResult result) : base(message) => Result = result;
    public ModbusCommunicationException(string message, ModbusResult result, Exception inner) : base(message, inner) => Result = result;

    public ModbusResult Result { get; }
}

/// <summary>No complete response arrived within the response timeout.</summary>
public sealed class ModbusTimeoutException : ModbusCommunicationException
{
    public ModbusTimeoutException(string message, ModbusResult result = ModbusResult.Timeout) : base(message, result) { }
}

/// <summary>A response arrived but was malformed (CRC/LRC, length, unit, function).</summary>
public sealed class ModbusFrameException : ModbusCommunicationException
{
    public ModbusFrameException(string message, ModbusResult result = ModbusResult.ResponseError) : base(message, result) { }
}

/// <summary>The connection could not be opened or was lost.</summary>
public sealed class ModbusConnectionException : ModbusCommunicationException
{
    public ModbusConnectionException(string message, int openError = 0) : base(message, ModbusResult.PortNotOpen) => OpenError = openError;
    public ModbusConnectionException(string message, int openError, Exception inner) : base(message, ModbusResult.PortNotOpen, inner) => OpenError = openError;

    /// <summary>OpenConnection error number (1 = serial port not available, 13 = connect error, 14 = timeout ...).</summary>
    public int OpenError { get; }
}

/// <summary>The slave answered with a Modbus exception response.</summary>
public sealed class ModbusSlaveException : Exception
{
    public ModbusSlaveException(byte functionCode, byte exceptionCode)
        : base($"Exception {exceptionCode:D2}: {Describe(exceptionCode)}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }
    public ModbusResult Result => (ModbusResult)(0x80 | ExceptionCode);

    public static string Describe(byte code) => code switch
    {
        1 => "Illegal Function",
        2 => "Illegal Data Address",
        3 => "Illegal Data Value",
        4 => "Server Device Failure",
        5 => "Acknowledge",
        6 => "Server Device Busy",
        7 => "Negative Acknowledge",
        8 => "Memory Parity Error",
        10 => "Gateway Path Unavailable",
        11 => "Gateway Target Device Failed To Respond",
        _ => "Unknown exception",
    };
}

public static class ModbusResults
{
    public static ModbusResult FromException(Exception ex) => ex switch
    {
        ModbusSlaveException s => s.Result,
        ModbusCommunicationException c => c.Result,
        _ => ModbusResult.ReadError,
    };

    public static string Describe(ModbusResult result) => result switch
    {
        ModbusResult.Success => "OK",
        ModbusResult.Timeout => "Timeout error",
        ModbusResult.CrcError => "CRC/LRC error",
        ModbusResult.ResponseError => "Response error",
        ModbusResult.WriteError => "Write error",
        ModbusResult.ReadError => "Read error",
        ModbusResult.PortNotOpen => "No connection",
        ModbusResult.DataUninitialized => "No data yet",
        ModbusResult.InsufficientBytes => "Insufficient bytes received",
        ModbusResult.ByteCountError => "Byte count error",
        ModbusResult.TransactionIdError => "Transaction ID error",
        _ when ((int)result & 0x80) != 0 => $"Exception {(int)result & 0x7F:D2}: {ModbusSlaveException.Describe((byte)((int)result & 0x7F))}",
        _ => result.ToString(),
    };
}

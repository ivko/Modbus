namespace ModbusMaster.Core.Protocol;

public enum FunctionCode : byte
{
    ReadCoils = 1,
    ReadDiscreteInputs = 2,
    ReadHoldingRegisters = 3,
    ReadInputRegisters = 4,
    WriteSingleCoil = 5,
    WriteSingleRegister = 6,
    Diagnostics = 8,
    WriteMultipleCoils = 15,
    WriteMultipleRegisters = 16,
    ReportServerId = 17,
    MaskWriteRegister = 22,
    ReadWriteMultipleRegisters = 23,
}

public static class FunctionCodes
{
    public static bool IsBitFunction(FunctionCode f) =>
        f is FunctionCode.ReadCoils or FunctionCode.ReadDiscreteInputs
            or FunctionCode.WriteSingleCoil or FunctionCode.WriteMultipleCoils;

    public static bool IsRead(FunctionCode f) =>
        f is FunctionCode.ReadCoils or FunctionCode.ReadDiscreteInputs
            or FunctionCode.ReadHoldingRegisters or FunctionCode.ReadInputRegisters;

    public static string Describe(FunctionCode f) => f switch
    {
        FunctionCode.ReadCoils => "01 Read Coils (0x)",
        FunctionCode.ReadDiscreteInputs => "02 Read Discrete Inputs (1x)",
        FunctionCode.ReadHoldingRegisters => "03 Read Holding Registers (4x)",
        FunctionCode.ReadInputRegisters => "04 Read Input Registers (3x)",
        FunctionCode.WriteSingleCoil => "05 Write Single Coil",
        FunctionCode.WriteSingleRegister => "06 Write Single Register",
        FunctionCode.Diagnostics => "08 Diagnostics",
        FunctionCode.WriteMultipleCoils => "15 Write Multiple Coils",
        FunctionCode.WriteMultipleRegisters => "16 Write Multiple Registers",
        FunctionCode.ReportServerId => "17 Report Server ID",
        FunctionCode.MaskWriteRegister => "22 Mask Write Register",
        FunctionCode.ReadWriteMultipleRegisters => "23 Read/Write Multiple Registers",
        _ => $"{(byte)f:D2}",
    };

    /// <summary>Conventional PLC address prefix: 0xxxx coils, 1xxxx inputs, 3xxxx input registers, 4xxxx holding registers.</summary>
    public static int PlcBase(FunctionCode f) => f switch
    {
        FunctionCode.ReadCoils or FunctionCode.WriteSingleCoil or FunctionCode.WriteMultipleCoils => 1,
        FunctionCode.ReadDiscreteInputs => 10001,
        FunctionCode.ReadInputRegisters => 30001,
        _ => 40001,
    };
}

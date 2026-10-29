# Use the Modbus engine directly from PowerShell 7.5+ (loads .NET 10 libraries).
# Start a test slave first if you have no device:  mbm serve --port 1502 --simulate
Add-Type -Path (Join-Path $PSScriptRoot '..\..\ModbusMaster.Core.dll')

$s = [ModbusMaster.Core.ConnectionSettings]::new()
$s.Kind = [ModbusMaster.Core.ConnectionKind]::Tcp
$s.Host = '127.0.0.1'
$s.Port = 1502

$client = [ModbusMaster.Core.ModbusConnection]::Create($s)
try {
    $client.Open()
    $client.WriteMultipleRegisters(1, 0, [uint16[]](100, 200, 300))
    $values = $client.ReadHoldingRegisters(1, 0, 3)
    "Holding registers 0-2: $($values -join ', ')"

    # 32-bit float in registers 10-11, little-endian byte swap (CDAB)
    $order = [ModbusMaster.Core.Data.ByteOrder]::LittleEndianByteSwap
    $regs = [ModbusMaster.Core.Data.RegisterCodec]::Encode(12.75, [ModbusMaster.Core.Data.ValueKind]::Float, $order)
    $client.WriteMultipleRegisters(1, 10, $regs)
    $back = $client.ReadHoldingRegisters(1, 10, 2)
    "Float: " + [ModbusMaster.Core.Data.RegisterCodec]::Decode($back, [ModbusMaster.Core.Data.ValueKind]::Float, $order)
}
catch [ModbusMaster.Core.ModbusSlaveException] { "Slave exception: $($_.Exception.Message)" }
catch [ModbusMaster.Core.ModbusCommunicationException] { "Communication error: $($_.Exception.Message)" }
finally { $client.Dispose() }

# Use the Modbus engine directly from PowerShell 7.5+ (loads .NET 10 libraries).
# Start a test slave first if you have no device:  mbm serve --port 1502 --simulate
Add-Type -Path (Join-Path $PSScriptRoot '..\..\Modbus.Core.dll')

$s = [Modbus.Core.ConnectionSettings]::new()
$s.Kind = [Modbus.Core.ConnectionKind]::Tcp
$s.Host = '127.0.0.1'
$s.Port = 1502

$client = [Modbus.Core.ModbusConnection]::Create($s)
try {
    $client.Open()
    $client.WriteMultipleRegisters(1, 0, [uint16[]](100, 200, 300))
    $values = $client.ReadHoldingRegisters(1, 0, 3)
    "Holding registers 0-2: $($values -join ', ')"

    # 32-bit float in registers 10-11, little-endian byte swap (CDAB)
    $order = [Modbus.Core.Data.ByteOrder]::LittleEndianByteSwap
    $regs = [Modbus.Core.Data.RegisterCodec]::Encode(12.75, [Modbus.Core.Data.ValueKind]::Float, $order)
    $client.WriteMultipleRegisters(1, 10, $regs)
    $back = $client.ReadHoldingRegisters(1, 10, 2)
    "Float: " + [Modbus.Core.Data.RegisterCodec]::Decode($back, [Modbus.Core.Data.ValueKind]::Float, $order)
}
catch [Modbus.Core.ModbusSlaveException] { "Slave exception: $($_.Exception.Message)" }
catch [Modbus.Core.ModbusCommunicationException] { "Communication error: $($_.Exception.Message)" }
finally { $client.Dispose() }

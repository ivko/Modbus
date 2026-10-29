using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Core.Polling;

public sealed record SlaveScanResult(byte SlaveId, ModbusResult Result, string Detail)
{
    /// <summary>A slave exists when it answered, even with an exception.</summary>
    public bool Found => Result == ModbusResult.Success || ((int)Result & 0x80) != 0;
}

public sealed record AddressScanResult(int Address, ushort? Value, ModbusResult Result);

public static class Scanners
{
    /// <summary>Slave Scan: sends one read to every slave ID in the range and reports who answers.</summary>
    public static IEnumerable<SlaveScanResult> ScanSlaves(ModbusClient client, int firstId, int lastId,
        FunctionCode function, ushort address, int quantity, CancellationToken cancel = default)
    {
        for (var id = firstId; id <= lastId; id++)
        {
            cancel.ThrowIfCancellationRequested();
            SlaveScanResult result;
            try
            {
                var values = client.Read((byte)id, function, address, quantity);
                result = new SlaveScanResult((byte)id, ModbusResult.Success, string.Join(" ", values));
            }
            catch (ModbusSlaveException ex)
            {
                result = new SlaveScanResult((byte)id, ex.Result, ex.Message);
            }
            catch (ModbusConnectionException)
            {
                throw;
            }
            catch (ModbusCommunicationException ex)
            {
                result = new SlaveScanResult((byte)id, ex.Result, ex.Message);
            }
            yield return result;
        }
    }

    /// <summary>
    /// Address Scan: reads a range in blocks and, where a block fails with an exception,
    /// retries address by address to find which addresses exist.
    /// </summary>
    public static IEnumerable<AddressScanResult> ScanAddresses(ModbusClient client, byte slaveId, FunctionCode function,
        int firstAddress, int lastAddress, int blockSize = 100, CancellationToken cancel = default)
    {
        blockSize = Math.Clamp(blockSize, 1, Pdu.MaxQuantity(function));
        for (var start = firstAddress; start <= lastAddress; start += blockSize)
        {
            cancel.ThrowIfCancellationRequested();
            var count = Math.Min(blockSize, lastAddress - start + 1);
            ushort[]? block = null;
            ModbusResult blockResult;
            try
            {
                block = client.Read(slaveId, function, (ushort)start, count);
                blockResult = ModbusResult.Success;
            }
            catch (ModbusSlaveException ex)
            {
                blockResult = ex.Result;
            }

            if (block is not null)
            {
                for (var i = 0; i < count; i++) yield return new AddressScanResult(start + i, block[i], ModbusResult.Success);
                continue;
            }

            if (count == 1)
            {
                yield return new AddressScanResult(start, null, blockResult);
                continue;
            }

            for (var a = start; a < start + count; a++)
            {
                cancel.ThrowIfCancellationRequested();
                AddressScanResult single;
                try
                {
                    var v = client.Read(slaveId, function, (ushort)a, 1);
                    single = new AddressScanResult(a, v[0], ModbusResult.Success);
                }
                catch (ModbusSlaveException ex)
                {
                    single = new AddressScanResult(a, null, ex.Result);
                }
                yield return single;
            }
        }
    }
}

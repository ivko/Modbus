using System.Text;
using Modbus.Core.Protocol;

namespace Modbus.Core;

public enum TrafficDirection { Tx, Rx }

/// <summary>One raw frame sent or received on the wire.</summary>
public sealed record TrafficEntry(DateTime Timestamp, TrafficDirection Direction, byte[] Data, bool IsAsciiFrame = false, string? Note = null)
{
    public string DataText => IsAsciiFrame
        ? Encoding.ASCII.GetString(Data).TrimEnd('\r', '\n')
        : Hex.Format(Data);

    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} {(Direction == TrafficDirection.Tx ? "Tx" : "Rx")}: {DataText}{(Note is null ? "" : "  ; " + Note)}";
}

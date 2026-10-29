namespace Modbus.Cli;

internal static class Help
{
    public const string Text = """
        mbm - Modbus Master command line

        USAGE
          mbm <command> <connection> [options]

        COMMANDS
          read            Read coils, inputs or registers (functions 01-04)
          write           Write coils or registers (functions 05, 06, 15, 16)
          mask            Mask write register (function 22)          --address --and --or
          rw              Read/write multiple registers (function 23) --read-address --read-count --write-address --values
          id              Report server ID (function 17)
          diag            Diagnostics (function 08)                   --sub --data
          devid           Read device identification (43/14)       --code 1|2|3|4 --object N
          events          Get comm event counter (function 11)
          raw             Send any PDU and print the response PDU    --pdu "03 00 00 00 0A"
          scan            Slave scan: find responding IDs            --units 1-247
          scan-addresses  Address scan: find readable addresses      --addresses 0-9999
          ports           List serial ports
          serve           Run a Modbus slave simulator for tests     --port 502 --framing tcp|rtu|ascii --simulate
          version         Show the version

        CONNECTION (choose one)
          --tcp HOST[:PORT]        Modbus TCP (default port 502)
          --udp HOST[:PORT]        Modbus UDP
          --rtu-tcp HOST[:PORT]    RTU frames over TCP (serial gateways)
          --rtu-udp HOST[:PORT]    RTU frames over UDP
          --ascii-tcp HOST[:PORT]  ASCII frames over TCP
          --ascii-udp HOST[:PORT]  ASCII frames over UDP
          --tls HOST[:PORT]        Modbus/TCP Security (default port 802)
                                   --cert client.pfx --cert-password P --ca ca.pem --server-name N --insecure
          --serial COMn            Serial port: --baud 9600 --parity E|N|O --data-bits 8 --stop-bits 1
                                   --mode rtu|ascii --remove-echo
          --connection FILE        Use the connection saved in a workspace (*.mmw) or connection (*.json) file

        COMMON OPTIONS
          --unit N           Slave ID / unit ID (default 1). 0 = broadcast (writes only)
          --timeout MS       Response timeout (default 1000, scan 200)
          --connect-timeout  TCP connect timeout (default 1000)
          --delay MS         Delay between polls (default 20)
          --log              Print every frame sent and received to stderr
          --json             One JSON object per result on stdout
          --ipv6             Prefer IPv6 when resolving host names

        READ OPTIONS
          --fc 1|2|3|4  or  --table coils|inputs|holding|input-registers   (default 3, holding)
          --address N        First address, 0-based (default 0). Hex works: 0x10
          --count N          Number of registers or bits
          --format F         int16 uint16 hex bin int32 uint32 int64 uint64 float double
          --byte-order O     abcd (big-endian, default)  dcba (little-endian)
                             badc (big-endian byte swap) cdab (little-endian byte swap)
          --watch            Repeat every --interval MS (default 1000) until Ctrl+C
          --csv FILE         Append each result as a row to a CSV file
          --plain            Values only, space separated

        WRITE OPTIONS
          --address N --values 1,2,3   Values in --format/--byte-order. 32/64-bit values use 2/4 registers.
          --coils                      Write coils (values 1/0, on/off, true/false)
          --fc 5|6|15|16               Force a function. Default: single write for one value, multiple otherwise.

        EXIT CODES
          0 success   1 Modbus exception from the slave   2 communication error   3 usage error

        EXAMPLES
          mbm read --tcp 192.168.1.10 --unit 1 --address 0 --count 10
          mbm read --serial COM3 --baud 19200 --parity N --unit 5 --fc 4 --address 100 --count 2 --format float --byte-order cdab
          mbm write --tcp 192.168.1.10 --address 40 --values 1500
          mbm write --rtu-tcp 10.0.0.7:4001 --unit 3 --coils --address 0 --values 1,0,1
          mbm read --tcp plc --count 4 --json --watch --interval 500
          mbm scan --serial COM3 --baud 9600 --units 1-32
          mbm serve --port 1502 --simulate
        """;
}

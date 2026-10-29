# Modbus Master and Modbus Slave

A Modbus master and a Modbus slave simulator for Windows, with a command line tool and COM automation.
Both cover Modbus TCP, UDP, RTU, ASCII, RTU/ASCII over TCP/UDP and Modbus/TCP Security (TLS).

| Part | File | Use |
| --- | --- | --- |
| Master program | `ModbusMaster.exe` | Data windows, charts, logging, scans, Test Center |
| Slave simulator | `ModbusSlave.exe` | Simulated devices with value and error simulation (delay, missing answers, CRC errors, exceptions) |
| Command line | `mbm.exe` | Batch files, PowerShell, Python `subprocess`, CI |
| Automation | COM `ModbusMaster.*` and `ModbusSlave.*` | Excel VBA, VBScript, Python pywin32. Same members as the Modbus Poll and Modbus Slave automation interfaces |
| Library | `Modbus.Core.dll` | C#, PowerShell 7.5+ |

The manuals are `docs/manual/index.html` (master) and `docs/manual/slave.html` (slave), also opened with F1.

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet build -c Release
dotnet test
```

Publish everything into one folder (framework dependent, needs the .NET 10 Desktop Runtime):

```
dotnet publish src/Modbus.App -c Release -o E:\tools\ModbusMaster
dotnet publish src/Modbus.Slave -c Release -o E:\tools\ModbusMaster
dotnet publish src/Modbus.Cli -c Release -o E:\tools\ModbusMaster
```

Register automation for the current user (no administrator rights needed):

```
E:\tools\ModbusMaster\ModbusMaster.exe /regserver
E:\tools\ModbusMaster\ModbusSlave.exe /regserver
```

Add `/compat` to also register `Mbpoll.*` (master) or `Mbslave.*` (slave), so scripts written for
Modbus Poll or Modbus Slave run unchanged. Do not do that while those programs are installed and in use. Run it again after moving the program folder.

## Layout

```
src/Modbus.Core   protocol, transports, framing, poll scheduler, scanners, slave engine and servers
src/Modbus.Ui     shared WPF parts: data grid, chart, traffic window, dialogs, COM server plumbing
src/Modbus.App    master program and its automation
src/Modbus.Slave  slave program and its automation
src/Modbus.Cli    mbm command line
tests/            unit and end-to-end tests (simulator over TCP, RTU, ASCII and TLS)
docs/manual       user manual
examples          Excel VBA, Python, VBScript, PowerShell, Test Center strings
```

NuGet packages are restored to `E:\tools\nuget\packages` (see `nuget.config`).

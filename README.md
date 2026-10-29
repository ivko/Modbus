# Modbus Master

A Modbus master for Windows with a graphical program, a command line tool and a COM automation
interface. It covers Modbus TCP, UDP, RTU, ASCII, RTU/ASCII over TCP/UDP and Modbus/TCP Security (TLS).

| Part | File | Use |
| --- | --- | --- |
| Windows program | `ModbusMaster.exe` | Data windows, charts, logging, scans, Test Center |
| Command line | `mbm.exe` | Batch files, PowerShell, Python `subprocess`, CI |
| Automation | COM `ModbusMaster.Application` / `ModbusMaster.Document` | Excel VBA, VBScript, Python pywin32. Same members as the Modbus Poll automation interface |
| Library | `ModbusMaster.Core.dll` | C#, PowerShell 7.5+ |

The user manual is `docs/manual/index.html` (also opened with F1 in the program).

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet build -c Release
dotnet test
```

Publish everything into one folder (framework dependent, needs the .NET 10 Desktop Runtime):

```
dotnet publish src/ModbusMaster.App -c Release -o E:\tools\ModbusMaster
dotnet publish src/ModbusMaster.Cli -c Release -o E:\tools\ModbusMaster
```

Register automation for the current user (no administrator rights needed):

```
E:\tools\ModbusMaster\ModbusMaster.exe /regserver
```

Add `/compat` to also register the names `Mbpoll.Application` and `Mbpoll.Document`, so scripts written
for Modbus Poll run unchanged. Run it again after moving the program folder.

## Layout

```
src/ModbusMaster.Core   protocol, transports (serial, TCP, UDP, TLS), framing, poll scheduler, scanners, simulator
src/ModbusMaster.App    WPF program and the COM automation server
src/ModbusMaster.Cli    mbm command line
tests/                  unit and end-to-end tests (simulator over TCP, RTU, ASCII and TLS)
docs/manual             user manual
examples                Excel VBA, Python, VBScript, PowerShell, Test Center strings
```

NuGet packages are restored to `E:\tools\nuget\packages` (see `nuget.config`).

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

The user manuals are in `docs/manual`: `index.html` for the master and `slave.html` for the slave.
Both programs open them with F1.

## Build

Requires the .NET 10 SDK on Windows. Run the commands from the repository root.

```
dotnet build -c Release
dotnet test
```

## Install

Publish the three programs into one folder. `publish` is used here; it is ignored by git.

```
dotnet publish src/Modbus.App -c Release -o publish
dotnet publish src/Modbus.Slave -c Release -o publish
dotnet publish src/Modbus.Cli -c Release -o publish
```

The folder is self-contained apart from the .NET 10 Desktop Runtime. Copy it wherever the programs
should live; the manuals and examples are inside it.

Register automation for the current user from the final location (no administrator rights needed):

```
ModbusMaster.exe /regserver
ModbusSlave.exe /regserver
```

Registration stores the full path of each program, so register again after moving the folder.
`/unregserver` removes it. Add `/compat` to also register `Mbpoll.*` (master) or `Mbslave.*` (slave),
so scripts written for Modbus Poll or Modbus Slave run unchanged. Do not do that while those programs
are installed and in use.

## Layout

```
src/Modbus.Core   protocol, transports, framing, poll scheduler, scanners, slave engine and servers
src/Modbus.Ui     shared WPF parts: data grid, chart, traffic window, dialogs, COM server plumbing
src/Modbus.App    master program and its automation
src/Modbus.Slave  slave program and its automation
src/Modbus.Cli    mbm command line
tests/            unit and end-to-end tests (simulator over TCP, RTU, ASCII and TLS)
docs/manual       user manuals
examples          Excel VBA, Python, VBScript, PowerShell, Test Center strings
```

## NuGet cache

`nuget.config` keeps downloaded NuGet packages in `packages`, next to it in the repository folder.
The folder is ignored by git and is filled by the first build.

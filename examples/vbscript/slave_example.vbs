' Modbus Slave automation from VBScript. Run: cscript slave_example.vbs
' Builds a small simulated device, starts it on port 1502 and turns on error simulation after 20 s.
' Read it with:  mbm read --tcp 127.0.0.1:1502 --count 6 --format float

Set app = CreateObject("ModbusSlave.Application")
app.Connection = 1          ' Modbus TCP/IP
app.ServerPort = 1502
app.AnyAddress = 1

Set regs = CreateObject("ModbusSlave.Document")
regs.SetupHoldingRegisters 1, 0, 20
regs.ByteOrder = 0
regs.SetFormat 0, 5         ' registers 0-1: float big-endian
regs.SetName 0, "Temperature"
regs.Floats(0) = 21.5
regs.SetName 2, "Status"
regs.SRegisters(2) = 1
regs.SetName 3, "Counter"
regs.AutoIncrement(3) = True

Set coils = CreateObject("ModbusSlave.Document")
coils.SetupCoils 1, 0, 8
coils.Coils(0) = 1

result = app.OpenConnection()
If result <> 0 And result <> 255 Then
    WScript.Echo "Could not start the slave, error " & result
    WScript.Quit 2
End If
regs.ShowWindow
WScript.Echo "Slave running on port 1502."

WScript.Sleep 20000
regs.ResponseDelay = 300
regs.NoResponsePercent = 20
regs.ErrorSimulation = True
WScript.Echo "Error simulation on: 300 ms delay, 20 % missing answers. Press Ctrl+C to stop the script."
WScript.Sleep 600000

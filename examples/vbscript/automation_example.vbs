' Modbus Master automation from VBScript. Run: cscript automation_example.vbs
' Start a test slave first if you have no device:  mbm serve --port 1502 --simulate

Set app = CreateObject("ModbusMaster.Application")
app.Connection = 1
app.IPAddress = "127.0.0.1"
app.ServerPort = 1502

Set doc = CreateObject("ModbusMaster.Document")
doc.ReadInputRegisters 1, 0, 4, 500

result = app.OpenConnection()
If result <> 0 And result <> 255 Then
    WScript.Echo "Connection failed, error " & result
    WScript.Quit 2
End If

WScript.Sleep 1500
If doc.ReadResult() = 0 Then
    For n = 0 To 3
        WScript.Echo "Input register " & n & " = " & doc.URegisters(n)
    Next
Else
    WScript.Echo "Read failed, result " & doc.ReadResult()
End If

' Write 1234 to holding register 10 with function 06.
doc.SRegisters(0) = 1234
doc.WriteSingleRegister 1, 10
WScript.Sleep 300
WScript.Echo "Write result " & doc.WriteResult()

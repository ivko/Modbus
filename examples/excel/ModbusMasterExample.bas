Attribute VB_Name = "ModbusMasterExample"
' Modbus Master - Excel VBA example
'
' 1. In Excel: Developer > Visual Basic > File > Import File... and pick this file.
' 2. Put the connection settings in Sheet1: B2 = IP address, B3 = port, B4 = slave ID.
' 3. Run StartModbus, then ReadValues (for example from buttons on the sheet).
'
' Scripts written for Modbus Poll work the same way. Replace "Mbpoll." with "ModbusMaster."
' in CreateObject, or register the compatible names (Tools > Register Automation).

Option Explicit

Public app As Object
Public regs As Object
Public coils As Object

Public Sub StartModbus()
    Set app = CreateObject("ModbusMaster.Application")
    Set regs = CreateObject("ModbusMaster.Document")
    Set coils = CreateObject("ModbusMaster.Document")

    Dim slave As Integer
    slave = Sheet1.Range("B4").Value

    regs.ReadHoldingRegisters slave, 0, 10, 1000   ' 10 holding registers every 1000 ms
    coils.ReadCoils slave, 0, 10, 1000              ' 10 coils every 1000 ms
    ' regs.ShowWindow                               ' uncomment to see Modbus Master

    app.Connection = 1                              ' Modbus TCP/IP
    app.IPAddress = Sheet1.Range("B2").Value
    app.ServerPort = Sheet1.Range("B3").Value
    app.ConnectTimeout = 1000
    app.ResponseTimeout = 1000

    Dim result As Integer
    result = app.OpenConnection()
    Sheet1.Range("B6").Value = result
    If result <> 0 And result <> 255 Then MsgBox "Connection failed, error " & result
End Sub

Public Sub ReadValues()
    Dim n As Integer
    Sheet1.Range("E2").Value = regs.ReadResult()     ' 0 = OK
    Sheet1.Range("E3").Value = coils.ReadResult()
    For n = 0 To 9
        Sheet1.Cells(10 + n, 1).Value = n
        Sheet1.Cells(10 + n, 2).Value = regs.SRegisters(n)
        Sheet1.Cells(10 + n, 3).Value = coils.Coils(n)
    Next n
End Sub

Public Sub WriteValues()
    ' Fill the internal write array from column D and write it with function 16.
    Dim n As Integer
    For n = 0 To 9
        regs.SRegisters(n) = Sheet1.Cells(10 + n, 4).Value
    Next n
    regs.WriteMultipleRegisters Sheet1.Range("B4").Value, 0, 10
End Sub

Public Sub WriteFloat()
    ' Writes the float in D21 to holding registers 100-101 (big-endian).
    regs.ByteOrder = 0
    regs.Floats(0) = Sheet1.Range("D21").Value
    regs.WriteMultipleRegisters Sheet1.Range("B4").Value, 100, 2
End Sub

Public Sub StopModbus()
    If Not app Is Nothing Then app.CloseConnection
    Set regs = Nothing
    Set coils = Nothing
    Set app = Nothing
End Sub

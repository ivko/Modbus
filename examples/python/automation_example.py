"""Modbus Master automation from Python (pip install pywin32).

Register automation first: Tools > Register Automation, or "ModbusMaster.exe /regserver".
Start a test slave if you have no device:  mbm serve --port 1502 --simulate
"""
import time

import win32com.client as win32

SIGNED, UNSIGNED, HEX, BINARY = 0, 1, 2, 3
FLOAT_BE, FLOAT_LE_BS = 5, 4

app = win32.Dispatch("ModbusMaster.Application")
app.Connection = 1            # Modbus TCP/IP
app.IPAddress = "127.0.0.1"
app.ServerPort = 1502
app.ResponseTimeout = 1000    # ms
app.DelayBetweenPolls = 20    # ms
result = app.OpenConnection()
if result not in (0, 255):
    raise SystemExit(f"Connection failed, error {result}")

# A data window that reads 10 holding registers from slave 1, address 0, every 500 ms.
win = win32.Dispatch("ModbusMaster.Document")
win.ReadHoldingRegisters(1, 0, 10, 500)
win.ShowWindow()
win.EnableRefresh = False
win.SetName(0, "Setpoint")
win.SetFormat(0, SIGNED)
win.SetName(2, "Temperature")
win.SetFormat(2, FLOAT_BE)    # registers 2-3 as a big-endian float
win.SetFormat(4, HEX)
win.EnableRefresh = True

# Write through the internal array (no extra window): registers 0-4.
for i, value in enumerate([1, 10, 100, 1000, 10000]):
    win.SRegisters(i, value)
win.WriteMultipleRegisters(1, 0, 5)
time.sleep(0.5)
print("write result:", win.WriteResult())

time.sleep(1.0)
if win.ReadResult() == 0:
    print("register 0 =", win.SRegisters(0))
    print("tx/rx =", win.GetTxCount(), win.GetRxCount())
else:
    print("read failed, result", win.ReadResult())

# Conditional colors: red when register 1 > 50.
win.ColorsSetColors(2, 255, 180, 180)   # rule 1 background
win.ColorsSetRules(2, 0)                # rule 1: greater than
win.ColorsSetCompares(50, 0)
win.ColorsUpdateCell(1)

input("Press ENTER to quit: ")

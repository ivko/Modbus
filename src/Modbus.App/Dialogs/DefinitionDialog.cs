using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;

namespace Modbus.App.Dialogs;

/// <summary>Read/Write Definition (F8).</summary>
public static class DefinitionDialog
{
    private static readonly FunctionCode[] WindowFunctions =
    [
        FunctionCode.ReadCoils, FunctionCode.ReadDiscreteInputs, FunctionCode.ReadHoldingRegisters, FunctionCode.ReadInputRegisters,
        FunctionCode.WriteSingleCoil, FunctionCode.WriteSingleRegister, FunctionCode.WriteMultipleCoils, FunctionCode.WriteMultipleRegisters,
        FunctionCode.ReadWriteMultipleRegisters,
    ];

    public static (PollDefinition Definition, string Title)? Show(PollDefinition current, string title)
    {
        var d = current.Clone();
        var f = new FormDialog("Read/Write Definition", "definition");
        var name = f.AddText("Window name", title);
        f.AddInfo("Leave the name empty to name the tab after its definition, e.g. \"ID 1 · 03 Holding 0–9\".");
        var slave = f.AddText("Slave ID", d.SlaveId.ToString(), 90);
        f.AddInfo("Serial: slave address 1-247 (0 = broadcast, writes only). TCP: unit ID; many devices ignore it.");
        var function = f.AddCombo("Function", WindowFunctions.Select(fc => new Choice<FunctionCode>(fc, FunctionCodes.Describe(fc))), d.Function);
        var address = f.AddText("Address", d.Address.ToString(), 90);
        var plc = f.AddCheck("PLC addresses (base 1: 40001...)", d.PlcAddresses);
        f.AddInfo("Protocol addresses count from 0. PLC address 40001 is holding register 0, 30001 is input register 0.");
        var quantity = f.AddText("Quantity", d.Quantity.ToString(), 90);
        var writeAddress = f.AddText("Write address (function 23)", d.WriteAddress.ToString(), 90);
        var scan = f.AddText("Scan rate [ms]", d.ScanRateMs.ToString(), 90);
        var disabled = f.AddCheck("Read/Write disabled (use Read/Write Once, F6)", d.Disabled);

        void Update()
        {
            var fc = FormDialog.Selected<FunctionCode>(function);
            writeAddress.IsEnabled = fc == FunctionCode.ReadWriteMultipleRegisters;
            quantity.IsEnabled = fc is not (FunctionCode.WriteSingleCoil or FunctionCode.WriteSingleRegister);
        }
        function.SelectionChanged += (_, _) => Update();
        Update();

        f.Validate = () =>
        {
            var id = Hex.ParseNumber(slave.Text);
            if (id is < 0 or > 255) return "Slave ID must be 0 to 255.";
            d.SlaveId = (byte)id;
            d.Function = FormDialog.Selected<FunctionCode>(function);
            var a = Hex.ParseNumber(address.Text);
            d.PlcAddresses = plc.IsChecked == true;
            if (d.PlcAddresses)
            {
                var plcBase = FunctionCodes.PlcBase(d.Function);
                // Accept both "40001" style and "1" style input when PLC addressing is on.
                a = a >= plcBase ? a - plcBase : a - 1;
            }
            if (a is < 0 or > 65535) return "Address must be 0 to 65535.";
            d.Address = (ushort)a;
            d.Quantity = d.Function is FunctionCode.WriteSingleCoil or FunctionCode.WriteSingleRegister ? 1 : (int)Hex.ParseNumber(quantity.Text);
            var wa = Hex.ParseNumber(writeAddress.Text);
            if (wa is < 0 or > 65535) return "Write address must be 0 to 65535.";
            d.WriteAddress = (ushort)wa;
            d.ScanRateMs = (int)Hex.ParseNumber(scan.Text);
            d.Disabled = disabled.IsChecked == true;
            var error = d.Validate();
            return error.Length > 0 ? error : null;
        };

        if (d.PlcAddresses) address.Text = (d.Address + FunctionCodes.PlcBase(d.Function)).ToString();
        return f.ShowModal() ? (d, name.Text.Trim()) : null;
    }
}

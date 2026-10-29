using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Modbus.Ui;
using Modbus.Core.Data;
using Modbus.Core.Polling;

namespace Modbus.Ui.Dialogs;

/// <summary>Display options for the selected value cells.</summary>
public static class CellDialogs
{
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static double D(string s) => double.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool Scaling(PollDocument doc, IReadOnlyList<int> cells)
    {
        var current = doc.PeekCell(cells[0])?.Scaling ?? new Scaling();
        var f = new FormDialog("Scaling", "scaling");
        f.AddInfo($"Linear scaling for {cells.Count} cell(s). Value = ScaledMin + (Raw - RawMin) x (ScaledMax - ScaledMin) / (RawMax - RawMin).");
        var enable = f.AddCheck("Enable scaling", doc.PeekCell(cells[0])?.Scaling is not null);
        var rawMin = f.AddText("Raw min", N(current.RawMin), 120);
        var rawMax = f.AddText("Raw max", N(current.RawMax), 120);
        var scaledMin = f.AddText("Scaled min", N(current.ScaledMin), 120);
        var scaledMax = f.AddText("Scaled max", N(current.ScaledMax), 120);
        var decimals = f.AddText("Decimals", current.Decimals.ToString(), 60);
        Scaling? result = null;
        f.Validate = () =>
        {
            if (enable.IsChecked != true) return null;
            result = new Scaling
            {
                RawMin = D(rawMin.Text), RawMax = D(rawMax.Text), ScaledMin = D(scaledMin.Text), ScaledMax = D(scaledMax.Text),
                Decimals = int.Parse(decimals.Text, CultureInfo.InvariantCulture),
            };
            return result.RawMax == result.RawMin ? "Raw min and raw max must differ." : null;
        };
        if (!f.ShowModal()) return false;
        foreach (var i in cells) doc.Cell(i).Scaling = result?.Clone();
        return true;
    }

    public static bool Colors(PollDocument doc, IReadOnlyList<int> cells)
    {
        var current = doc.PeekCell(cells[0])?.Colors?.Clone() ?? new ConditionalColors();
        var f = new FormDialog("Conditional Colors", "colors");
        f.AddInfo("Rule 1 is checked first. If no rule matches, the normal colors are used. \"and\" means (value AND compare) is not 0.");
        var enable = f.AddCheck("Enable conditional colors", doc.PeekCell(cells[0])?.Colors is not null);

        ComboBox ColorBox(string label, int rgb)
        {
            var items = ColorUtil.Palette.Select(p => new Choice<int>(p.Rgb, p.Name)).ToList();
            if (items.All(i => i.Value != rgb)) items.Insert(0, new Choice<int>(rgb, $"#{rgb:X6}"));
            var combo = f.AddCombo(label, items, rgb);
            combo.ItemTemplate = SwatchTemplate();
            return combo;
        }

        var rules = Enum.GetValues<ColorRule>().Select(r => new Choice<ColorRule>(r, r switch
        {
            ColorRule.NotUsed => "not used",
            ColorRule.EqualTo => "equal to",
            ColorRule.GreaterThan => "greater than",
            ColorRule.LessThan => "less than",
            ColorRule.GreaterThanOrEqualTo => "greater than or equal to",
            ColorRule.LessThanOrEqualTo => "less than or equal to",
            _ => "and",
        })).ToArray();

        f.AddSection("Normal");
        var nbg = ColorBox("Background", current.Colors[0]);
        var nfg = ColorBox("Text", current.Colors[1]);
        f.AddSection("Rule 1");
        var r1 = f.AddCombo("Rule", rules, current.Rule1);
        var c1 = f.AddText("Compare value", N(current.Compare1), 120);
        var r1bg = ColorBox("Background", current.Colors[2]);
        var r1fg = ColorBox("Text", current.Colors[3]);
        f.AddSection("Rule 2");
        var r2 = f.AddCombo("Rule", rules, current.Rule2);
        var c2 = f.AddText("Compare value", N(current.Compare2), 120);
        var r2bg = ColorBox("Background", current.Colors[4]);
        var r2fg = ColorBox("Text", current.Colors[5]);

        f.Validate = () =>
        {
            current.Colors = [Pick(nbg), Pick(nfg), Pick(r1bg), Pick(r1fg), Pick(r2bg), Pick(r2fg)];
            current.Rule1 = FormDialog.Selected<ColorRule>(r1);
            current.Rule2 = FormDialog.Selected<ColorRule>(r2);
            current.Compare1 = D(c1.Text);
            current.Compare2 = D(c2.Text);
            return null;
        };
        if (!f.ShowModal()) return false;
        foreach (var i in cells) doc.Cell(i).Colors = enable.IsChecked == true ? current.Clone() : null;
        return true;

        static int Pick(ComboBox c) => FormDialog.Selected<int>(c);
    }

    public static bool ValueNames(PollDocument doc, IReadOnlyList<int> cells)
    {
        var current = doc.PeekCell(cells[0])?.ValueNames ?? [];
        var f = new FormDialog("Value Names", "value-names");
        f.AddInfo("Show a text instead of a number. One entry per line: value=text, e.g.\n0=Stopped\n1=Running\n2=Fault");
        var box = f.AddRow("", new TextBox
        {
            Text = string.Join(Environment.NewLine, current.Select(v => $"{v.Value}={v.Name}")),
            AcceptsReturn = true, Height = 160, Width = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        });
        List<ValueName> parsed = [];
        f.Validate = () =>
        {
            parsed = [];
            foreach (var line in box.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) return $"Missing '=' in line: {line}";
                parsed.Add(new ValueName { Value = Core.Protocol.Hex.ParseNumber(line[..eq]), Name = line[(eq + 1)..].Trim() });
            }
            return null;
        };
        if (!f.ShowModal()) return false;
        foreach (var i in cells) doc.Cell(i).ValueNames = parsed.Select(v => new ValueName { Value = v.Value, Name = v.Name }).ToList();
        return true;
    }

    private static DataTemplate SwatchTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var swatch = new FrameworkElementFactory(typeof(Border));
        swatch.SetValue(FrameworkElement.WidthProperty, 14.0);
        swatch.SetValue(FrameworkElement.HeightProperty, 14.0);
        swatch.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.Gray);
        swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        swatch.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Value") { Converter = new RgbToBrush() });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        panel.AppendChild(swatch);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    private sealed class RgbToBrush : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => ColorUtil.Brush((int)value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

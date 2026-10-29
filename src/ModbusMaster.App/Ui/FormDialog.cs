using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModbusMaster.App.Ui;

public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A small labeled form with OK/Cancel/Help, built in code.</summary>
public class FormDialog : Window
{
    private readonly Grid _grid = new() { Margin = new Thickness(12, 12, 12, 4) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 12) };
    private readonly string? _helpTopic;
    private int _row;

    public FormDialog(string title, string? helpTopic = null, bool okCancel = true)
    {
        Title = title;
        _helpTopic = helpTopic;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });

        var dock = new DockPanel();
        DockPanel.SetDock(_buttons, Dock.Bottom);
        dock.Children.Add(_buttons);
        dock.Children.Add(_grid);
        Content = dock;

        if (okCancel)
        {
            AddButton("OK", OnOk, isDefault: true);
            AddButton("Cancel", () => DialogResult = false, isCancel: true);
        }
        else
        {
            AddButton("Close", Close, isCancel: true);
        }
        if (helpTopic is not null) AddButton("Help", () => Help.Open(helpTopic));

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1 && _helpTopic is not null)
            {
                Help.Open(_helpTopic);
                e.Handled = true;
            }
        };
    }

    /// <summary>Returns an error text to keep the dialog open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    public bool ShowModal() => ShowDialog() == true;

    private void OnOk()
    {
        string? error;
        try
        {
            error = Validate?.Invoke();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            error = ex.Message;
        }
        if (!string.IsNullOrEmpty(error))
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    public Button AddButton(string text, Action action, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), IsDefault = isDefault, IsCancel = isCancel };
        b.Click += (_, _) => action();
        _buttons.Children.Add(b);
        return b;
    }

    public T AddRow<T>(string label, T element) where T : FrameworkElement
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (label.Length > 0)
        {
            var l = new Label { Content = label, Padding = new Thickness(0, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center, Target = element };
            Grid.SetRow(l, _row);
            _grid.Children.Add(l);
            Grid.SetColumn(element, 1);
        }
        else
        {
            Grid.SetColumnSpan(element, 2);
        }
        element.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(element, _row++);
        _grid.Children.Add(element);
        return element;
    }

    public TextBox AddText(string label, string value, double width = 0)
    {
        var t = new TextBox { Text = value, Padding = new Thickness(2) };
        if (width > 0) { t.Width = width; t.HorizontalAlignment = HorizontalAlignment.Left; }
        t.GotKeyboardFocus += (_, _) => t.SelectAll();
        return AddRow(label, t);
    }

    public PasswordBox AddPassword(string label, string? value)
    {
        return AddRow(label, new PasswordBox { Password = value ?? "", Padding = new Thickness(2) });
    }

    public ComboBox AddCombo<T>(string label, IEnumerable<Choice<T>> items, T selected, bool editable = false)
    {
        var c = new ComboBox { IsEditable = editable };
        foreach (var item in items) c.Items.Add(item);
        c.SelectedItem = c.Items.Cast<Choice<T>>().FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, selected));
        if (c.SelectedItem is null && editable) c.Text = selected?.ToString() ?? "";
        if (c.SelectedItem is null && !editable && c.Items.Count > 0) c.SelectedIndex = 0;
        return AddRow(label, c);
    }

    public CheckBox AddCheck(string text, bool value) =>
        AddRow("", new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) });

    public TextBlock AddInfo(string text, Brush? color = null) =>
        AddRow("", new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = color ?? Brushes.DimGray });

    public void AddSection(string header)
    {
        AddRow("", new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
    }

    public static T Selected<T>(ComboBox combo) => combo.SelectedItem is Choice<T> c ? c.Value : default!;
}

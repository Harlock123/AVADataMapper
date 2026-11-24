using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVADataMapper.Core.Mapping;

namespace AVADataMapper;

/// <summary>The Checks tab (the mapping against both schemas), and trying the selected column's transform on a value.</summary>
public sealed partial class MainWindow
{
    public StackPanel ChecksPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public TextBox TryBox { get; } = new() { Watermark = "a value from the old column (null for null)", Width = 260, FontFamily = Look.Mono, FontSize = 12 };
    public TextBlock TryResult { get; } = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };

    partial void AfterChange()
    {
        ShowChecks();
        TryIt();
    }

    private void ShowChecks()
    {
        ChecksPanel.Children.Clear();
        ChecksPanel.Children.Add(new TextBlock { Text = "Checks", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        var errors = Findings.Count(f => f.Severity == Severity.Error);
        var warnings = Findings.Count(f => f.Severity == Severity.Warning);
        ChecksPanel.Children.Add(new TextBlock
        {
            Text = Old == null || New == null ? "Open both schemas: the mapping is checked against them." :
                   Findings.Count == 0 ? "The mapping fits both schemas. The dry run tries it on every old row." :
                   $"{errors} error{(errors == 1 ? "" : "s")} (the migration can't run as mapped) and {warnings} warning{(warnings == 1 ? "" : "s")} (some rows may be rejected; the dry run says which). Checked against both schemas, before any row is read.",
            Foreground = errors > 0 ? Look.Reject : Look.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var f in Findings)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("80,48,230,*"), Margin = new Thickness(0, 3) };
            row.Children.Add(new TextBlock { Text = f.Severity.ToString().ToUpperInvariant(), Foreground = f.Severity == Severity.Error ? Look.Reject : f.Severity == Severity.Warning ? Look.Deny : Look.Dim, FontSize = 11.5, FontWeight = FontWeight.SemiBold });
            row.Children.Add(new TextBlock { Text = f.Rule.Id, Foreground = Look.Heading, FontSize = 12, [Grid.ColumnProperty] = 1 });
            var about = new TextBlock { Text = f.About, Foreground = Look.Accent, FontFamily = Look.Mono, FontSize = 12.5, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), [Grid.ColumnProperty] = 2 };
            var parts = f.About.Split('.');
            about.PointerPressed += (_, _) => { ShowTable(parts[0], parts.Length > 1 ? parts[1] : null); Tabs.SelectedIndex = 0; };
            row.Children.Add(about);
            var text = new StackPanel { [Grid.ColumnProperty] = 3 };
            text.Children.Add(new TextBlock { Text = f.Message, Foreground = Look.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = f.Rule.Title + (f.Fix != null ? " · " + f.Fix : ""), Foreground = Look.Heading, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            ChecksPanel.Children.Add(row);
        }
    }

    /// <summary>Runs the selected column's mapping on the value in the Try box (key columns need the dry run's key map).</summary>
    public void TryIt()
    {
        TryResult.Text = "";
        if (_table == null || _column == null || Map.Table(_table)?.Column(_column) is not { } cm || New?.Table(_table)?.Column(_column) is not { } target) return;
        if (TryBox.Text is not { Length: > 0 } text) return;
        if (cm.Kind == ColumnKind.Key) { TryResult.Text = "A key goes through the key map: the dry run tries it."; TryResult.Foreground = Look.Dim; return; }
        object? value = text.Trim().Equals("null", StringComparison.OrdinalIgnoreCase) ? null : text;
        var o = Core.Mapping.Transforms.Apply(cm, _ => value, target, new TransformContext());
        TryResult.Text = o.Rejected != null ? "Rejected: " + o.Rejected : o.UseDefault ? $"→ the column's default ({target.Default ?? "none"})" : "→ " + Shown(o.Value);
        TryResult.Foreground = o.Rejected != null ? Look.Reject : Look.Clean;
    }

    /// <summary>A value as people read it: dates as yyyy-MM-dd, text in quotes, null as null.</summary>
    public static string Shown(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

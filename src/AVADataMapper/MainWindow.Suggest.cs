using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVADataMapper.Core.Mapping;

namespace AVADataMapper;

/// <summary>Suggestions, confirming them, the AVALegacyLens scan used as evidence, and the coverage report.</summary>
public sealed partial class MainWindow
{
    public StackPanel CoveragePanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };

    /// <summary>What the old system's code says, from an AVALegacyLens scan (null: none open).</summary>
    public Evidence? Evidence => Work.Evidence;

    public List<CoverageItem> CoverageItems { get; private set; } = [];

    /// <summary>Uses an AVALegacyLens scan of the old system as evidence (kept in the mapping, as a path).</summary>
    public void OpenEvidence(string path) => Work.OpenEvidence(path);

    /// <summary>Suggests mappings for everything not yet mapped (one undo step); nothing already mapped changes.</summary>
    public Suggester.Result? Suggest() => Work.Suggest();

    /// <summary>Confirms suggestions: one column, a whole table, or every suggestion marked sure.</summary>
    public int Confirm(string? table = null, string? column = null, bool sureOnly = false) => Work.Confirm(table, column, sureOnly);

    /// <summary>Records that an old table or column isn't migrated, and why.</summary>
    public void Drop(string about, string why) => Work.Drop(about, why);

    partial void Changed()
    {
        CoverageItems = Work.Coverage;
        ShowCoverage();
        ShowEvidence();
        AfterChange();
        Ran();
    }

    /// <summary>After every change: the profile and dry run tabs keep up with the schemas.</summary>
    partial void Ran();

    /// <summary>After every change: later parts refresh what they show.</summary>
    partial void AfterChange();

    private void ShowCoverage()
    {
        CoveragePanel.Children.Clear();
        CoveragePanel.Children.Add(new TextBlock { Text = "Coverage", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var suggest = new Button { Content = "Suggest Mappings", Margin = new Thickness(0, 0, 8, 0) };
        suggest.Click += (_, _) => Suggest();
        var sure = new Button { Content = "Confirm the Sure Ones", Margin = new Thickness(0, 0, 8, 0) };
        sure.Click += (_, _) => Confirm(sureOnly: true);
        bar.Children.Add(suggest);
        bar.Children.Add(sure);
        CoveragePanel.Children.Add(bar);
        CoveragePanel.Children.Add(new TextBlock
        {
            Text = Evidence == null ? "Every new column filled or not, the suggestions to check, and every old table and column nothing reads. Open an AVALegacyLens scan of the old system (File menu) to see what its code does with each." :
                   $"Every new column filled or not, the suggestions to check, and every old table and column nothing reads, with what the old system's code ({Evidence.Source}) does with it.",
            Foreground = Look.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left,
        });
        foreach (var section in new[] { Coverage.Required, Coverage.Unmapped, Coverage.Suggested, Coverage.Unread, Coverage.Dropped })
        {
            var items = CoverageItems.Where(i => i.Section == section).ToList();
            if (items.Count == 0) continue;
            CoveragePanel.Children.Add(new TextBlock { Text = $"{section.ToUpperInvariant()} · {items.Count}", Foreground = Look.Heading, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            foreach (var item in items) CoveragePanel.Children.Add(CoverageRow(item));
        }
    }

    private Control CoverageRow(CoverageItem item)
    {
        var brush = item.Section switch { Coverage.Required or Coverage.Unmapped => Look.Reject, Coverage.Suggested => Look.Warn, Coverage.Unread => Look.Deny, _ => Look.Dim };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*,Auto"), Margin = new Thickness(0, 2) };
        row.Children.Add(new TextBlock { Text = item.About, Foreground = brush, FontFamily = Look.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { [Grid.ColumnProperty] = 1, Margin = new Thickness(8, 0) };
        text.Children.Add(new TextBlock { Text = item.Text, Foreground = Look.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        if (item.Evidence != null) text.Children.Add(new TextBlock { Text = "Old code: " + item.Evidence, Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        var parts = item.About.Split('.');
        Button? action = item.Section switch
        {
            Coverage.Unread => new Button { Content = "Drop" },
            Coverage.Suggested => new Button { Content = "Confirm" },
            Coverage.Required or Coverage.Unmapped => new Button { Content = "Show" },
            _ => null,
        };
        if (action != null)
        {
            action.FontSize = 12;
            action.Padding = new Thickness(10, 2);
            action.Click += (_, _) =>
            {
                switch (item.Section)
                {
                    case Coverage.Unread: Drop(item.About, item.Evidence is { } e ? $"Dropped: {e}" : "Dropped: not needed in the new system."); break;
                    case Coverage.Suggested: Confirm(parts[0], parts.Length > 1 ? parts[1] : null); break;
                    default: ShowTable(parts[0], parts.Length > 1 ? parts[1] : null); Tabs.SelectedIndex = 0; break;
                }
            };
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }
        return row;
    }

    /// <summary>What the old code does with the selected column's source, under the table's name.</summary>
    public TextBlock EvidenceText { get; } = new() { Foreground = Look.Accent, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private void ShowEvidence()
    {
        EvidenceText.Text = "";
        if (Evidence == null || _table == null || _column == null || Map.Table(_table) is not { From: { } from } tm || tm.Column(_column) is not { } cm) return;
        var lines = cm.Reads.Select(r => (r, e: Evidence.Column(from, r))).Where(x => x.e != null).Select(x =>
            $"{from}.{x.r}: " + (x.e!.Unused ? "no code reads or writes it." : $"written by {(x.e.Writers.Count > 0 ? string.Join(", ", x.e.Writers) : "nothing")}") +
            string.Concat(x.e.Rules.Select(rule => $"; rule: \"{rule}\""))).ToList();
        EvidenceText.Text = string.Join("\n", lines);
    }

    private partial IEnumerable<TabItem> MoreTabs() =>
    [
        new TabItem { Header = "Coverage", Content = new ScrollViewer { Content = CoveragePanel }, FontSize = 14 },
        new TabItem { Header = "Checks", Content = new ScrollViewer { Content = ChecksPanel }, FontSize = 14 },
        .. LaterTabs(),
    ];

    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        new MenuItem
        {
            Header = "_Mapping",
            ItemsSource = (object[])
            [
                item("_Suggest Mappings", () => Suggest(), Key.G, KeyModifiers.Control),
                item("_Confirm Column", () => { if (_table != null && _column != null) Confirm(_table, _column); }, Key.Enter, KeyModifiers.Control),
                item("Confirm _Table", () => { if (_table != null) Confirm(_table); }, null, KeyModifiers.None),
                item("Confirm the Sure _Ones", () => Confirm(sureOnly: true), null, KeyModifiers.None),
                .. LaterMappingItems(item),
            ],
        },
        .. LaterMenus(item),
    ];

    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        item("Open an AVALegacyLens _Scan…", () => _ = OpenEvidencePicked(), null, KeyModifiers.None),
        new Separator(),
        .. LaterFileItems(item),
    ];

    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
        [item("_Coverage", () => Tabs.SelectedIndex = 1, Key.D2, KeyModifiers.Control), item("C_hecks", () => Tabs.SelectedIndex = 2, Key.D3, KeyModifiers.Control), .. LaterViewItems(item)];

    private async Task OpenEvidencePicked()
    {
        if (await PickFile("A scan of the old system (.lens, from AVALegacyLens)") is { } p) OpenEvidence(p);
    }

    // Hooks for the milestones after this one.
    private partial IEnumerable<TabItem> LaterTabs();
    private partial IEnumerable<object> LaterMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> LaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> LaterFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> LaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
}

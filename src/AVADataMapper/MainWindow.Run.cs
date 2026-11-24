using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;

namespace AVADataMapper;

/// <summary>The Profile tab (an old table's columns, what they hold, and what that suggests) and the Dry Run tab.</summary>
public sealed partial class MainWindow
{
    public ComboBox ProfileTable { get; } = new() { MinWidth = 200 };
    public StackPanel ProfilePanel { get; } = new() { Spacing = 2 };
    public StackPanel DryRunPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };

    public TableProfile? Profile { get; private set; }
    public List<TableRun>? LastRun { get; private set; }

    private Control BuildProfilePage()
    {
        var run = new Button { Content = "Profile", Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => { if (ProfileTable.SelectedItem is string t) ProfileOld(t); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(new TextBlock { Text = "Old table", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(ProfileTable);
        bar.Children.Add(run);
        var page = new StackPanel { Margin = new Thickness(20, 14), Spacing = 6 };
        page.Children.Add(new TextBlock { Text = "Profile", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        page.Children.Add(new TextBlock { Text = "What an old table's columns really hold: nulls, lengths, shapes, the commonest values, and the date formats in text. Read from the old database's rows; nothing is written.", Foreground = Look.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left });
        page.Children.Add(bar);
        page.Children.Add(ProfilePanel);
        return new ScrollViewer { Content = page };
    }

    /// <summary>Profiles an old table's rows, and shows what each column holds and suggests.</summary>
    public TableProfile? ProfileOld(string table)
    {
        if (Old?.Table(table) is not { } t) { Say($"The old database has no table {table}."); return null; }
        try
        {
            var (c, d) = Core.Data.Rows.OpenSide(Map.Source, MapPath, Connections);
            using (c) Profile = Profiler.Profile(c, d, t);
            ProfileTable.SelectedItem = t.Name;
            ShowProfile();
            Say($"Profiled {t.Name}: {Profile.Rows:N0} rows.");
            return Profile;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"Couldn't profile {table}: {e.Message}");
            return null;
        }
    }

    private void ShowProfile()
    {
        ProfilePanel.Children.Clear();
        if (Profile == null || Old?.Table(Profile.Table) is not { } ot) return;
        ProfilePanel.Children.Add(new TextBlock { Text = $"{ot.Name}: {Profile.Rows:N0} rows", Foreground = Look.Text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
        // Where each old column goes, so its profile can be set against the new column.
        var uses = Map.Tables.Where(m => m.From != null && Old.Table(m.From) == ot)
            .SelectMany(m => m.Columns.SelectMany(c => c.Reads.Select(r => (r, m, c)))).ToLookup(x => x.r, StringComparer.OrdinalIgnoreCase);
        foreach (var cp in Profile.Columns)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 4) };
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = cp.Name, Foreground = Look.Text, FontSize = 13 });
            name.Children.Add(new TextBlock { Text = cp.Type.Declared, Foreground = Look.Dim, FontSize = 11, FontFamily = Look.Mono });
            row.Children.Add(name);
            var info = new StackPanel { [Grid.ColumnProperty] = 1 };
            var parts = new List<string> { $"{cp.Values:N0} values, {cp.Distinct:N0}{(cp.DistinctCapped ? "+" : "")} distinct" };
            if (cp.Nulls > 0) parts.Add($"{cp.Nulls:N0} null");
            if (cp.Blanks > 0) parts.Add($"{cp.Blanks:N0} blank");
            if (cp.MaxLength != null) parts.Add($"length {cp.MinLength}–{cp.MaxLength}");
            if (cp.Min != null && cp.Type.IsNumber || cp.Type.IsTemporal) parts.Add($"from {cp.Min} to {cp.Max}");
            if (cp.Scale is > 0) parts.Add($"{cp.Scale} decimal places");
            info.Children.Add(new TextBlock { Text = string.Join(" · ", parts), Foreground = Look.Code, FontSize = 12.5 });
            if (cp.Shapes.Count > 0 && cp.Shapes.Count <= 12 || cp.DateFormats.Count > 0)
                info.Children.Add(new TextBlock { Text = "Shapes: " + string.Join(", ", cp.Shapes.Take(6).Select(s => $"{s.Shape} ×{s.Count}")), Foreground = Look.Heading, FontSize = 12, FontFamily = Look.Mono, TextWrapping = TextWrapping.Wrap });
            if (cp.Distinct <= 30 && cp.Top.Count > 0)
                info.Children.Add(new TextBlock { Text = "Values: " + string.Join(", ", cp.Top.Select(v => $"{(v.Value.Trim() != v.Value ? $"\"{v.Value}\"" : v.Value)} ×{v.Count}")), Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            foreach (var (_, m, c) in uses[cp.Name])
            {
                var nc = New?.Table(m.Target)?.Column(c.Target);
                var line = new WrapPanel();
                var says = new List<string>();
                if (nc?.Type.Length is > 0 and var n && cp.MaxLength > n && c.Kind is ColumnKind.Direct) says.Add($"the longest is {cp.MaxLength}; {m.Target}.{c.Target} takes {n}");
                if (cp.Nulls > 0 && nc is { Nullable: false } && c.IfNull == null && c.Kind is ColumnKind.Direct or ColumnKind.Convert or ColumnKind.Lookup) says.Add($"{cp.Nulls} null{(cp.Nulls == 1 ? "" : "s")}; {m.Target}.{c.Target} is required");
                if (cp.DateFormats.Count > 0 && c.Kind == ColumnKind.Convert && c.As is "date" or "datetime")
                {
                    var missing = cp.DateFormats.Except(c.Formats ?? [], StringComparer.Ordinal).ToList();
                    if (missing.Count > 0) says.Add($"dates in {string.Join(", ", cp.DateFormats)}; {m.Target}.{c.Target} reads {(c.Formats is { Count: > 0 } f ? string.Join(", ", f) : "yyyy-MM-dd only")}");
                    if (cp.NotDates.Count > 0) says.Add($"not dates in any of them: {string.Join(", ", cp.NotDates.Take(6))}");
                }
                if (c.Kind == ColumnKind.Lookup && c.Map is { } map)
                {
                    var notIn = cp.Top.Select(v => (c.Trim == true ? v.Value.Trim() : v.Value)).Where(v => !map.Keys.Contains(v, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
                    if (notIn.Count > 0) says.Add($"{string.Join(", ", notIn)} {(notIn.Count == 1 ? "isn't" : "aren't")} in the lookup");
                }
                line.Children.Add(new TextBlock { Text = $"→ {m.Target}.{c.Target}" + (says.Count > 0 ? ": " + string.Join("; ", says) : ""), Foreground = says.Count > 0 ? Look.Deny : Look.Clean, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, VerticalAlignment = VerticalAlignment.Center });
                if (cp.DateFormats.Count > 0 && c.Kind == ColumnKind.Convert && c.As is "date" or "datetime" && cp.DateFormats.Except(c.Formats ?? [], StringComparer.Ordinal).Any())
                {
                    var use = new Button { Content = "Use These Formats", FontSize = 11.5, Padding = new Thickness(8, 1), Margin = new Thickness(8, 0, 0, 0) };
                    var (tm, col) = (m.Target, c.Target);
                    use.Click += (_, _) => UseFormats(tm, col);
                    line.Children.Add(use);
                }
                if (c.Kind == ColumnKind.Lookup && c.Table != null && Map.Table(c.Table) is { From: null } code && cp.Distinct <= 200)
                {
                    var make = new Button { Content = "Make Code Rows", FontSize = 11.5, Padding = new Thickness(8, 1), Margin = new Thickness(8, 0, 0, 0) };
                    var (tm, col) = (m.Target, c.Target);
                    make.Click += (_, _) => UseCodes(tm, col);
                    line.Children.Add(make);
                }
                info.Children.Add(line);
            }
            row.Children.Add(info);
            ProfilePanel.Children.Add(row);
        }
    }

    /// <summary>Sets a date conversion's formats to the ones the profile found (one undo step).</summary>
    public void UseFormats(string table, string column)
    {
        if (Profile == null || Map.Table(table)?.Column(column) is not { From: { } from } cm || Profile.Column(from) is not { } cp) return;
        Change($"used the profile's date formats for {table}.{column}", () =>
        {
            cm.Formats = [.. (cm.Formats ?? []).Concat(cp.DateFormats).Distinct(StringComparer.Ordinal)];
            cm.BlankIsNull ??= cp.Blanks > 0 ? true : null;
        });
        ShowProfile();
        Say($"{table}.{column} reads {string.Join(", ", cm.Formats!)}." + (cp.NotDates.Count > 0 ? $" Still not dates: {string.Join(", ", cp.NotDates.Take(6))}." : ""));
    }

    /// <summary>
    /// Fills a lookup's code table from the codes the profile found (meanings start as the codes: change them), and maps
    /// each code to itself (trimmed, in capitals) where the lookup doesn't say yet. One undo step.
    /// </summary>
    public void UseCodes(string table, string column)
    {
        if (Profile == null || Map.Table(table)?.Column(column) is not { From: { } from, Table: { } codeTable } cm || Profile.Column(from) is not { } cp || Map.Table(codeTable) is not { From: null } code) return;
        var target = New?.Table(codeTable);
        var key = target?.PrimaryKey.FirstOrDefault() ?? "code";
        var other = target?.Columns.FirstOrDefault(c => !c.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && !c.Identity && !c.Nullable)?.Name;
        Change($"made {codeTable}'s rows from {from}'s codes", () =>
        {
            code.Rows ??= [];
            cm.Map ??= [];
            foreach (var v in cp.Top.Select(t => t.Value.Trim().ToUpperInvariant()).Distinct())
            {
                if (!code.Rows.Any(r => string.Equals(Expression.Str(Core.Mapping.Transforms.Clr(r.GetValueOrDefault(key))), v, StringComparison.OrdinalIgnoreCase)))
                {
                    var r = new Dictionary<string, JsonNode?> { [key] = v };
                    if (other != null) r[other] = v;
                    code.Rows.Add(r);
                }
                if (!cm.Map.Keys.Contains(v, StringComparer.OrdinalIgnoreCase)) cm.Map[v] = v;
            }
            cm.Trim ??= true;
        });
        ShowProfile();
        Say($"{codeTable} has {code.Rows!.Count} rows now. Their meanings are the codes for now: say what each means (Mapping tab, {codeTable}).");
    }

    // ───────────────────────────── the dry run ─────────────────────────────

    /// <summary>Runs the whole mapping on the old rows in memory, and shows what would go in and what would be rejected. Writes nothing.</summary>
    public List<TableRun>? RunDryRun()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        if (Findings.Any(f => f.Severity == Severity.Error)) { Say($"The mapping has {Findings.Count(f => f.Severity == Severity.Error)} errors (the Checks tab): fix them first."); return null; }
        try
        {
            var (c, d) = Core.Data.Rows.OpenSide(Map.Source, MapPath, Connections);
            using (c) LastRun = DryRun.Run(Map, Old, New, c, d);
            ShowDryRun();
            var inserted = LastRun.Sum(r => r.Inserted);
            var rejected = LastRun.Sum(r => r.Rejected);
            Say($"Dry run: {inserted:N0} rows would go in, {rejected:N0} would be rejected ({rejected - LastRun.Sum(r => r.Followed):N0} for their own values). Nothing was written.");
            return LastRun;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or FormatException)
        {
            Say($"The dry run couldn't run: {e.Message}");
            return null;
        }
    }

    private void ShowDryRun()
    {
        DryRunPanel.Children.Clear();
        DryRunPanel.Children.Add(new TextBlock { Text = "Dry Run", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var run = new Button { Content = "Run It (Writes Nothing)", Margin = new Thickness(0, 0, 8, 0) };
        run.Click += (_, _) => RunDryRun();
        bar.Children.Add(run);
        DryRunPanel.Children.Add(bar);
        DryRunPanel.Children.Add(new TextBlock { Text = "The whole migration made in memory from the old rows: every filter, transform and key, checked as the new database would check it. Rows that point at a rejected or filtered-out row are rejected with it.", Foreground = Look.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left });
        if (LastRun == null) return;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,110,80,80,90,90,*"), Margin = new Thickness(0, 10, 0, 6) };
        void Cell(string text, int col, int row, IBrush brush, bool bold = false)
        {
            if (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = 12.5, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 2, 10, 2), [Grid.ColumnProperty] = col, [Grid.RowProperty] = row });
        }
        string[] heads = ["New table", "From", "Read", "Filtered", "Would go in", "Rejected", "Their own / following another"];
        for (var i = 0; i < heads.Length; i++) Cell(heads[i], i, 0, Look.Heading);
        var r = 1;
        foreach (var t in LastRun)
        {
            Cell(t.Target, 0, r, Look.Text, true);
            Cell(t.From ?? "rows given", 1, r, Look.Dim);
            Cell($"{t.Read:N0}", 2, r, Look.Code);
            Cell(t.Filtered > 0 ? $"{t.Filtered:N0}" : "", 3, r, Look.Dim);
            Cell($"{t.Inserted:N0}", 4, r, Look.Clean);
            Cell(t.Rejected > 0 ? $"{t.Rejected:N0}" : "", 5, r, Look.Reject);
            Cell(t.Rejected > 0 ? $"{t.Rejected - t.Followed} / {t.Followed}" : "", 6, r, Look.Dim);
            r++;
        }
        DryRunPanel.Children.Add(grid);
        foreach (var t in LastRun.Where(t => t.Rejected > 0 || t.Notes.Count > 0))
        {
            DryRunPanel.Children.Add(new TextBlock { Text = $"{t.Target.ToUpperInvariant()} · {t.Rejected} REJECTED", Foreground = Look.Heading, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            foreach (var n in t.Notes) DryRunPanel.Children.Add(new TextBlock { Text = n, Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            foreach (var x in t.Rejects.Where(x => Engine.Root(x.Reasons[0]) == null).Take(60))
                DryRunPanel.Children.Add(RejectRow(t, x));
            var followed = t.Rejects.Where(x => Engine.Root(x.Reasons[0]) != null).GroupBy(x => Engine.Root(x.Reasons[0])!).ToList();
            foreach (var g in followed)
                DryRunPanel.Children.Add(new TextBlock { Text = $"{g.Count()} row{(g.Count() == 1 ? "" : "s")} ({string.Join(", ", g.Take(8).Select(x => x.OldKey))}{(g.Count() > 8 ? "…" : "")}) follow a row that's {g.Key}", Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) });
        }
    }

    private Control RejectRow(TableRun t, Reject x)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), Margin = new Thickness(0, 1) };
        var key = new TextBlock { Text = $"{t.From} {x.OldKey}", Foreground = Look.Accent, FontFamily = Look.Mono, FontSize = 12 };
        ToolTip.SetTip(key, x.Old == null ? null : string.Join("\n", x.Old.Select(kv => $"{kv.Key} = {Shown(kv.Value)}")));
        row.Children.Add(key);
        row.Children.Add(new TextBlock { Text = string.Join("; ", x.Reasons), Foreground = Look.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, [Grid.ColumnProperty] = 1 });
        return row;
    }

    private partial IEnumerable<TabItem> LaterTabs()
    {
        ProfileTable.ItemsSource = null;
        ShowDryRun();
        return
        [
            new TabItem { Header = "Profile", Content = BuildProfilePage(), FontSize = 14 },
            new TabItem { Header = "Dry Run", Content = new ScrollViewer { Content = DryRunPanel }, FontSize = 14 },
            .. EvenLaterTabs(),
        ];
    }


    private partial IEnumerable<object> LaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        new Separator(),
        item("_Profile the Old Table", () => { if (Map.Table(_table ?? "")?.From is { } f) { ProfileOld(f); Tabs.SelectedIndex = 3; } }, Key.P, KeyModifiers.Control),
        item("_Dry Run", () => { RunDryRun(); Tabs.SelectedIndex = 4; }, Key.F5, KeyModifiers.None),
        .. EvenLaterMappingItems(item),
    ];

    private partial IEnumerable<object> LaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        item("_Profile", () => Tabs.SelectedIndex = 3, Key.D4, KeyModifiers.Control),
        item("_Dry Run", () => Tabs.SelectedIndex = 4, Key.D5, KeyModifiers.Control),
        .. EvenLaterViewItems(item),
    ];

    private partial IEnumerable<TabItem> EvenLaterTabs();
    private partial IEnumerable<object> EvenLaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> EvenLaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
}

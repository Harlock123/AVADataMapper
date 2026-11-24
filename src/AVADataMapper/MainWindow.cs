using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper;

/// <summary>A new column on the mapping tab: the column, how it's filled (null: not yet), and its row.</summary>
public sealed record ColumnRow(Column Column, ColumnMap? Map, Grid View);

/// <summary>
/// The window: the old schema on the left, the new one on the right, and between them the mapping: for each new
/// table, the old table its rows come from, and for each new column, how it's filled. Built in code; later parts
/// (MainWindow.*.cs) add their tabs and menus through the partial hooks.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _table;
    private string? _column;
    private bool _filling;

    public TreeView OldTree { get; } = new() { Background = Look.Bar };
    public TreeView NewTree { get; } = new() { Background = Look.Bar };
    public TextBlock OldTitle { get; } = new() { Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(10, 8, 10, 2), TextWrapping = TextWrapping.Wrap };
    public TextBlock NewTitle { get; } = new() { Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(10, 8, 10, 2), TextWrapping = TextWrapping.Wrap };
    public TabControl Tabs { get; } = new();
    public ListBox TargetTables { get; } = new() { Background = Look.Bar, Width = 200 };
    public ComboBox SourceBox { get; } = new() { MinWidth = 200 };
    public TextBox WhereBox { get; } = new() { Watermark = "every row (a filter: Deleted = 0)", MinWidth = 260 };
    public TextBlock TableTitle { get; } = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
    public TextBlock TableNote { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public StackPanel ColumnPanel { get; } = new() { Spacing = 2 };
    public TextBox ColumnJson { get; } = new() { FontFamily = Look.Mono, FontSize = 12, AcceptsReturn = true, Height = 150, TextWrapping = TextWrapping.NoWrap };
    public TextBlock ColumnJsonTitle { get; } = new() { Foreground = Look.Heading, FontSize = 11 };
    public TextBlock txtStatusBar { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    public List<ColumnRow> Rows { get; } = [];

    /// <summary>The mapping open (a new, empty one at first).</summary>
    public MapFile Map { get; private set; } = new();

    /// <summary>Where the mapping is saved (null: not yet).</summary>
    public string? MapPath { get; private set; }

    public DatabaseSchema? Old { get; private set; }
    public DatabaseSchema? New { get; private set; }
    public bool Dirty { get; private set; }

    /// <summary>The new table on show, and the column selected in it.</summary>
    public string? CurrentTable => _table;
    public string? CurrentColumn => _column;

    /// <summary>Asks for a file to open; tests answer it themselves.</summary>
    public Func<string, Task<string?>> PickFile { get; set; }

    /// <summary>Asks where to save (title, suggested name); tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSaveFile { get; set; }

    /// <summary>Asks for a connection (title) → dialect and connection string; tests answer it themselves.</summary>
    public Func<string, Task<(Dialect, string)?>> PickConnection { get; set; }

    public MainWindow() : this(null) { }

    public MainWindow(string? open)
    {
        Title = "AVA Data Mapper";
        Width = 1500;
        Height = 900;
        MinWidth = 900;
        MinHeight = 560;
        Background = Look.Window;
        PickFile = AskForFile;
        PickSaveFile = AskWhereToSave;
        PickConnection = title => ConnectDialog.Ask(this, title);

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = txtStatusBar, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        AddExtraDocks(root);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("255,4,*,4,255") };
        var left = Side("OLD DATABASE", OldTitle, OldTree);
        var right = Side("NEW DATABASE", NewTitle, NewTree);
        Grid.SetColumn(right, 4);
        main.Children.Add(left);
        main.Children.Add(new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns, [Grid.ColumnProperty] = 1 });
        Tabs.ItemsSource = new List<TabItem> { new() { Header = "Mapping", Content = BuildMappingPage(), FontSize = 14 } }.Concat(MoreTabs()).ToList();
        Grid.SetColumn(Tabs, 2);
        main.Children.Add(Tabs);
        main.Children.Add(new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns, [Grid.ColumnProperty] = 3 });
        main.Children.Add(right);
        root.Children.Add(main);
        Content = root;

        OldTree.SelectionChanged += (_, _) => OldPicked();
        NewTree.SelectionChanged += (_, _) =>
        {
            if (_filling || NewTree.SelectedItem is not TreeViewItem { Tag: ValueTuple<Table, Column?> tag }) return;
            ShowTable(tag.Item1.FullName, tag.Item2?.Name);
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z && FocusManager?.GetFocusedElement() is not TextBox) { Undo(); e.Handled = true; }
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y && FocusManager?.GetFocusedElement() is not TextBox) { Redo(); e.Handled = true; }
        };
        AfterBuild();
        if (open != null) OpenMap(open);
        else Refresh();
    }

    private static DockPanel Side(string heading, TextBlock title, TreeView tree)
    {
        var p = new DockPanel { Background = Look.Bar };
        var h = new TextBlock { Text = heading, Foreground = Look.Heading, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 8, 10, 0) };
        DockPanel.SetDock(h, Dock.Top);
        DockPanel.SetDock(title, Dock.Top);
        p.Children.Add(h);
        p.Children.Add(title);
        p.Children.Add(tree);
        return p;
    }

    partial void AddExtraDocks(DockPanel root);
    partial void AfterBuild();
    private partial IEnumerable<TabItem> MoreTabs();
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);

    /// <summary>After the mapping or a schema changes: later parts refresh what depends on them (findings, coverage).</summary>
    partial void Changed();

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Control)
        {
            var m = new MenuItem { Header = header };
            if (key is { } k)
            {
                m.InputGesture = new KeyGesture(k, mods);
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(k, mods), Command = new Command(click) });
            }
            m.Click += (_, _) => click();
            return m;
        }
        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = (object[])
            [
                Item("_New Mapping", NewMap, Key.N),
                Item("_Open Mapping…", () => _ = OpenPicked(), Key.O),
                Item("_Save", () => _ = Save(), Key.S),
                Item("Save _As…", () => _ = SaveAs(), Key.S, KeyModifiers.Control | KeyModifiers.Shift),
                new Separator(),
                Item("Open Old _Schema File…", () => _ = OpenSchemaPicked(true), null),
                Item("Connect to the Old _Database…", () => _ = ConnectPicked(true), null),
                Item("Open New Schema _File…", () => _ = OpenSchemaPicked(false), null),
                Item("Connect to the New Data_base…", () => _ = ConnectPicked(false), null),
                new Separator(),
                .. MoreFileItems(Item),
                Item("E_xit", Close, null, KeyModifiers.None),
            ],
        };
        var edit = new MenuItem
        {
            Header = "_Edit",
            ItemsSource = (object[]) [Item("_Undo", Undo, null), Item("_Redo", Redo, null)],
        };
        var view = new MenuItem
        {
            Header = "_View",
            ItemsSource = (object[]) [Item("_Mapping", () => Tabs.SelectedIndex = 0, Key.D1), .. MoreViewItems(Item)],
        };
        return new Menu { Background = Look.Bar, ItemsSource = (object[])[file, edit, view, .. MoreMenus(Item)] };
    }

    private Control BuildMappingPage()
    {
        TargetTables.SelectionChanged += (_, _) =>
        {
            if (_filling || TargetTables.SelectedItem is not ListBoxItem { Tag: string t }) return;
            ShowTable(t);
        };
        SourceBox.SelectionChanged += (_, _) =>
        {
            if (_filling || _table == null || SourceBox.SelectedItem is not string s) return;
            SetSource(_table, s.StartsWith('(') ? null : s);
        };
        WhereBox.LostFocus += (_, _) =>
        {
            if (_table == null || Map.Table(_table) is not { } tm) return;
            var w = string.IsNullOrWhiteSpace(WhereBox.Text) ? null : WhereBox.Text.Trim();
            if (w != tm.Where) Change($"filtered {_table}", () => tm.Where = w);
        };
        var apply = new Button { Content = "Apply", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        apply.Click += (_, _) => ApplyColumnJson();
        var head = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 8) };
        head.Children.Add(TableTitle);
        head.Children.Add(TableNote);
        head.Children.Add(EvidenceText);
        var source = new WrapPanel();
        source.Children.Add(new TextBlock { Text = "Rows from", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        source.Children.Add(SourceBox);
        source.Children.Add(new TextBlock { Text = "where", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) });
        source.Children.Add(WhereBox);
        head.Children.Add(source);
        var columns = new ScrollViewer { Content = ColumnPanel, Margin = new Thickness(16, 0, 16, 0) };
        var json = new DockPanel { Margin = new Thickness(16, 6, 16, 10) };
        var jsonHead = new DockPanel();
        DockPanel.SetDock(apply, Dock.Right);
        jsonHead.Children.Add(apply);
        jsonHead.Children.Add(ColumnJsonTitle);
        DockPanel.SetDock(jsonHead, Dock.Top);
        json.Children.Add(jsonHead);
        var tryRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var tryLabel = new TextBlock { Text = "Try it on an old value:", Foreground = Look.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(tryLabel, Dock.Left);
        DockPanel.SetDock(TryBox, Dock.Left);
        tryRow.Children.Add(tryLabel);
        tryRow.Children.Add(TryBox);
        tryRow.Children.Add(TryResult);
        TryBox.TextChanged += (_, _) => TryIt();
        DockPanel.SetDock(tryRow, Dock.Bottom);
        json.Children.Add(tryRow);
        json.Children.Add(ColumnJson);
        var editor = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(json, Dock.Bottom);
        editor.Children.Add(head);
        editor.Children.Add(json);
        editor.Children.Add(columns);
        var page = new DockPanel();
        DockPanel.SetDock(TargetTables, Dock.Left);
        page.Children.Add(TargetTables);
        page.Children.Add(editor);
        return page;
    }

    // ───────────────────────────── opening and saving ─────────────────────────────

    public void NewMap()
    {
        Map = new MapFile();
        MapPath = null;
        Old = New = null;
        Evidence = null;
        _table = _column = null;
        _undo.Clear();
        _redo.Clear();
        Dirty = false;
        Refresh();
        Say("A new mapping: open the old and new schemas (File menu).");
    }

    /// <summary>Opens a mapping and the schemas it names (files beside it, or saved connections called old and new).</summary>
    public void OpenMap(string path)
    {
        try
        {
            Map = MapFile.Load(path);
            MapPath = Path.GetFullPath(path);
            _undo.Clear();
            _redo.Clear();
            Dirty = false;
            _table = Map.Tables.FirstOrDefault()?.Target;
            _column = null;
            var said = new List<string>();
            LoadEvidence();
            Old = LoadSide(Map.Source, said);
            New = LoadSide(Map.Target, said);
            Refresh();
            Say($"Opened {Path.GetFileName(path)}: {Map.Tables.Count} tables mapped." + (said.Count > 0 ? " " + string.Join(" ", said) : ""));
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Say($"Couldn't open {Path.GetFileName(path)}: {e.Message}");
        }
    }

    private DatabaseSchema? LoadSide(Side side, List<string> said)
    {
        try
        {
            if (MapFile.FullPath(side, MapPath) is { } file) return SchemaFiles.Load(file, side.Dialect == Dialect.Sqlite && SchemaFiles.IsSqlite(file) ? null : side.Dialect);
            if (Connections.Get(side.Name) is { } saved)
            {
                using var c = Catalogs.Open(saved.Dialect, saved.Connection);
                return Catalogs.Read(c, saved.Dialect, Catalogs.Describe(saved.Dialect, saved.Connection));
            }
            said.Add($"The {side.Name} database ({side.Described ?? side.Dialect.ToString()}) isn't connected: File › Connect to the {(side == Map.Source ? "Old" : "New")} Database.");
        }
        catch (Exception e) when (e is IOException or FormatException or System.Data.Common.DbException or InvalidOperationException or ArgumentException)
        {
            said.Add($"The {side.Name} schema couldn't be read: {e.Message}");
        }
        return null;
    }

    /// <summary>Opens a schema from a file (a SQLite database or a DDL script) as the old or the new side.</summary>
    public void OpenSchema(bool old, string path, Dialect? dialect = null)
    {
        try
        {
            var schema = SchemaFiles.Load(path, dialect);
            var side = old ? Map.Source : Map.Target;
            Change($"opened the {(old ? "old" : "new")} schema", () =>
            {
                side.Dialect = schema.Dialect;
                side.File = Relative(Path.GetFullPath(path));
                side.Described = schema.Source;
            });
            if (old) Old = schema;
            else New = schema;
            Refresh();
            Say($"The {(old ? "old" : "new")} schema: {schema.Tables.Count} tables from {schema.Source} ({schema.Dialect}).");
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            Say($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>Reads a live database's schema as the old or new side, and keeps the connection (by name, outside the mapping).</summary>
    public void ConnectSchema(bool old, Dialect dialect, string connection)
    {
        try
        {
            var described = Catalogs.Describe(dialect, connection);
            DatabaseSchema schema;
            using (var c = Catalogs.Open(dialect, connection)) schema = Catalogs.Read(c, dialect, described);
            var side = old ? Map.Source : Map.Target;
            Connections.Set(side.Name, dialect, connection);
            Change($"connected the {(old ? "old" : "new")} database", () =>
            {
                side.Dialect = dialect;
                side.File = null;
                side.Described = described;
            });
            if (old) Old = schema;
            else New = schema;
            Refresh();
            Say($"The {(old ? "old" : "new")} database {described}: {schema.Tables.Count} tables. The connection is kept in your settings, not in the mapping.");
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or ArgumentException or IOException)
        {
            Say($"Couldn't connect: {e.Message}");
        }
    }

    /// <summary>A side's file as the mapping keeps it: relative to the mapping file when it's saved.</summary>
    private string Relative(string full) => MapPath == null ? full : Path.GetRelativePath(Path.GetDirectoryName(MapPath)!, full).Replace('\\', '/');

    public void SaveMap(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // Files the sides read stay found: relative to where the mapping is now.
            foreach (var side in new[] { Map.Source, Map.Target })
                if (MapFile.FullPath(side, MapPath) is { } f) side.File = Path.GetRelativePath(Path.GetDirectoryName(full)!, f).Replace('\\', '/');
            if (Map.Evidence != null && MapFile.FullPath(new Side { File = Map.Evidence }, MapPath) is { } ev) Map.Evidence = Path.GetRelativePath(Path.GetDirectoryName(full)!, ev).Replace('\\', '/');
            Map.Save(full);
            MapPath = full;
            Dirty = false;
            Title = $"AVA Data Mapper: {Path.GetFileName(full)}";
            Say($"Saved {Path.GetFileName(full)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save: {e.Message}");
        }
    }

    private async Task Save()
    {
        if (MapPath == null) await SaveAs();
        else SaveMap(MapPath);
    }

    private async Task SaveAs()
    {
        if (await PickSaveFile("Save the mapping", (Map.Name ?? "mapping") + MapFile.Extension) is { } p) SaveMap(p);
    }

    private async Task OpenPicked()
    {
        if (await PickFile("Open a mapping (.dmap)") is { } p) OpenMap(p);
    }

    private async Task OpenSchemaPicked(bool old)
    {
        if (await PickFile($"The {(old ? "old" : "new")} schema: a SQLite database or a DDL script") is { } p) OpenSchema(old, p);
    }

    private async Task ConnectPicked(bool old)
    {
        if (await PickConnection($"Connect to the {(old ? "old" : "new")} database") is { } c) ConnectSchema(old, c.Item1, c.Item2);
    }

    // ───────────────────────────── changing the mapping ─────────────────────────────

    /// <summary>Makes a change to the mapping as one undo step.</summary>
    public void Change(string what, Action change)
    {
        _undo.Push(Map.ToJson());
        _redo.Clear();
        change();
        Dirty = true;
        LastChange = what;
        Refresh();
    }

    /// <summary>What the last change was (for the status line and tests).</summary>
    public string? LastChange { get; private set; }

    public void Undo()
    {
        if (_undo.Count == 0) { Say("Nothing to undo."); return; }
        _redo.Push(Map.ToJson());
        Map = MapFile.Read(_undo.Pop());
        Dirty = true;
        Refresh();
        Say("Undone.");
    }

    public void Redo()
    {
        if (_redo.Count == 0) { Say("Nothing to redo."); return; }
        _undo.Push(Map.ToJson());
        Map = MapFile.Read(_redo.Pop());
        Dirty = true;
        Refresh();
        Say("Redone.");
    }

    /// <summary>The table map for a new table, made when it's first given a source.</summary>
    private TableMap Ensure(string target)
    {
        if (Map.Table(target) is { } tm) return tm;
        var table = New?.Table(target);
        tm = new TableMap { Target = table?.Name ?? target };
        Map.Tables.Add(tm);
        return tm;
    }

    /// <summary>Says which old table a new one's rows come from (null: rows given, for a code table).</summary>
    public void SetSource(string target, string? from) =>
        Change($"set {target}'s source to {from ?? "rows given"}", () =>
        {
            var tm = Ensure(target);
            tm.From = from == null ? null : Old?.Table(from)?.Name ?? from;
            if (New?.Table(target) is { } t)
                foreach (var c in t.Columns.Where(c => c.Identity && tm.Column(c.Name) == null))
                    tm.Columns.Add(new ColumnMap { Target = c.Name, Kind = ColumnKind.Generated });
        });

    /// <summary>Says how a new column is filled: its kind and the old column it reads (a key or lookup's table from the new foreign key).</summary>
    public void SetColumn(string target, string column, ColumnKind kind, string? from = null)
    {
        Change($"mapped {target}.{column}", () =>
        {
            var tm = Ensure(target);
            var cm = tm.Column(column);
            if (cm == null)
            {
                cm = new ColumnMap { Target = New?.Table(target)?.Column(column)?.Name ?? column };
                tm.Columns.Add(cm);
                Order(tm);
            }
            cm.Kind = kind;
            cm.From = from ?? (kind is ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated ? null : cm.From);
            cm.Certainty = Certainty.Confirmed;
            if (kind is ColumnKind.Key or ColumnKind.Lookup && cm.Table == null && New?.Table(target) is { } t
                && t.ForeignKeys.FirstOrDefault(f => f.Columns.Count == 1 && f.Columns[0].Equals(column, StringComparison.OrdinalIgnoreCase)) is { } fk)
                cm.Table = New.Table(fk.RefTable)?.Name ?? fk.RefTable;
        });
    }

    /// <summary>Takes a new column out of the mapping (it's filled by nothing).</summary>
    public void Unmap(string target, string column) =>
        Change($"unmapped {target}.{column}", () => Map.Table(target)?.Columns.RemoveAll(c => c.Target.Equals(column, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Puts a table map's columns in the new table's order.</summary>
    private void Order(TableMap tm)
    {
        if (New?.Table(tm.Target) is not { } t) return;
        tm.Columns = [.. tm.Columns.OrderBy(c => t.Column(c.Target)?.Ordinal ?? int.MaxValue)];
    }

    /// <summary>Replaces the selected column's map with the JSON in the editor below the columns.</summary>
    public void ApplyColumnJson()
    {
        if (_table == null || _column == null) { Say("Pick a column first."); return; }
        try
        {
            var cm = JsonSerializer.Deserialize<ColumnMap>(ColumnJson.Text ?? "", MapFile.Json) ?? throw new FormatException("It's empty.");
            cm.Target = _column;
            var table = _table;
            Change($"edited {table}.{cm.Target}", () =>
            {
                var tm = Ensure(table);
                tm.Columns.RemoveAll(c => c.Target.Equals(cm.Target, StringComparison.OrdinalIgnoreCase));
                tm.Columns.Add(cm);
                Order(tm);
            });
            Say($"{table}.{cm.Target}: {Describe(cm)}.");
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            Say($"That isn't a column mapping: {e.Message}");
        }
    }

    /// <summary>An old column picked in the old tree fills the selected new column (as a direct copy, unless it's already another kind).</summary>
    private void OldPicked()
    {
        if (_filling || OldTree.SelectedItem is not TreeViewItem { Tag: (Table t, Column c) } || _table == null || _column == null) return;
        var tm = Map.Table(_table);
        if (tm?.From == null || Old?.Table(tm.From) != t) { Say($"{c.Name} is in {t.Name}; {_table}'s rows come from {tm?.From ?? "no table yet"}."); return; }
        var kind = tm.Column(_column)?.Kind is { } k && k is not (ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated) ? k : ColumnKind.Direct;
        SetColumn(_table, _column, kind, c.Name);
    }

    // ───────────────────────────── showing ─────────────────────────────

    /// <summary>Fills the trees, the table list and the table on show from the mapping and the schemas.</summary>
    public void Refresh()
    {
        _filling = true;
        try
        {
            Title = $"AVA Data Mapper{(MapPath != null ? ": " + Path.GetFileName(MapPath) : "")}{(Dirty ? " •" : "")}";
            OldTitle.Text = Old == null ? "Nothing open: File › Open Old Schema File or Connect." : $"{Old.Source} · {Old.Dialect} · {Old.Tables.Count} tables";
            NewTitle.Text = New == null ? "Nothing open: File › Open New Schema File or Connect." : $"{New.Source} · {New.Dialect} · {New.Tables.Count} tables";
            var read = Map.Tables.Where(t => t.From != null).SelectMany(t => t.Columns.SelectMany(c => c.Reads).Select(r => (t.From!, r)))
                .Concat(Map.Tables.Where(t => t.From != null && t.Where != null).SelectMany(t => Core.Mapping.Transforms.ExpressionColumns(t.Where!).Select(r => (t.From!, r))))
                .Select(x => $"{x.Item1}.{x.Item2}".ToLowerInvariant()).ToHashSet();
            // A source table's key is read too: the key map keeps each new row against it.
            foreach (var tm in Map.Tables.Where(t => t.From != null))
                foreach (var k in tm.OldKey ?? Old?.Table(tm.From!)?.PrimaryKey ?? []) read.Add($"{tm.From}.{k}".ToLowerInvariant());
            var fromTables = Map.Tables.Where(t => t.From != null).Select(t => t.From!.ToLowerInvariant()).ToHashSet();
            FillTree(OldTree, Old, t => fromTables.Contains(t.Name.ToLowerInvariant()) || fromTables.Contains(t.FullName.ToLowerInvariant()),
                (t, c) => read.Contains($"{t.Name}.{c.Name}".ToLowerInvariant()) || read.Contains($"{t.FullName}.{c.Name}".ToLowerInvariant()));
            FillTree(NewTree, New, t => Map.Table(t.Name) != null, (t, c) => Map.Table(t.Name)?.Column(c.Name) != null || c.Identity);
            FillTargets();
        }
        finally
        {
            _filling = false;
        }
        Findings = Old != null && New != null ? MapChecks.Check(Map, Old, New) : [];
        ShowTable(_table, _column);
        Changed();
    }

    /// <summary>The mapping checked against both schemas (empty until both are open).</summary>
    public List<MapFinding> Findings { get; private set; } = [];

    private void FillTree(TreeView tree, DatabaseSchema? schema, Func<Table, bool> tableUsed, Func<Table, Column, bool> columnUsed)
    {
        var expanded = tree.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded && i.Tag is (Table, null)).Select(i => ((Table, Column?))i.Tag!).Select(x => x.Item1.FullName).ToHashSet();
        var items = new List<TreeViewItem>();
        foreach (var t in schema?.Tables ?? [])
        {
            var used = tableUsed(t);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new TextBlock { Text = t.FullName, Foreground = used ? Look.Text : Look.Dim, FontWeight = FontWeight.SemiBold });
            header.Children.Add(new TextBlock { Text = $"{t.Columns.Count}", Foreground = Look.Gutter, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var item = new TreeViewItem { Header = header, Tag = (t, (Column?)null), IsExpanded = expanded.Contains(t.FullName) };
            var children = new List<TreeViewItem>();
            foreach (var c in t.Columns)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var on = columnUsed(t, c);
                row.Children.Add(new TextBlock { Text = on ? "●" : "○", Foreground = on ? Look.Clean : Look.Gutter, FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = c.Name, Foreground = t.PrimaryKey.Contains(c.Name) ? Look.Accent : Look.Text, FontSize = 12.5 });
                row.Children.Add(new TextBlock { Text = c.Type.Declared + (c.Nullable ? "" : " not null") + (c.Identity ? " identity" : ""), Foreground = Look.Dim, FontSize = 11.5, FontFamily = Look.Mono, VerticalAlignment = VerticalAlignment.Center });
                children.Add(new TreeViewItem { Header = row, Tag = (t, (Column?)c) });
            }
            item.ItemsSource = children;
            items.Add(item);
        }
        tree.ItemsSource = items;
    }

    private void FillTargets()
    {
        var items = new List<ListBoxItem>();
        foreach (var t in New?.Tables ?? [])
        {
            var tm = Map.Table(t.Name);
            var (done, of) = Filled(t, tm);
            var p = new StackPanel { Spacing = 1 };
            p.Children.Add(new TextBlock { Text = t.Name, Foreground = Look.Text, FontWeight = FontWeight.SemiBold });
            p.Children.Add(new TextBlock
            {
                Text = tm == null ? "not mapped" : $"{(tm.From ?? (tm.Rows != null ? $"{tm.Rows.Count} rows given" : "no source"))} · {done}/{of} columns",
                Foreground = tm == null ? Look.Dim : done == of ? Look.Clean : Look.Warn, FontSize = 11.5,
            });
            items.Add(new ListBoxItem { Content = p, Tag = t.Name });
        }
        TargetTables.ItemsSource = items;
        TargetTables.SelectedItem = items.FirstOrDefault(i => (string)i.Tag! == (New?.Table(_table ?? "")?.Name ?? _table));
    }

    /// <summary>How many of a new table's columns are filled (identity columns fill themselves), out of how many.</summary>
    public static (int Done, int Of) Filled(Table t, TableMap? tm) =>
        (t.Columns.Count(c => c.Identity || tm?.Column(c.Name) != null), t.Columns.Count);

    /// <summary>Shows a new table's mapping: its source, filter and every column with how it's filled.</summary>
    public void ShowTable(string? target, string? column = null)
    {
        _table = target == null ? null : New?.Table(target)?.Name ?? target;
        _column = column;
        Rows.Clear();
        ColumnPanel.Children.Clear();
        var table = _table == null ? null : New?.Table(_table);
        var tm = _table == null ? null : Map.Table(_table);
        _filling = true;
        try
        {
            TableTitle.Text = table == null ? (New == null ? "Open the new schema" : "Pick a new table") : table.FullName;
            if (table == null)
            {
                TableNote.Text = New == null ? "The mapping fills the new database's tables from the old one's: open both schemas from the File menu, or a mapping (.dmap)." : "Each table on the left is one of the new database's; pick one to say where its rows come from.";
                SourceBox.ItemsSource = null;
                WhereBox.Text = "";
                ColumnJson.Text = "";
                ColumnJsonTitle.Text = "";
                return;
            }
            var (done, of) = Filled(table, tm);
            TableNote.Text = $"{done} of {of} columns filled" + (table.ForeignKeys.Count > 0 ? " · refers to " + string.Join(", ", table.ForeignKeys.Select(f => f.RefTable).Distinct()) : "") + (tm?.Reason is { } r ? " · " + r : "");
            var sources = new List<string> { "(rows given: a code table)" };
            sources.AddRange(Old?.Tables.Select(t => t.Name) ?? []);
            if (tm?.From != null && !sources.Contains(tm.From)) sources.Add(tm.From);
            SourceBox.ItemsSource = sources;
            SourceBox.SelectedItem = tm == null ? null : tm.From ?? sources[0];
            WhereBox.Text = tm?.Where ?? "";
            var old = tm?.From == null ? null : Old?.Table(tm.From);
            var heads = new Grid { ColumnDefinitions = new ColumnDefinitions("185,118,165,*"), Margin = new Thickness(0, 4, 0, 4) };
            foreach (var (h, i) in new[] { "New column", "Filled by", "Old column", "How" }.Select((h, i) => (h, i)))
                heads.Children.Add(new TextBlock { Text = h, Foreground = Look.Heading, FontSize = 11, [Grid.ColumnProperty] = i });
            ColumnPanel.Children.Add(heads);
            foreach (var c in table.Columns)
            {
                var cm = tm?.Column(c.Name);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("185,118,165,*"), Background = c.Name == _column ? Look.Highlight : Brushes.Transparent, MinHeight = 30 };
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(new TextBlock { Text = c.Name + (c.Nullable ? "" : " *"), Foreground = cm == null && !c.Identity && !c.Nullable && c.Default == null ? Look.Warn : Look.Text, FontSize = 13 });
                name.Children.Add(new TextBlock { Text = c.Type.Declared + (c.Identity ? " · identity" : ""), Foreground = Look.Dim, FontSize = 11, FontFamily = Look.Mono });
                row.Children.Add(name);
                var kind = new ComboBox { ItemsSource = Enum.GetValues<ColumnKind>().Select(k => k.ToString().ToLowerInvariant()).Prepend("(not filled)").ToList(), MinWidth = 108, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 };
                kind.SelectedItem = cm == null ? (c.Identity ? "generated" : "(not filled)") : cm.Kind.ToString().ToLowerInvariant();
                var col = c.Name;
                kind.SelectionChanged += (_, _) =>
                {
                    if (_filling || kind.SelectedItem is not string k) return;
                    if (k.StartsWith('(')) Unmap(table.Name, col);
                    else SetColumn(table.Name, col, Enum.Parse<ColumnKind>(k, true));
                };
                row.Children.Add(kind);
                var from = new ComboBox { ItemsSource = (old?.Columns.Select(x => x.Name) ?? []).Prepend("").ToList(), MinWidth = 155, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 2, IsEnabled = old != null && cm?.Kind is not (ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated or ColumnKind.Merge) };
                from.SelectedItem = cm?.From is { } f ? old?.Column(f)?.Name ?? f : "";
                from.SelectionChanged += (_, _) =>
                {
                    if (_filling || from.SelectedItem is not string s) return;
                    SetColumn(table.Name, col, cm?.Kind is { } k2 && k2 is not (ColumnKind.Generated or ColumnKind.Constant or ColumnKind.Default) ? k2 : ColumnKind.Direct, s.Length == 0 ? null : s);
                };
                row.Children.Add(from);
                var how = new TextBlock
                {
                    Text = cm == null ? (c.Identity ? "the new database numbers it" : c.Nullable ? "left empty" : c.Default != null ? $"its default ({c.Default})" : "nothing fills it, and it's required") : Describe(cm),
                    Foreground = cm?.Certainty is Certainty.Guessed or Certainty.Likely ? Look.Warn : Look.Code, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [Grid.ColumnProperty] = 3,
                };
                var said = Findings.Where(f => f.About.Equals($"{table.Name}.{c.Name}", StringComparison.OrdinalIgnoreCase)).ToList();
                if (said.Count > 0)
                {
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [Grid.ColumnProperty] = 3 };
                    how.Margin = new Thickness(0);
                    how.SetValue(Grid.ColumnProperty, 0);
                    stack.Children.Add(how);
                    foreach (var found in said)
                        stack.Children.Add(new TextBlock { Text = $"{found.Rule.Id} {found.Message}", Foreground = found.Severity == Severity.Error ? Look.Reject : found.Severity == Severity.Warning ? Look.Deny : Look.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(stack);
                }
                else row.Children.Add(how);
                row.PointerPressed += (_, _) => ShowTable(table.Name, col);
                Rows.Add(new ColumnRow(c, cm, row));
                ColumnPanel.Children.Add(row);
            }
            var selected = _column == null ? null : tm?.Column(_column);
            ColumnJsonTitle.Text = _column == null ? "Pick a column to see (and edit) its mapping as JSON." : $"{table.Name}.{_column}: its mapping, as JSON (edit, then Apply)";
            ColumnJson.Text = selected == null ? (_column == null ? "" : JsonSerializer.Serialize(new ColumnMap { Target = _column, Kind = ColumnKind.Direct }, MapFile.Json)) : JsonSerializer.Serialize(selected, MapFile.Json);
            TargetTables.SelectedItem = TargetTables.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == table.Name);
        }
        finally
        {
            _filling = false;
        }
        ShowEvidence();
        TryIt();
    }

    /// <summary>A column map in words (Core's <see cref="MapText.Describe"/>).</summary>
    public static string Describe(ColumnMap c) => MapText.Describe(c);

    // ───────────────────────────── settings and asking ─────────────────────────────

    /// <summary>Tests point this at a folder of their own, so no test reads the person's settings or connections.</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>The program's settings folder (saved connections, AI settings).</summary>
    internal static string SettingsFolder => FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } d ? d : Path.GetTempPath(), "AVADataMapper");

    public SavedConnections Connections => new(SettingsFolder);

    private async Task<string?> AskForFile(string title)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> AskWhereToSave(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return file?.TryGetLocalPath();
    }

    public void Say(string text) => txtStatusBar.Text = text;

    private sealed class Command(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}

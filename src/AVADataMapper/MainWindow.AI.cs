using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AVADataMapper.Mcp;

namespace AVADataMapper;

/// <summary>The AI side's place in the window: the chat's dock, the AI menu, and the view as agents see and set it.</summary>
public sealed partial class MainWindow
{
    public Border ChatHolder { get; } = new() { Width = 440, IsVisible = false, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 0, 0, 0) };

    /// <summary>Where pictures of the view are drawn for agents, out of sight.</summary>
    public Panel AgentStage { get; } = new() { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false };

    public MenuItem ChatMenuItem { get; } = new() { Header = "AI _Chat", InputGesture = new KeyGesture(Key.F12), ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem SeeRealMenuItem { get; } = new() { Header = "Let AI Agents _See Values", ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem AllowAgentsMenuItem { get; } = new() { Header = "_Allow AI Agents (MCP)", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Started with <c>--mask</c>: agents see values masked whatever the setting says.</summary>
    public bool ForceDeidentify { get; init; }

    /// <summary>Started with <c>--real</c>: agents see values (unless <see cref="ForceDeidentify"/>).</summary>
    public bool RealFromCommandLine { get; init; }

    partial void AddExtraDocks(DockPanel root)
    {
        DockPanel.SetDock(ChatHolder, Dock.Right);
        root.Children.Add(ChatHolder);
        DockPanel.SetDock(AgentStage, Dock.Right);
        root.Children.Add(AgentStage);
    }

    private partial IEnumerable<object> LaterMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item)
    {
        ChatMenuItem.Click += OnToggleChatClick;
        SeeRealMenuItem.Click += OnSeeRealClick;
        AllowAgentsMenuItem.Click += OnAllowAgentsClick;
        return
        [
            new MenuItem
            {
                Header = "_AI",
                ItemsSource = (object[])
                [
                    ChatMenuItem,
                    new Separator(),
                    SeeRealMenuItem,
                    item("_Working Folder…", () => OnWorkingFolderClick(null, new RoutedEventArgs()), null, KeyModifiers.None),
                    new Separator(),
                    AllowAgentsMenuItem,
                    item("C_onnect an Agent…", () => _ = ShowConnect(), null, KeyModifiers.None),
                ],
            },
            new MenuItem
            {
                Header = "_Help",
                ItemsSource = (object[])
                [
                    item("_About", () => Say($"AVA Data Mapper {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}: moves a legacy system's data into its rebuild, and proves it arrived. MIT licence, Lonnie Watson."), null, KeyModifiers.None),
                ],
            },
        ];
    }

    private static readonly string[] TabNames = ["mapping", "coverage", "checks", "profile", "dry run", "migrate"];

    /// <summary>What the window shows, as agents see it.</summary>
    public ViewState CurrentView =>
        new(Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < TabNames.Length ? TabNames[Tabs.SelectedIndex] : "mapping", _table, _column);

    /// <summary>Shows what an agent asked: a table (a column selected) on a tab.</summary>
    public void ShowView(ViewState view)
    {
        if (view.Table != null) ShowTable(view.Table, view.Column);
        Tabs.SelectedIndex = Math.Max(0, Array.IndexOf(TabNames, view.Tab));
    }

    partial void AfterBuild()
    {
        SetUpAgents();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.F12) return;
            ToggleChat();
            e.Handled = true;
        };
    }
}

using Avalonia.Controls;
using Avalonia.Input;

namespace AVADataMapper;

/// <summary>The hooks later parts of the window fill: more tabs and menus.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> AgentTabs() => [];
    private partial IEnumerable<object> LaterFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
}

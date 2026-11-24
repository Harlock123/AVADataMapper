using Avalonia.Controls;
using Avalonia.Input;

namespace AVADataMapper;

/// <summary>The hooks later parts of the window fill: more tabs, menus, and what follows a change.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> MoreTabs() => [];
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
}

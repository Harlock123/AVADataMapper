using Avalonia.Controls;
using Avalonia.Input;

namespace AVADataMapper;

/// <summary>The hooks later parts of the window fill: more tabs and menus.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> LaterTabs() => [];
    private partial IEnumerable<object> LaterMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> LaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> LaterFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> LaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
}

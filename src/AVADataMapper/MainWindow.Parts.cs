using Avalonia.Controls;
using Avalonia.Input;

namespace AVADataMapper;

/// <summary>The hooks later parts of the window fill: more tabs and menus.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<object> LaterMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<TabItem> EvenLaterTabs() => [];
    private partial IEnumerable<object> EvenLaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> EvenLaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> LaterFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AVADataMapper.Core.Schema;

namespace AVADataMapper;

/// <summary>Asks for a database: its kind and connection string. The string is kept in the settings folder, never in a mapping.</summary>
internal static class ConnectDialog
{
    public static async Task<(Dialect, string)?> Ask(Window owner, string title)
    {
        var kinds = new ComboBox { ItemsSource = new[] { "SQL Server", "PostgreSQL", "SQLite" }, SelectedIndex = 0, MinWidth = 160 };
        var text = new TextBox { Watermark = "Server=…;Database=…;User Id=…;Password=…;TrustServerCertificate=True", MinWidth = 520 };
        kinds.SelectionChanged += (_, _) => text.Watermark = kinds.SelectedIndex switch
        {
            1 => "Host=…;Port=5432;Database=…;Username=…;Password=…",
            2 => "the database file's path",
            _ => "Server=…;Database=…;User Id=…;Password=…;TrustServerCertificate=True",
        };
        (Dialect, string)? answer = null;
        var ok = new Button { Content = "Connect", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 640, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 12,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Database", VerticalAlignment = VerticalAlignment.Center }, kinds } },
                    text,
                    new TextBlock { Text = "Only the catalog is read to show the schema. The connection is kept in your settings (readable by you only), not in the mapping.", Opacity = 0.7, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text.Text)) return;
            answer = ((Dialect)kinds.SelectedIndex, text.Text.Trim());
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return answer;
    }
}

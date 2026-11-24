using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AVADataMapper;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        // Denser trees and lists than Fluent's defaults: a schema can have hundreds of tables.
        Styles.Add(new Style(x => x.OfType<TreeViewItem>()) { Setters = { new Setter(TreeViewItem.MinHeightProperty, 26.0) } });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 4)), new Setter(ListBoxItem.MinHeightProperty, 26.0) } });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AVADataMapper [map.dmap]: opens the mapping and the schemas it names.
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)))
            {
                RealFromCommandLine = args.Contains("--real"),
                ForceDeidentify = args.Contains("--mask"),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

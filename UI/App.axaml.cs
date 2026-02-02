using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AsyncImageLoader;
using AsyncImageLoader.Loaders;
using HyPrism.UI.ViewModels;
using HyPrism.UI.Services;
using HyPrism.Services.Core;
using HyPrism.Services;

namespace HyPrism.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        
        // Initialize AsyncImageLoader
        ImageLoader.AsyncImageLoader = new RamCachedWebImageLoader();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize simple services like theme
        // We need to peek at the config to set the initial color
        // Since MainViewModel will initialize AppService, we can grab it from there or do a quick separate read.
        // For simplicity, we'll let MainViewModel handle the logic or just read it once here.
        
        // Quick read to set initial color before window shows
        try 
        {
            var configService = new ConfigService(UtilityService.GetEffectiveAppDir());
            ThemeService.Instance.Initialize(configService.Configuration.AccentColor);
        }
        catch { /* ignore, fallback to default */ }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

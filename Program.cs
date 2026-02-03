using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using HyPrism.Services;
using HyPrism.UI;

using Avalonia.ReactiveUI;
using Avalonia.Svg.Skia;

namespace HyPrism;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Print ASCII Logo
        try
        {
            Console.WriteLine("""

 .-..-.      .---.       _                
 : :; :      : .; :     :_;               
 :    :.-..-.:  _.'.--. .-. .--. ,-.,-.,-.
 : :: :: :; :: :   : ..': :`._-.': ,. ,. :
 :_;:_;`._. ;:_;   :_;  :_;`.__.':_;:_;:_;
        .-. :                             
        `._.'                     launcher

""");
        }
        catch { /* Ignore if console is not available */ }

        // Check for wrapper mode flag
        if (args.Contains("--wrapper"))
        {
            // In wrapper mode, launch the wrapper UI
            // This is used by Flatpak/AppImage to manage the installation of the actual HyPrism binary
            Console.WriteLine("Running in wrapper mode");
            // The wrapper UI will use WrapperGetStatus, WrapperInstallLatest, WrapperLaunch methods
        }
        
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
            
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .With(new SkiaOptions { UseOpacitySaveLayer = true })
            .LogToTrace();
            
}

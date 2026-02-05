using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HyPrism.UI.ViewModels;
using System.Windows.Input;

namespace HyPrism.UI.Views;

public partial class NewsView : UserControl
{
    public NewsView()
    {
        InitializeComponent();
        
        var scrollArea = this.FindControl<Grid>("ScrollArea");
        if (scrollArea != null)
        {
            scrollArea.PointerWheelChanged += OnScroll;
        }
    }

    private void OnScroll(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is NewsViewModel vm)
        {
            // Delta.Y is typically 1.0 or -1.0 per tick.
            // We want to scroll 1 item per tick.
            // Invert sign if felt unnatural (WheelDown -> DeltaY negative -> Next item)
            
            // If Delta.Y > 0 (Wheel Up) -> Previous Item (-1)
            // If Delta.Y < 0 (Wheel Down) -> Next Item (+1)
            
            int direction = e.Delta.Y > 0 ? -1 : 1;
            
            ICommand cmd = vm.ScrollCommand;
            if (cmd.CanExecute(direction))
            {
                cmd.Execute(direction);
                e.Handled = true;
            }
        }
    }
}

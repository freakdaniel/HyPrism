using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HyPrism.UI.ViewModels;
using System;

namespace HyPrism.UI.Views;

public partial class DashboardView : UserControl
{
    private DateTime _lastScrollTime = DateTime.MinValue;
    private const int ScrollDelayMs = 200; // Debounce time for scroll

    public DashboardView()
    {
        InitializeComponent();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (DataContext is not DashboardViewModel vm) return;
        
        // Prevent rapid page flipping
        if ((DateTime.Now - _lastScrollTime).TotalMilliseconds < ScrollDelayMs) return;

        // Logic: Scroll DOWN (Delta.Y < 0) -> Next Page
        //        Scroll UP (Delta.Y > 0) -> Prev Page
        
        int direction = 0;
        if (e.Delta.Y < -0.1) direction = 1;
        else if (e.Delta.Y > 0.1) direction = -1;

        if (direction != 0)
        {
            var maxPage = Enum.GetValues<NavigationPage>().Length - 1;
            int current = (int)vm.CurrentPage;
            int next = Math.Clamp(current + direction, 0, maxPage);
            
            if (next != current)
            {
                vm.CurrentPage = (NavigationPage)next;
                _lastScrollTime = DateTime.Now;
            }
        }
    }
}

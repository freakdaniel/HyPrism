using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using System.Collections;
using System;

namespace HyPrism.UI.Components.Inputs;

public enum SelectionInputDirection
{
    Down,
    Up
}

public partial class SelectionInput : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SelectionInput, string>(nameof(Label));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<SelectionInput, string>(nameof(Placeholder));

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
        AvaloniaProperty.Register<SelectionInput, IEnumerable>(nameof(ItemsSource));

    public IEnumerable ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SelectionInput, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }
    
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<SelectionInput, IDataTemplate?>(nameof(ItemTemplate));

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SelectionInput, bool>(nameof(IsExpanded));

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public static readonly StyledProperty<SelectionInputDirection> DirectionProperty =
        AvaloniaProperty.Register<SelectionInput, SelectionInputDirection>(nameof(Direction), SelectionInputDirection.Down);

    public SelectionInputDirection Direction
    {
        get => GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }
    
    private bool _updatingSelection;
    private IDisposable? _pointerOutsideHandler;

    public SelectionInput()
    {
        InitializeComponent();
    }

    // Handlers for closing popup when item is selected
    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem != null && !_updatingSelection)
        {
            _updatingSelection = true;
            SelectedItem = listBox.SelectedItem;
            _updatingSelection = false;
            
            IsExpanded = false;
        }
    }
    
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        if (change.Property == SelectedItemProperty && !_updatingSelection) 
        {
            // Sync logic if needed, but binding usually handles it
        }

        if (change.Property == IsExpandedProperty)
        {
             var expanded = change.GetNewValue<bool>();
             
             int zIndex = expanded ? 999 : 0;
             SetValue(Panel.ZIndexProperty, zIndex);
             
             if (Parent is Panel parentPanel)
             {
                 parentPanel.SetValue(Panel.ZIndexProperty, zIndex);
             }
             
             // Handle outside clicks
             UpdateOutsideClickHandlers(expanded);
        }
    }

    private void UpdateOutsideClickHandlers(bool enable)
    {
        if (enable)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                _pointerOutsideHandler = topLevel.AddDisposableHandler(
                    InputElement.PointerPressedEvent, 
                    OnOutsidePointerPressed, 
                    RoutingStrategies.Tunnel);
            }
        }
        else
        {
            _pointerOutsideHandler?.Dispose();
            _pointerOutsideHandler = null;
        }
    }

    private void OnOutsidePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsExpanded) return;

        if (e.Source is Visual source)
        {
             // If the click is NOT inside this control (and not inside the toggle or dropdown)
             if (!this.IsVisualAncestorOf(source))
             {
                 IsExpanded = false;
             }
        }
    }

}

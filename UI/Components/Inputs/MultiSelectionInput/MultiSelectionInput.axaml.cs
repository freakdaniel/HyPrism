using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using System.Collections;
using System;

namespace HyPrism.UI.Components.Inputs;

public partial class MultiSelectionInput : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<MultiSelectionInput, string>(nameof(Label));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
        AvaloniaProperty.Register<MultiSelectionInput, IEnumerable>(nameof(ItemsSource));

    public IEnumerable ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<MultiSelectionInput, string>(nameof(Placeholder));

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<MultiSelectionInput, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly StyledProperty<IList?> SelectedItemsProperty =
        AvaloniaProperty.Register<MultiSelectionInput, IList?>(nameof(SelectedItems), defaultBindingMode: BindingMode.TwoWay);

    public IList? SelectedItems
    {
        get => GetValue(SelectedItemsProperty);
        set => SetValue(SelectedItemsProperty, value);
    }

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<MultiSelectionInput, IDataTemplate?>(nameof(ItemTemplate));

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<MultiSelectionInput, object?>(nameof(HeaderContent));

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public static readonly StyledProperty<IDataTemplate?> HeaderTemplateProperty =
        AvaloniaProperty.Register<MultiSelectionInput, IDataTemplate?>(nameof(HeaderTemplate));

    public IDataTemplate? HeaderTemplate
    {
        get => GetValue(HeaderTemplateProperty);
        set => SetValue(HeaderTemplateProperty, value);
    }

    public static readonly StyledProperty<SelectionMode> SelectionModeProperty =
        AvaloniaProperty.Register<MultiSelectionInput, SelectionMode>(nameof(SelectionMode), SelectionMode.Multiple);

    public SelectionMode SelectionMode
    {
        get => GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<MultiSelectionInput, bool>(nameof(IsExpanded));

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private bool _updatingSelection;
    private IDisposable? _pointerOutsideHandler;

    public MultiSelectionInput()
    {
        InitializeComponent();
    }

    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem != null && !_updatingSelection)
        {
            _updatingSelection = true;
            SelectedItem = listBox.SelectedItem;
            _updatingSelection = false;

            if (SelectionMode == SelectionMode.Single)
            {
                IsExpanded = false;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandedProperty)
        {
            var expanded = change.GetNewValue<bool>();

            int zIndex = expanded ? 999 : 0;
            SetValue(Panel.ZIndexProperty, zIndex);

            if (Parent is Panel parentPanel)
            {
                parentPanel.SetValue(Panel.ZIndexProperty, zIndex);
            }

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
            if (!this.IsVisualAncestorOf(source))
            {
                IsExpanded = false;
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

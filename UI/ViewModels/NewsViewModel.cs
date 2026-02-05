using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using HyPrism.Services;
using HyPrism.Services.Core;
using HyPrism.Models;
using Avalonia.Threading;

namespace HyPrism.UI.ViewModels;

public class CircularNewsItem : ReactiveObject
{
    public NewsItemResponse Data { get; init; }
    
    // Properties for "Wheel" Layout
    private double _translateX;
    public double TranslateX
    {
        get => _translateX;
        set => this.RaiseAndSetIfChanged(ref _translateX, value);
    }
    
    private double _translateY;
    public double TranslateY
    {
        get => _translateY;
        set => this.RaiseAndSetIfChanged(ref _translateY, value);
    }
    
    // Rotation angle
    private double _angle;
    public double Angle
    {
        get => _angle;
        set => this.RaiseAndSetIfChanged(ref _angle, value);
    }
    
    private double _opacity = 1.0;
    public double Opacity
    {
        get => _opacity;
        set => this.RaiseAndSetIfChanged(ref _opacity, value);
    }
    
    private double _scale = 1.0;
    public double Scale
    {
        get => _scale;
        set => this.RaiseAndSetIfChanged(ref _scale, value);
    }

    private int _zIndex;
    public int ZIndex
    {
        get => _zIndex;
        set => this.RaiseAndSetIfChanged(ref _zIndex, value);
    }
    
    private double _blurRadius;
    public double BlurRadius
    {
        get => _blurRadius;
        set => this.RaiseAndSetIfChanged(ref _blurRadius, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public CircularNewsItem(NewsItemResponse data)
    {
        Data = data;
    }
}

public class NewsViewModel : ReactiveObject
{
    private readonly NewsService _newsService;
    private readonly BrowserService _browserService;

    private readonly List<NewsItemResponse> _allNews = new();
    
    // Circular Navigation State
    public ObservableCollection<CircularNewsItem> VisibleItems { get; } = new();
    private int _selectedIndex = 0;
    
    // Smooth scrolling offset: 0 means perfectly centered, -1/+1 means transitioning
    private double _scrollOffset = 0.0;
    public double ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            this.RaiseAndSetIfChanged(ref _scrollOffset, value);
            UpdateVisibleItems();
        }
    }
    
    // Config: How many items to show around the center.
    // 3 above, 1 center, 3 below = 7 total.
    private const int VISIBLE_WINDOW_RADIUS = 3; 

    // Reactive Localization Properties
    public IObservable<string> NewsTitle { get; }
    public IObservable<string> NewsAll { get; }
    public IObservable<string> NewsHytale { get; }
    public IObservable<string> NewsHyPrism { get; }
    public IObservable<string> NewsLoading { get; }
    
    private string _activeFilter = "all"; // "all", "hytale", "hyprism"
    public string ActiveFilter
    {
        get => _activeFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeFilter, value);
            FilterNews();            
            this.RaisePropertyChanged(nameof(ActiveTabPosition));
        }
    }
    
    // Selection for new Layout
    private NewsItemResponse? _selectedNewsItem;
    public NewsItemResponse? SelectedNewsItem
    {
        get => _selectedNewsItem;
        set => this.RaiseAndSetIfChanged(ref _selectedNewsItem, value);
    }
    
    // Tab switcher dimensions and positioning
    public double TabContainerWidth => 380.0;
    public double TabContainerHeight => 44.0;
    public double TabContainerPadding => 4.0;
    public double TabHeight => 36.0;
    
    // Calculate available width for tabs (container - 2*padding)
    private double AvailableWidth => TabContainerWidth - (TabContainerPadding * 2);
    
    // Each tab takes 1/3 of available width
    public double TabWidth => AvailableWidth / 3.0;
    
    public double ActiveTabPosition
    {
        get
        {
            return ActiveFilter.ToLower() switch
            {
                "all" => 0,
                "hytale" => TabWidth,
                "hyprism" => TabWidth * 2,
                _ => 0
            };        }
    }
    
    // News collection
    public ObservableCollection<NewsItemResponse> News { get; } = new();
    
    // Loading state
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }
    
    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }
    
    // Commands
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<string, Unit> SetFilterCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<int, Unit> ScrollCommand { get; } // -1 for Up, 1 for Down
    
    public NewsViewModel(NewsService newsService, BrowserService browserService)
    {
        _newsService = newsService;
        _browserService = browserService;
        
        // Initialize reactive localization properties
        var loc = LocalizationService.Instance;
        NewsTitle = loc.GetObservable("news.title");
        NewsAll = loc.GetObservable("news.all");
        NewsHytale = loc.GetObservable("news.hytale");
        NewsHyPrism = loc.GetObservable("news.hyprism");
        NewsLoading = loc.GetObservable("news.loading");
        
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadNewsAsync);
        SetFilterCommand = ReactiveCommand.Create<string>(
            filter => 
            { 
                ActiveFilter = filter; 
            },
            Observable.Return(true)); // Temporarily allow all
        OpenLinkCommand = ReactiveCommand.Create<string>(url =>
        {
            if (!string.IsNullOrEmpty(url))
            {
                _browserService.OpenURL(url);
            }
        });

        ScrollCommand = ReactiveCommand.Create<int>(delta => 
        {
             MoveSelection(delta);
        });
        
        _ = LoadNewsAsync();
    }
    
    private async Task LoadNewsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        
        try
        {
            var allNewsItems = await _newsService.GetNewsAsync(30, NewsSource.All);
            
            _allNews.Clear();
            _allNews.AddRange(allNewsItems);
            
            // Apply initial filter
            FilterNews();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load news: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    private void FilterNews()
    {
        News.Clear();
        
        var filteredSource = ActiveFilter switch
        {
            "hytale" => _allNews.Where(n => n.Source == "hytale"),
            "hyprism" => _allNews.Where(n => n.Source == "hyprism"),
            _ => _allNews
        };
        
        var list = filteredSource.ToList();
        foreach (var item in list)
        {
            News.Add(item);
        }

        // Reset Selection
        _selectedIndex = 0;
        if (list.Count > 0)
        {
            SetSelectedNewsItemImmediate(list[0]);
        }
        else 
        {
            SetSelectedNewsItemImmediate(null);
        }

        UpdateVisibleItems();
    }

    private DispatcherTimer? _scrollTimer;
    private readonly Stopwatch _scrollStopwatch = new();
    private int _targetIndex;
    private bool _isAnimating;
    private System.Timers.Timer? _contentDebounceTimer;
    private NewsItemResponse? _pendingSelectedItem;
    private const int ContentDebounceMs = 460;
    
    private void MoveSelection(int delta)
    {
        if (News.Count == 0 || _isAnimating) return;
        
        int newIndex = Math.Clamp(_selectedIndex + delta, 0, News.Count - 1);
        
        if (newIndex != _selectedIndex)
        {
            _isAnimating = true;
            _targetIndex = newIndex;
            
            // Update selected index BEFORE animation so new items appear in VisibleItems
            _selectedIndex = _targetIndex;
            SetSelectedNewsItemDebounced(News[_selectedIndex]);
            
            // Update collection to include new items that will appear
            UpdateVisibleItems();
            
            // Now animate from negative offset back to 0
            // If delta is +1, we start at -1 and animate to 0
            // If delta is -1, we start at +1 and animate to 0
            ScrollOffset = -delta;
            
            // Animate ScrollOffset from -delta to 0 over time (time-based for smoothness)
            const double durationMs = 300.0;
            _scrollTimer?.Stop();
            _scrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };

            _scrollStopwatch.Restart();
            _scrollTimer.Tick += (_, _) =>
            {
                double elapsed = _scrollStopwatch.Elapsed.TotalMilliseconds;
                double progress = Math.Clamp(elapsed / durationMs, 0, 1);

                // Ease-out cubic
                progress = 1 - Math.Pow(1 - progress, 3);

                ScrollOffset = -delta * (1 - progress);

                if (progress >= 1)
                {
                    _scrollTimer?.Stop();
                    ScrollOffset = 0;
                    _isAnimating = false;
                }
            };
            _scrollTimer.Start();
        }
    }

    private void SetSelectedNewsItemImmediate(NewsItemResponse? item)
    {
        _contentDebounceTimer?.Stop();
        _pendingSelectedItem = null;
        SelectedNewsItem = item;
    }

    private void SetSelectedNewsItemDebounced(NewsItemResponse? item)
    {
        _pendingSelectedItem = item;
        if (_contentDebounceTimer == null)
        {
            _contentDebounceTimer = new System.Timers.Timer(ContentDebounceMs)
            {
                AutoReset = false
            };
            _contentDebounceTimer.Elapsed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SelectedNewsItem = _pendingSelectedItem;
                });
            };
        }
        else
        {
            _contentDebounceTimer.Stop();
            _contentDebounceTimer.Interval = ContentDebounceMs;
        }

        _contentDebounceTimer.Start();
    }

    private void UpdateVisibleItems()
    {
        if (News.Count == 0)
        {
            VisibleItems.Clear();
            return;
        }
        
        // We take a window around the selected index: e.g. [Selected-3, Selected+3]
        int start = _selectedIndex - VISIBLE_WINDOW_RADIUS;
        int end = _selectedIndex + VISIBLE_WINDOW_RADIUS;
        
        // Build list of indices we need
        var neededIndices = new List<int>();
        for (int i = start; i <= end; i++)
        {
            if (i >= 0 && i < News.Count)
                neededIndices.Add(i);
        }
        
        // Reuse existing items or create new ones
        var newVisibleItems = new List<CircularNewsItem>();
        
        for (int idx = 0; idx < neededIndices.Count; idx++)
        {
            int i = neededIndices[idx];
            var item = News[i];
            
            // Try to find existing item with same data
            var existingItem = VisibleItems.FirstOrDefault(x => x.Data == item);
            CircularNewsItem vm;
            
            if (existingItem != null)
            {
                vm = existingItem;
            }
            else
            {
                vm = new CircularNewsItem(item);
            }
             
             // Calculate visual position relative to selection (center)
             int relativeIndex = i - _selectedIndex; // -3, -2, -1, 0, 1, 2, 3
             
             // WHEEL MATH
             // Center (0) is closest to the hub (Right most).
             // We calculate X shift based on a circle equation to ensure consistent curvature.
             
             // Constants for the virtual circle
             const double VIRTUAL_RADIUS = 500; // Large radius for gentle curve
             const double ITEM_HEIGHT_ESTIMATE = 96; // 80px Item + 16px Spacing
             
             // Y position relative to center (approximate)
             // Add extra spacing for items away from center to create "breathing" effect
             double baseSpacing = 85;
             double extraSpacing = Math.Abs(relativeIndex) > 0 ? Math.Abs(relativeIndex) * 3 : 0;
             
             // Apply ScrollOffset for smooth interpolation during transitions
             double effectiveRelativeIndex = relativeIndex - _scrollOffset;
             double relativeY = effectiveRelativeIndex * (baseSpacing + extraSpacing); 
             vm.TranslateY = relativeY;
             
             // Circle Equation: x = sqrt(R^2 - y^2)
             // We want the bulge (center) to be at X_MAX.
             // At edges, x is smaller.
             
             double xOnCircle = Math.Sqrt(Math.Pow(VIRTUAL_RADIUS, 2) - Math.Pow(relativeY, 2));
             
             // Calculate the x for the furthest item in the window
             double maxRelY = VISIBLE_WINDOW_RADIUS * ITEM_HEIGHT_ESTIMATE;
             double minX = Math.Sqrt(Math.Pow(VIRTUAL_RADIUS, 2) - Math.Pow(maxRelY, 2));
             
             vm.TranslateX = (xOnCircle - minX) + 10; // +10 padding
             
             // Rotation based on effective position
             double angleDeg = (effectiveRelativeIndex * 12); 
             vm.Angle = -angleDeg;
             
             // Normalized distance for opacity/scale/blur (use effective index for smooth transition)
             double normalizedDist = Math.Abs(effectiveRelativeIndex) / (double)VISIBLE_WINDOW_RADIUS;
             
             // Opacity & Scale (using normalized effective distance for smooth transitions)
             vm.Opacity = Math.Max(0.05, 1.0 - (normalizedDist * 0.9));
             vm.Scale = 1.0 - (normalizedDist * 0.15);
             
             // Z-Index: Center should be on top (use effective index for smooth transition)
             vm.ZIndex = 100 - (int)(Math.Abs(effectiveRelativeIndex) * 10);

             // Blur: smooth transition based on effective distance
             vm.BlurRadius = Math.Abs(effectiveRelativeIndex) * 3.0;
             
             vm.IsSelected = (i == _selectedIndex);
             
             newVisibleItems.Add(vm);
        }
        
        // Update VisibleItems collection efficiently
        // Remove items that are no longer needed
        for (int j = VisibleItems.Count - 1; j >= 0; j--)
        {
            if (!newVisibleItems.Contains(VisibleItems[j]))
            {
                VisibleItems.RemoveAt(j);
            }
        }
        
        // Add new items or reorder
        for (int j = 0; j < newVisibleItems.Count; j++)
        {
            if (j >= VisibleItems.Count)
            {
                VisibleItems.Add(newVisibleItems[j]);
            }
            else if (VisibleItems[j] != newVisibleItems[j])
            {
                int oldIndex = VisibleItems.IndexOf(newVisibleItems[j]);
                if (oldIndex >= 0)
                {
                    VisibleItems.Move(oldIndex, j);
                }
                else
                {
                    VisibleItems.Insert(j, newVisibleItems[j]);
                }
            }
        }
    }
}

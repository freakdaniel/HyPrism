using ReactiveUI;
using HyPrism.Services;
using HyPrism.Models;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Reactive;
using Avalonia.Threading;
using System.Collections.Generic;

namespace HyPrism.UI.ViewModels;

public class ModManagerViewModel : ReactiveObject
{
    private readonly AppService _appService;
    private readonly string _branch;
    private readonly int _version;

    // Loading State
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    // Tabs
    private string _activeTab = "installed";
    public string ActiveTab
    {
        get => _activeTab;
        set 
        {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            this.RaisePropertyChanged(nameof(IsInstalledTab));
            this.RaisePropertyChanged(nameof(IsBrowseTab));
            
            if (value == "installed")
                _ = LoadInstalledMods();
            // else if (value == "browse") LoadBrowse...
        }
    }

    public bool IsInstalledTab => ActiveTab == "installed";
    public bool IsBrowseTab => ActiveTab == "browse";

    // Installed Mods
    private ObservableCollection<InstalledMod> _installedMods = new();
    public ObservableCollection<InstalledMod> InstalledMods
    {
        get => _installedMods;
        set => this.RaiseAndSetIfChanged(ref _installedMods, value);
    }

    // Browse Mods
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }
    
    private ObservableCollection<ModInfo> _searchResults = new();
    public ObservableCollection<ModInfo> SearchResults
    {
        get => _searchResults;
        set => this.RaiseAndSetIfChanged(ref _searchResults, value);
    }
    
    // Commands
    public ReactiveCommand<string, Unit> SwitchTabCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }

    public ModManagerViewModel(AppService appService, string branch, int version)
    {
        _appService = appService;
        _branch = branch;
        _version = version;
        
        SwitchTabCommand = ReactiveCommand.Create<string>(tab => ActiveTab = tab);
        CloseCommand = ReactiveCommand.Create(() => {});
        SearchCommand = ReactiveCommand.CreateFromTask(SearchModsAsync);
        
        // Initial Load
        _ = LoadInstalledMods();
    }
    
    public async Task SearchModsAsync()
    {
        IsLoading = true;
        SearchResults.Clear();
        try 
        {
             var result = await _appService.SearchModsAsync(_searchQuery, 0, 20, System.Array.Empty<string>(), 2, 1);
             await Dispatcher.UIThread.InvokeAsync(() => 
             {
                 SearchResults = new ObservableCollection<ModInfo>(result.Mods);
             });
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Error searching mods: {ex}");
        }
        finally 
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    public async Task LoadInstalledMods()
    {
        IsLoading = true;
        try 
        {
             var mods = await Task.Run(() => _appService.GetInstanceInstalledMods(_branch, _version));
             await Dispatcher.UIThread.InvokeAsync(() => 
             {
                 InstalledMods = new ObservableCollection<InstalledMod>(mods);
             });
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Error loading mods: {ex}");
        }
        finally 
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }
}

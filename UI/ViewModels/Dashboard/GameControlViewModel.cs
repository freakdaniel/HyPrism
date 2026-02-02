using System;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using HyPrism.Services;
using HyPrism.Services.Core;
using System.Threading.Tasks;

namespace HyPrism.UI.ViewModels.Dashboard;

public class GameControlViewModel : ReactiveObject
{
    private readonly AppService _appService;

    // Commands
    public ReactiveCommand<Unit, Unit> ToggleModsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> LaunchCommand { get; }

    // Properties
    private string _selectedBranch = "Release";
    public string SelectedBranch
    {
        get => _selectedBranch;
        set => this.RaiseAndSetIfChanged(ref _selectedBranch, value);
    }
    
    private int _selectedVersion = 0;
    public int SelectedVersion
    {
        get => _selectedVersion;
        set => this.RaiseAndSetIfChanged(ref _selectedVersion, value);
    }

    public ObservableCollection<string> Branches { get; } = new() { "Release", "Pre-Release" };

    // Localization
    public IObservable<string> MainEducational { get; }
    public IObservable<string> MainBuyIt { get; }
    public IObservable<string> MainPlay { get; }

    public GameControlViewModel(AppService appService, Action<string, int> toggleMods, Func<Task> launchAction)
    {
        _appService = appService;

        var loc = LocalizationService.Instance;
        MainEducational = loc.GetObservable("main.educational");
        MainBuyIt = loc.GetObservable("main.buyIt");
        MainPlay = loc.GetObservable("main.play");
        
        ToggleModsCommand = ReactiveCommand.Create(() => toggleMods(SelectedBranch, SelectedVersion));
        
        OpenFolderCommand = ReactiveCommand.Create(() =>  
        {
            var branch = SelectedBranch?.ToLower().Replace(" ", "-") ?? "release";
            _appService.OpenInstanceFolder(branch, SelectedVersion);
        });

        LaunchCommand = ReactiveCommand.CreateFromTask(launchAction);
    }
}

using System;
using System.Reactive;
using ReactiveUI;
using HyPrism.Services;
using HyPrism.Services.Core;

namespace HyPrism.UI.ViewModels.Dashboard;

public class HeaderViewModel : ReactiveObject
{
    private readonly AppService _appService;
    
    // Commands exposed to UI
    public ReactiveCommand<Unit, Unit> ToggleProfileEditorCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    
    // Properties
    private string _nick;
    public string Nick
    {
        get => _nick;
        set => this.RaiseAndSetIfChanged(ref _nick, value);
    }
    
    public IObservable<string> AppVersion { get; }

    public HeaderViewModel(AppService appService, Action toggleProfileEditor, Action toggleSettings)
    {
        _appService = appService;
        _nick = _appService.Configuration.Nick;
        
        // Localization
        var loc = LocalizationService.Instance;
        AppVersion = loc.GetObservable("app.version");
        
        // Commands
        ToggleProfileEditorCommand = ReactiveCommand.Create(toggleProfileEditor);
        ToggleSettingsCommand = ReactiveCommand.Create(toggleSettings);
    }

    public void RefreshNick()
    {
        Nick = _appService.Configuration.Nick;
    }
}

using ReactiveUI;
using System.Reactive;
using HyPrism.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using System.Linq;
using HyPrism.Services.Core;
using System;
using System.Reactive.Linq;

namespace HyPrism.UI.ViewModels;

public class BranchItem
{
    public string DisplayName { get; set; } = "";
    public string Value { get; set; } = "";
}

public class LanguageItem
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class SettingsViewModel : ReactiveObject
{
    private readonly AppService _appService;
    public LocalizationService Localization => _appService.Localization;

    // Reactive Localization Properties - will update automatically when language changes
    public IObservable<string> SettingsTitle { get; }
    public IObservable<string> MyProfile { get; }
    public IObservable<string> General { get; }
    public IObservable<string> Visuals { get; }
    public IObservable<string> Language { get; }
    public IObservable<string> Data { get; }
    public IObservable<string> Instances { get; }
    public IObservable<string> About { get; }

    // Tabs
    private string _activeTab = "profile";
    public string ActiveTab
    {
        get => _activeTab;
        set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    // Profile
    private string _nick;
    public string Nick
    {
        get => _nick;
        set
        {
            if (_appService.SetNick(value))
            {
                this.RaiseAndSetIfChanged(ref _nick, value);
            }
        }
    }

    private string _uuid;
    public string UUID
    {
        get => _uuid;
        set
        {
            if (_appService.SetUUID(value))
            {
                this.RaiseAndSetIfChanged(ref _uuid, value);
            }
        }
    }

    // General
    public bool CloseAfterLaunch
    {
        get => _appService.Configuration.CloseAfterLaunch;
        set
        {
            if (_appService.Configuration.CloseAfterLaunch != value)
            {
                _appService.Configuration.CloseAfterLaunch = value;
                _appService.SaveConfig();
                this.RaisePropertyChanged();
            }
        }
    }

    public bool DisableNews
    {
        get => _appService.Configuration.DisableNews;
        set
        {
            if (_appService.Configuration.DisableNews != value)
            {
                _appService.Configuration.DisableNews = value;
                _appService.SaveConfig();
                this.RaisePropertyChanged();
            }
        }
    }

    private string _launcherDataDirectory;
    public string LauncherDataDirectory
    {
        get => _launcherDataDirectory;
        set => this.RaiseAndSetIfChanged(ref _launcherDataDirectory, value);
    }
    
    public List<BranchItem> BranchItems { get; } = new()
    {
        new BranchItem { DisplayName = "Stable (Recommended)", Value = "release" },
        new BranchItem { DisplayName = "Beta (Experimental)", Value = "beta" }
    };
    
    private BranchItem? _selectedBranchItem;
    public BranchItem? SelectedBranchItem
    {
        get => _selectedBranchItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBranchItem, value);
            if (value != null)
            {
                _appService.SetLauncherBranch(value.Value);
            }
        }
    }
    
    // Language
    public List<LanguageItem> LanguageItems { get; }
    
    private LanguageItem? _selectedLanguageItem;
    public LanguageItem? SelectedLanguageItem
    {
        get => _selectedLanguageItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLanguageItem, value);
            if (value != null)
            {
                _appService.SetLanguage(value.Code);
            }
        }
    }
    
    // Commands
    public ReactiveCommand<string, Unit> SwitchTabCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; } // Handled by View
    public ReactiveCommand<Unit, Unit> BrowseLauncherDataCommand { get; }
    public ReactiveCommand<Unit, Unit> RandomizeUuidCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyUuidCommand { get; }

    public SettingsViewModel(AppService appService)
    {
        _appService = appService;
        
        // Initialize reactive localization properties - these will update automatically
        var loc = LocalizationService.Instance;
        SettingsTitle = loc.GetObservable("settings.title");
        MyProfile = loc.GetObservable("settings.myProfile");
        General = loc.GetObservable("settings.general");
        Visuals = loc.GetObservable("settings.visuals");
        Language = loc.GetObservable("settings.language");
        Data = loc.GetObservable("settings.data");
        Instances = loc.GetObservable("settings.instances");
        About = loc.GetObservable("settings.about");
        
        // Initialize language items - load names from locale files
        LanguageItems = LocalizationService.GetAvailableLanguages()
            .Select(kvp => new LanguageItem { Code = kvp.Key, DisplayName = kvp.Value })
            .OrderBy(l => l.DisplayName)
            .ToList();
        
        // Initialize properties
        _nick = _appService.GetNick();
        _uuid = _appService.GetUUID();
        _launcherDataDirectory = _appService.GetLauncherDataDirectory();
        
        // Initialize branch selection
        var currentBranch = _appService.GetLauncherBranch();
        _selectedBranchItem = BranchItems.FirstOrDefault(b => b.Value == currentBranch) ?? BranchItems[0];
        
        // Initialize language selection
        var currentLanguage = _appService.Configuration.Language;
        _selectedLanguageItem = LanguageItems.FirstOrDefault(l => l.Code == currentLanguage) ?? LanguageItems.First(l => l.Code == "en-US");
        
        SwitchTabCommand = ReactiveCommand.Create<string>(tab => ActiveTab = tab);
        CloseCommand = ReactiveCommand.Create(() => { });
        BrowseLauncherDataCommand = ReactiveCommand.CreateFromTask(BrowseLauncherDataAsync);
        RandomizeUuidCommand = ReactiveCommand.Create(RandomizeUuid);
        CopyUuidCommand = ReactiveCommand.CreateFromTask(CopyUuidAsync);
    }

    private async Task BrowseLauncherDataAsync()
    {
        var result = await _appService.BrowseFolder();
        if (!string.IsNullOrEmpty(result))
        {
            var setResult = await _appService.SetLauncherDataDirectoryAsync(result);
            if (!string.IsNullOrEmpty(setResult))
            {
                LauncherDataDirectory = setResult;
            }
        }
    }
    
    private void RandomizeUuid()
    {
        UUID = System.Guid.NewGuid().ToString();
    }
    
    private async Task CopyUuidAsync()
    {
        if (Avalonia.Application.Current != null)
        {
            var topLevel = Avalonia.Application.Current.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(UUID);
            }
        }
    }
}

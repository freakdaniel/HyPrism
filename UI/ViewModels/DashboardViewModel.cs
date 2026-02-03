using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using HyPrism.Services;
using HyPrism.Services.Core;
using HyPrism.Services.Game;
using HyPrism.Services.User;
using HyPrism.Models;
using Avalonia.Threading;
using HyPrism.UI.ViewModels.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace HyPrism.UI.ViewModels;

public class DashboardViewModel : ReactiveObject
{
    private readonly GameSessionService _gameSessionService;
    private readonly ModService _modService;
    private readonly InstanceService _instanceService;
    private readonly ConfigService _configService;
    private readonly FileService _fileService;
    private readonly ProgressNotificationService _progressService;
    private readonly BrowserService _browserService;
    private readonly NewsService _newsService;
    private readonly SettingsService _settingsService;
    private readonly FileDialogService _fileDialogService;
    private readonly ProfileService _profileService;
    private readonly SkinService _skinService;

    // Partial ViewModels
    public HeaderViewModel HeaderViewModel { get; }
    public GameControlViewModel GameControlViewModel { get; }
    public NewsViewModel NewsViewModel { get; }

    // Child ViewModels (Lazy Loaded)
    private SettingsViewModel? _settingsViewModel;
    public SettingsViewModel? SettingsViewModel
    {
        get => _settingsViewModel;
        set => this.RaiseAndSetIfChanged(ref _settingsViewModel, value);
    }
    
    private ProfileEditorViewModel? _profileEditorViewModel;
    public ProfileEditorViewModel? ProfileEditorViewModel
    {
        get => _profileEditorViewModel;
        set => this.RaiseAndSetIfChanged(ref _profileEditorViewModel, value);
    }
    
    private ModManagerViewModel? _modManagerViewModel;
    public ModManagerViewModel? ModManagerViewModel
    {
        get => _modManagerViewModel;
        set => this.RaiseAndSetIfChanged(ref _modManagerViewModel, value);
    }

    // Overlay State
    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set 
        {
            if (_isSettingsOpen != value)
            {
                this.RaiseAndSetIfChanged(ref _isSettingsOpen, value);
                if (value)
                {
                    _isConfigOpen = false;
                    this.RaisePropertyChanged(nameof(IsModsOpen));
                    _isProfileEditorOpen = false;
                    this.RaisePropertyChanged(nameof(IsProfileEditorOpen));
                }
            }
        }
    }
    
    private bool _isConfigOpen; // For ModManager
    public bool IsModsOpen
    {
        get => _isConfigOpen;
        set
        {
            if (_isConfigOpen != value)
            {
                _isConfigOpen = value;
                this.RaisePropertyChanged();
                if (value)
                {
                    _isSettingsOpen = false;
                    this.RaisePropertyChanged(nameof(IsSettingsOpen));
                    _isProfileEditorOpen = false;
                    this.RaisePropertyChanged(nameof(IsProfileEditorOpen));
                }
            }
        }
    }
    
    private bool _isProfileEditorOpen;
    public bool IsProfileEditorOpen
    {
        get => _isProfileEditorOpen;
        set
        {
            if (_isProfileEditorOpen != value)
            {
                _isProfileEditorOpen = value;
                this.RaisePropertyChanged();
                if (value)
                {
                    _isSettingsOpen = false;
                    this.RaisePropertyChanged(nameof(IsSettingsOpen));
                    _isConfigOpen = false;
                    this.RaisePropertyChanged(nameof(IsModsOpen));
                }
            }
        }
    }

    private bool _isErrorModalOpen;
    public bool IsErrorModalOpen
    {
        get => _isErrorModalOpen;
        set => this.RaiseAndSetIfChanged(ref _isErrorModalOpen, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _isOverlayOpen;
    public bool IsOverlayOpen => _isOverlayOpen.Value;

    // Download Overlay Properties
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set 
        {
            this.RaiseAndSetIfChanged(ref _isDownloading, value);
            OverlayOpacity = value ? 1.0 : 0.0;
        }
    }

    private double _overlayOpacity;
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => this.RaiseAndSetIfChanged(ref _overlayOpacity, value);
    }
    
    private string _progressText = "Please wait while we download and install the game files";
    public string ProgressText
    {
        get => _progressText;
        set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }
    
    private string _currentSpeed = "";
    public string CurrentSpeed
    {
        get => _currentSpeed;
        set => this.RaiseAndSetIfChanged(ref _currentSpeed, value);
    }

    // Error
    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }
    
    private string _errorTrace = "";
    public string ErrorTrace
    {
        get => _errorTrace;
        set => this.RaiseAndSetIfChanged(ref _errorTrace, value);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> CloseErrorModalCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyErrorCommand { get; }

    public DashboardViewModel(
        GameSessionService gameSessionService,
        ModService modService,
        InstanceService instanceService,
        ConfigService configService,
        FileService fileService,
        ProgressNotificationService progressService,
        BrowserService browserService,
        NewsService newsService,
        SettingsService settingsService,
        FileDialogService fileDialogService,
        ProfileService profileService,
        SkinService skinService)
    {
        _gameSessionService = gameSessionService;
        _modService = modService;
        _instanceService = instanceService;
        _configService = configService;
        _fileService = fileService;
        _progressService = progressService;
        _browserService = browserService;
        _newsService = newsService;
        _settingsService = settingsService;
        _fileDialogService = fileDialogService;
        _profileService = profileService;
        _skinService = skinService;

        // --- Setup Overlay State ---
        _isOverlayOpen = this.WhenAnyValue(
                x => x.IsSettingsOpen, 
                x => x.IsModsOpen, 
                x => x.IsProfileEditorOpen,
                x => x.IsErrorModalOpen,
                (s, m, p, e) => s || m || p || e)
            .ToProperty(this, x => x.IsOverlayOpen);

        // --- Setup Actions ---
        Action toggleSettingsAction = () => IsSettingsOpen = !IsSettingsOpen;
        Action toggleProfileEditorAction = () => { _ = ToggleProfileEditorAsync(); };
        Action<string, int> toggleModsAction = (branchName, version) =>
        {
            if (!IsModsOpen)
            {
                var branch = branchName?.ToLower().Replace(" ", "-") ?? "release"; 
                ModManagerViewModel = new ModManagerViewModel(_modService, _instanceService, branch, version);
                ModManagerViewModel.CloseCommand.Subscribe(_ => IsModsOpen = false);
            }
            IsModsOpen = !IsModsOpen;
        };

        // --- Initialize Child ViewModels ---
        HeaderViewModel = new HeaderViewModel(_configService, toggleProfileEditorAction, toggleSettingsAction);
        GameControlViewModel = new GameControlViewModel(_instanceService, _fileService, toggleModsAction, LaunchAsync);
        NewsViewModel = new NewsViewModel(_newsService, _browserService);
        
        // Lazy-load settings if possible, or init straight away
        // We'll init settings VM here to handle the lazy open
        SettingsViewModel = new SettingsViewModel(_settingsService, _configService, _fileDialogService, LocalizationService.Instance);
        SettingsViewModel.CloseCommand.Subscribe(_ => IsSettingsOpen = false);

        // --- Commands ---
        CloseErrorModalCommand = ReactiveCommand.Create(() => { IsErrorModalOpen = false; });
        CopyErrorCommand = ReactiveCommand.Create(() => 
        {
             // TODO: Clip
        });

        // --- Subscriptions ---
        _progressService.DownloadProgressChanged += OnDownloadProgressChanged;
        _progressService.ErrorOccurred += OnErrorOccurred;
    }

    public bool DisableNews => _settingsService.GetDisableNews();

    private async Task ToggleProfileEditorAsync()
    {
        if (IsProfileEditorOpen)
        {
            IsProfileEditorOpen = false;
            return;
        }

        ProfileEditorViewModel = new ProfileEditorViewModel(_configService, _profileService, _skinService, _fileService);
        // Wire up close
        var closeCmd = ProfileEditorViewModel.CloseCommand as ReactiveCommand<Unit, Unit>;
        closeCmd?.Subscribe(_ => IsProfileEditorOpen = false);
        // Wire up update
        ProfileEditorViewModel.ProfileUpdated += () => HeaderViewModel.RefreshNick();

        await ProfileEditorViewModel.LoadProfileAsync();
        IsProfileEditorOpen = true;
    }

    private async Task LaunchAsync()
    {
        if (IsDownloading) return;
        
        IsDownloading = true;
        DownloadProgress = 0;
        ProgressText = "Preparing...";
        
        try
        {
            await _gameSessionService.DownloadAndLaunchAsync();
        }
        catch (Exception ex)
        {
            OnErrorOccurred("launch", "Launch failed", ex.ToString());
        }
        finally
        {
            IsDownloading = false;
        }
    }
    
    private void OnDownloadProgressChanged(string state, double progress, string message, long downloaded, long total)
    {
        Dispatcher.UIThread.InvokeAsync(() => {
            if (state == "download" || state == "update")
            {
                ProgressText = message;
                DownloadProgress = progress;
            }
            
            if (state == "complete")
            {
                 IsDownloading = false;
            }
        });
    }

    private void OnErrorOccurred(string type, string message, string? trace)
    {
        Dispatcher.UIThread.InvokeAsync(() => {
            ErrorMessage = message;
            ErrorTrace = trace ?? "";
            IsErrorModalOpen = true;
            IsDownloading = false;
        });
    }
}

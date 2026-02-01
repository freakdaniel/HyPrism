using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using HyPrism.Backend.Services;
using HyPrism.Backend.Models;

namespace HyPrism.Backend;

public class AppService : IDisposable
{
    private readonly string _configPath;
    private readonly string _appDir;
    private Config _config;
    private Process? _gameProcess;
    private readonly ButlerService _butlerService;
    private readonly DiscordService _discordService;
    private CancellationTokenSource? _downloadCts;
    private bool _disposed;
    
    // New services
    private readonly ConfigService _configService;
    private readonly ProfileService _profileService;
    private readonly NewsService _newsService;
    private readonly VersionService _versionService;
    private readonly DownloadService _downloadService;
    private readonly ModService _modService;
    
    // Exposed for ViewModel access
    public Config Configuration => _config;
    public ProfileService ProfileService => _profileService;
    public NewsService NewsService => _newsService;
    public VersionService VersionService => _versionService;
    public ModService ModService => _modService;

    // UI Events
    public event Action<string, double, string, long, long>? DownloadProgressChanged;
    public event Action<string, int>? GameStateChanged;
    public event Action<string, string, string?>? ErrorOccurred;
    public event Action<object>? LauncherUpdateAvailable;
    
    // Skin protection: Watch for skin file overwrites during gameplay
    private FileSystemWatcher? _skinWatcher;
    private string? _protectedSkinPath;
    private string? _protectedSkinContent;
    private bool _skinProtectionEnabled;
    private readonly object _skinProtectionLock = new object();
    
    // Lock for mod manifest operations to prevent concurrent writes
    private static readonly SemaphoreSlim _modManifestLock = new(1, 1);
    
    private static readonly HttpClient HttpClient = new()
    {
        // Use longer timeout for large file downloads - can be overridden per-request with cancellation tokens
        Timeout = TimeSpan.FromMinutes(30)
    };
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static AppService()
    {
        LoadEnvFile();
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "HyPrism/1.0");
        HttpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }
    
    /// <summary>
    /// Loads environment variables from .env file (if present) for Discord bot configuration.
    /// </summary>
    private static void LoadEnvFile()
    {
        try
        {
            var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
            if (!File.Exists(envPath)) return;
            
            foreach (var line in File.ReadAllLines(envPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    // Remove quotes if present
                    if (value.StartsWith('"') && value.EndsWith('"'))
                        value = value.Substring(1, value.Length - 2);
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
        catch { /* Ignore errors loading .env file */ }
    }

    public AppService()
    {
        _appDir = GetEffectiveAppDir();
        Directory.CreateDirectory(_appDir);
        _configPath = Path.Combine(_appDir, "config.json");
        _config = LoadConfig();
        
        // Initialize new services
        _configService = new ConfigService(_appDir);
        _config = _configService.Configuration; // Use config from ConfigService
        _profileService = new ProfileService(_appDir, _configService);
        _newsService = new NewsService();
        _versionService = new VersionService(_appDir, HttpClient, _configService);
        _downloadService = new DownloadService(HttpClient);
        _modService = new ModService(HttpClient, _appDir);
        
        // Update placeholder names to random ones immediately
        if (_config.Nick == "Hyprism" || _config.Nick == "HyPrism" || _config.Nick == "Player")
        {
            _config.Nick = GenerateRandomUsername();
            SaveConfig();
            Logger.Info("Config", $"Updated placeholder username to: {_config.Nick}");
        }
        
        // IMPORTANT: Attempt to recover orphaned skin data after config is loaded.
        // This handles the case where config was reset but old skin files still exist.
        TryRecoverOrphanedSkinOnStartup();
        
        MigrateLegacyData();
        _butlerService = new ButlerService(_appDir);
        _discordService = new DiscordService();
        _discordService.Initialize();
        
        // Initialize profile mods symlink if an active profile exists
        InitializeProfileModsSymlink();
    }

    /// <summary>
    /// Gets the effective app directory, checking for environment variable override first.
    /// </summary>
    private string GetEffectiveAppDir()
    {
        // First check environment variable
        var envDir = Environment.GetEnvironmentVariable("HYPRISM_DATA");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            return envDir;
        }

        // Then check if there's a config file in default location with custom path
        var defaultDir = GetDefaultAppDir();
        var defaultConfigPath = Path.Combine(defaultDir, "config.json");
        if (File.Exists(defaultConfigPath))
        {
            try
            {
                var configJson = File.ReadAllText(defaultConfigPath);
                var config = JsonSerializer.Deserialize<Config>(configJson, JsonOptions);
                if (config != null && !string.IsNullOrWhiteSpace(config.LauncherDataDirectory) && Directory.Exists(config.LauncherDataDirectory))
                {
                    return config.LauncherDataDirectory;
                }
            }
            catch { /* Ignore parsing errors, use default */ }
        }

        return defaultDir;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSkinProtection();
        _discordService?.Dispose();
    }
    
    private string GetDefaultAppDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyPrism");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "HyPrism");
        }
        else
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Path.Combine(xdgDataHome, "HyPrism");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "HyPrism");
        }
    }

    /// <summary>
    /// Gets the UserData path for an instance. The game stores skins, settings, etc. here.
    /// </summary>
    private string GetInstanceUserDataPath(string versionPath)
    {
        return Path.Combine(versionPath, "UserData");
    }

    private static string GetOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }

    private static string GetArch()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => "amd64"
        };
    }

    private int ResolveVersionOrLatest(string branch, int version)
    {
        if (version > 0) return version;
        if (_config.SelectedVersion > 0) return _config.SelectedVersion;

        var info = LoadLatestInfo(branch);
        if (info?.Version > 0) return info.Version;

        string resolvedBranch = string.IsNullOrWhiteSpace(branch) ? _config.VersionType : branch;
        string branchDir = GetBranchPath(resolvedBranch);
        if (Directory.Exists(branchDir))
        {
            var latest = Directory.GetDirectories(branchDir)
                .Select(Path.GetFileName)
                .Select(name => int.TryParse(name, out var v) ? v : -1)
                .Where(v => v > 0)
                .OrderByDescending(v => v)
                .FirstOrDefault();
            return latest;
        }

        return 0;
    }

    // Normalize version type: "prerelease" or "pre-release" -> "pre-release"
    private static string NormalizeVersionType(string versionType)
    {
        if (string.IsNullOrWhiteSpace(versionType))
            return "release";
        if (versionType == "prerelease" || versionType == "pre-release")
            return "pre-release";
        if (versionType == "latest")
            return "release";
        return versionType;
    }

    private sealed class LatestInstanceInfo
    {
        public int Version { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private string GetInstanceRoot()
    {
        var root = string.IsNullOrWhiteSpace(_config.InstanceDirectory)
            ? Path.Combine(_appDir, "instances")
            : _config.InstanceDirectory;

        root = Environment.ExpandEnvironmentVariables(root);

        if (!Path.IsPathRooted(root))
        {
            root = Path.GetFullPath(Path.Combine(_appDir, root));
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            Logger.Error("Config", $"Failed to create instance root at {root}: {ex.Message}");
        }

        return root;
    }

    private string GetBranchPath(string branch)
    {
        string normalizedBranch = NormalizeVersionType(branch);
        return Path.Combine(GetInstanceRoot(), normalizedBranch);
    }

    private static void SafeCopyDirectory(string sourceDir, string destDir)
    {
        // CRITICAL: Prevent copying into itself (causes infinite loop)
        var normalizedSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedDest = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar);
        
        if (normalizedSource.Equals(normalizedDest, StringComparison.OrdinalIgnoreCase))
            return;
        if (normalizedDest.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;
        if (normalizedSource.StartsWith(normalizedDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;
            
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            if (!File.Exists(destFile))
            {
                File.Copy(file, destFile, overwrite: false);
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            SafeCopyDirectory(dir, destSubDir);
        }
    }

    private Config? LoadConfigFromPath(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private Config? LoadConfigFromToml(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var cfg = new Config();
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

                static string Unquote(string value)
                {
                    value = value.Trim();
                    // Handle double quotes
                    if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                    {
                        return value.Substring(1, value.Length - 2);
                    }
                    // Handle single quotes (TOML style)
                    if (value.StartsWith("'") && value.EndsWith("'") && value.Length >= 2)
                    {
                        return value.Substring(1, value.Length - 2);
                    }
                    return value;
                }

                var parts = trimmed.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim().ToLowerInvariant();
                var val = Unquote(parts[1]);

                switch (key)
                {
                    case "nick":
                    case "name":
                    case "username":
                        cfg.Nick = val;
                        break;
                    case "uuid":
                        cfg.UUID = val;
                        break;
                    case "instance_directory":
                    case "instancedirectory":
                    case "instance_dir":
                    case "instancepath":
                    case "instance_path":
                        cfg.InstanceDirectory = val;
                        break;
                    case "versiontype":
                    case "branch":
                        cfg.VersionType = NormalizeVersionType(val);
                        break;
                    case "selectedversion":
                        if (int.TryParse(val, out var sel)) cfg.SelectedVersion = sel;
                        break;
                }
            }
            return cfg;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> GetLegacyRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            roots.Add(path);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Add(Path.Combine(appData, "hyprism"));
            Add(Path.Combine(appData, "Hyprism"));
            Add(Path.Combine(appData, "HyPrism")); // legacy casing
            Add(Path.Combine(appData, "HyPrismLauncher"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Add(Path.Combine(home, "Library", "Application Support", "hyprism"));
            Add(Path.Combine(home, "Library", "Application Support", "Hyprism"));
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                Add(Path.Combine(xdg, "hyprism"));
                Add(Path.Combine(xdg, "Hyprism"));
            }
            Add(Path.Combine(home, ".local", "share", "hyprism"));
            Add(Path.Combine(home, ".local", "share", "Hyprism"));
        }

        return roots;
    }

    private IEnumerable<string> GetInstanceRootsIncludingLegacy()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> YieldIfExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) yield break;
            if (!Directory.Exists(path)) yield break;

            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                yield return full;
            }
        }

        foreach (var root in YieldIfExists(GetInstanceRoot()))
        {
            yield return root;
        }

        foreach (var legacy in GetLegacyRoots())
        {
            // Check legacy naming: 'instance' (singular) and 'instances' (plural)
            foreach (var r in YieldIfExists(Path.Combine(legacy, "instance")))
            {
                yield return r;
            }

            foreach (var r in YieldIfExists(Path.Combine(legacy, "instances")))
            {
                yield return r;
            }
        }

        // Also check old 'instance' folder in current app dir (singular -> plural migration)
        var oldInstanceDir = Path.Combine(_appDir, "instance");
        foreach (var r in YieldIfExists(oldInstanceDir))
        {
            yield return r;
        }
    }

    private string? FindExistingInstancePath(string branch, int version)
    {
        string normalizedBranch = NormalizeVersionType(branch);
        string versionSegment = version == 0 ? "latest" : version.ToString();

        foreach (var root in GetInstanceRootsIncludingLegacy())
        {
            // New layout: branch/version
            var candidate1 = Path.Combine(root, normalizedBranch, versionSegment);
            if (Directory.Exists(candidate1))
            {
                return candidate1;
            }

            // Legacy dash layout: release-5
            var candidate2 = Path.Combine(root, $"{normalizedBranch}-{versionSegment}");
            if (Directory.Exists(candidate2))
            {
                return candidate2;
            }

            // Legacy dash layout with v prefix: release-v5
            var candidate3 = Path.Combine(root, $"{normalizedBranch}-v{versionSegment}");
            if (Directory.Exists(candidate3))
            {
                return candidate3;
            }
        }

        return null;
    }

    private string? LoadLegacyUuid(string legacyRoot)
    {
        var candidates = new[] { "uuid.txt", "uuid", "uuid.dat" };
        foreach (var name in candidates)
        {
            var path = Path.Combine(legacyRoot, name);
            if (!File.Exists(path)) continue;

            try
            {
                var content = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(content) && Guid.TryParse(content, out var guid))
                {
                    return guid.ToString();
                }
            }
            catch
            {
                // ignore malformed legacy uuid files
            }
        }

        return null;
    }

    private void MigrateLegacyData()
    {
        try
        {
            foreach (var legacyRoot in GetLegacyRoots())
            {
                if (!Directory.Exists(legacyRoot)) continue;

                Logger.Info("Migrate", $"Found legacy data at {legacyRoot}");

                var legacyConfigPath = Path.Combine(legacyRoot, "config.json");
                var legacyTomlPath = Path.Combine(legacyRoot, "config.toml");
                
                // Load both JSON and TOML configs
                var jsonConfig = LoadConfigFromPath(legacyConfigPath);
                var tomlConfig = LoadConfigFromToml(legacyTomlPath);
                
                // Prefer TOML if it has a custom nick (not default), or prefer whichever has custom data
                Config? legacyConfig = null;
                bool tomlHasCustomNick = tomlConfig != null && !string.IsNullOrWhiteSpace(tomlConfig.Nick) 
                    && !string.Equals(tomlConfig.Nick, "Hyprism", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tomlConfig.Nick, "Player", StringComparison.OrdinalIgnoreCase);
                bool jsonHasCustomNick = jsonConfig != null && !string.IsNullOrWhiteSpace(jsonConfig.Nick)
                    && !string.Equals(jsonConfig.Nick, "Hyprism", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(jsonConfig.Nick, "Player", StringComparison.OrdinalIgnoreCase);
                    
                if (tomlHasCustomNick)
                {
                    legacyConfig = tomlConfig;
                    Logger.Info("Migrate", $"Using legacy config.toml (has custom nick): nick={legacyConfig?.Nick}, uuid={legacyConfig?.UUID}");
                }
                else if (jsonHasCustomNick)
                {
                    legacyConfig = jsonConfig;
                    Logger.Info("Migrate", $"Using legacy config.json (has custom nick): nick={legacyConfig?.Nick}, uuid={legacyConfig?.UUID}");
                }
                else if (tomlConfig != null)
                {
                    legacyConfig = tomlConfig;
                    Logger.Info("Migrate", $"Using legacy config.toml: nick={legacyConfig?.Nick}, uuid={legacyConfig?.UUID}");
                }
                else if (jsonConfig != null)
                {
                    legacyConfig = jsonConfig;
                    Logger.Info("Migrate", $"Using legacy config.json: nick={legacyConfig?.Nick}, uuid={legacyConfig?.UUID}");
                }
                else
                {
                    Logger.Warning("Migrate", $"No valid config found in {legacyRoot}");
                }

                // Only merge legacy config when current user name is still a default/placeholder
                bool allowMerge = string.IsNullOrWhiteSpace(_config.Nick)
                                  || string.Equals(_config.Nick, "Hyprism", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(_config.Nick, "Player", StringComparison.OrdinalIgnoreCase);

                if (!allowMerge)
                {
                    Logger.Info("Migrate", "Skipping legacy config merge because current nickname is custom.");
                }

                var updated = false;

                if (legacyConfig != null && allowMerge)
                {
                    Logger.Info("Migrate", $"Merging legacy config: nick={legacyConfig.Nick}");
                    if (!string.IsNullOrWhiteSpace(legacyConfig.Nick))
                    {
                        _config.Nick = legacyConfig.Nick;
                        updated = true;
                        Logger.Success("Migrate", $"Migrated nickname: {legacyConfig.Nick}");
                    }

                    if (string.IsNullOrWhiteSpace(_config.UUID) && !string.IsNullOrWhiteSpace(legacyConfig.UUID))
                    {
                        _config.UUID = legacyConfig.UUID;
                        updated = true;
                    }

                    if (string.IsNullOrWhiteSpace(_config.InstanceDirectory) && !string.IsNullOrWhiteSpace(legacyConfig.InstanceDirectory))
                    {
                        _config.InstanceDirectory = legacyConfig.InstanceDirectory;
                        updated = true;
                    }

                    if (_config.SelectedVersion == 0 && legacyConfig.SelectedVersion > 0)
                    {
                        _config.SelectedVersion = legacyConfig.SelectedVersion;
                        updated = true;
                    }

                    if (string.IsNullOrWhiteSpace(_config.VersionType) && !string.IsNullOrWhiteSpace(legacyConfig.VersionType))
                    {
                        _config.VersionType = NormalizeVersionType(legacyConfig.VersionType);
                        updated = true;
                    }
                }

                // Fallback: pick up a legacy uuid file if config lacked one
                if (string.IsNullOrWhiteSpace(_config.UUID))
                {
                    var legacyUuid = LoadLegacyUuid(legacyRoot);
                    if (!string.IsNullOrWhiteSpace(legacyUuid))
                    {
                        _config.UUID = legacyUuid;
                        updated = true;
                        Logger.Info("Migrate", "Recovered legacy UUID from legacy folder.");
                    }
                }

                if (updated)
                {
                    SaveConfigInternal(_config);
                    
                    // Delete old config.toml after successful migration
                    if (File.Exists(legacyTomlPath))
                    {
                        try
                        {
                            File.Delete(legacyTomlPath);
                            Logger.Success("Migrate", $"Deleted legacy config.toml at {legacyTomlPath}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning("Migrate", $"Failed to delete legacy config.toml: {ex.Message}");
                        }
                    }
                }

                // Detect legacy instance folders and copy to new structure
                var legacyInstanceRoot = Path.Combine(legacyRoot, "instance");
                var legacyInstancesRoot = Path.Combine(legacyRoot, "instances"); // v1 naming
                if (!Directory.Exists(legacyInstanceRoot) && Directory.Exists(legacyInstancesRoot))
                {
                    legacyInstanceRoot = legacyInstancesRoot;
                }

                if (Directory.Exists(legacyInstanceRoot))
                {
                    Logger.Info("Migrate", $"Legacy instances detected at {legacyInstanceRoot}");
                    MigrateLegacyInstances(legacyInstanceRoot);
                }
            }

            // Also migrate old 'instance' folder in current app dir (singular -> plural)
            var oldInstanceDir = Path.Combine(_appDir, "instance");
            if (Directory.Exists(oldInstanceDir))
            {
                Logger.Info("Migrate", $"Old 'instance' folder detected at {oldInstanceDir}");
                MigrateLegacyInstances(oldInstanceDir);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Migrate", $"Legacy migration skipped: {ex.Message}");
        }
    }

    private void MigrateLegacyInstances(string legacyInstanceRoot)
    {
        try
        {
            var newInstanceRoot = GetInstanceRoot();
            
            // Check if source is the same as destination (case-insensitive for macOS)
            var normalizedSource = Path.GetFullPath(legacyInstanceRoot).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedDest = Path.GetFullPath(newInstanceRoot).TrimEnd(Path.DirectorySeparatorChar);
            var isSameDirectory = normalizedSource.Equals(normalizedDest, StringComparison.OrdinalIgnoreCase);
            
            // If same directory, we'll restructure in-place (rename release-v5 to release/5)
            // If different directories, we'll copy as before
            if (isSameDirectory)
            {
                Logger.Info("Migrate", "Source equals destination - will restructure legacy folders in-place");
                RestructureLegacyFoldersInPlace(legacyInstanceRoot);
                return;
            }
            
            // CRITICAL: Prevent migration if source is inside destination (would cause infinite loop)
            if (normalizedSource.StartsWith(normalizedDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("Migrate", "Skipping migration - source is inside destination");
                return;
            }
            
            Logger.Info("Migrate", $"Copying legacy instances from {legacyInstanceRoot} to {newInstanceRoot}");

            foreach (var legacyDir in Directory.GetDirectories(legacyInstanceRoot))
            {
                var folderName = Path.GetFileName(legacyDir);
                if (string.IsNullOrEmpty(folderName)) continue;
                
                // CRITICAL: Skip folders that are already branch names (new structure)
                // These indicate we're looking at already-migrated data
                var normalizedFolderName = folderName.ToLowerInvariant();
                if (normalizedFolderName == "release" || normalizedFolderName == "pre-release" || 
                    normalizedFolderName == "prerelease" || normalizedFolderName == "latest")
                {
                    Logger.Info("Migrate", $"Skipping {folderName} - already in new structure format");
                    continue;
                }

                // Parse legacy naming: "release-v5" or "release-5" or "release/5"
                string branch;
                string versionSegment;

                if (folderName.Contains("/"))
                {
                    // Already new format: release/5
                    var parts = folderName.Split('/');
                    branch = parts[0];
                    versionSegment = parts.Length > 1 ? parts[1] : "latest";
                }
                else if (folderName.Contains("-"))
                {
                    // Legacy dash format: release-v5 or release-5
                    var parts = folderName.Split('-', 2);
                    branch = parts[0];
                    versionSegment = parts.Length > 1 ? parts[1] : "latest";
                    
                    // Strip 'v' prefix if present
                    if (versionSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    {
                        versionSegment = versionSegment.Substring(1);
                    }
                }
                else
                {
                    // Unknown format - skip to be safe (could be new structure subfolder)
                    Logger.Info("Migrate", $"Skipping {folderName} - unknown format, may be new structure");
                    continue;
                }

                // Normalize branch name
                branch = NormalizeVersionType(branch);

                // Create target path in new structure: instance/release/5
                var targetBranch = Path.Combine(newInstanceRoot, branch);
                var targetVersion = Path.Combine(targetBranch, versionSegment);
                
                // CRITICAL: Ensure we're not copying a folder into itself
                var normalizedLegacy = Path.GetFullPath(legacyDir).TrimEnd(Path.DirectorySeparatorChar);
                var normalizedTarget = Path.GetFullPath(targetVersion).TrimEnd(Path.DirectorySeparatorChar);
                if (normalizedLegacy.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                    normalizedTarget.StartsWith(normalizedLegacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    normalizedLegacy.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("Migrate", $"Skipping {folderName} - would cause recursive copy");
                    continue;
                }

                // Skip if already exists in new location
                if (Directory.Exists(targetVersion) && IsClientPresent(targetVersion))
                {
                    Logger.Info("Migrate", $"Skipping {folderName} - already exists at {targetVersion}");
                    continue;
                }

                Logger.Info("Migrate", $"Copying {folderName} -> {branch}/{versionSegment}");
                Directory.CreateDirectory(targetVersion);

                // Check if legacy has game/ subfolder or direct Client/ folder
                var legacyGameDir = Path.Combine(legacyDir, "game");
                var legacyClientDir = Path.Combine(legacyDir, "Client");
                
                if (Directory.Exists(legacyGameDir))
                {
                    // Legacy structure: release-v5/game/Client -> release/5/Client
                    foreach (var item in Directory.GetFileSystemEntries(legacyGameDir))
                    {
                        var name = Path.GetFileName(item);
                        var dest = Path.Combine(targetVersion, name);
                        
                        if (Directory.Exists(item))
                        {
                            SafeCopyDirectory(item, dest);
                        }
                        else if (File.Exists(item))
                        {
                            File.Copy(item, dest, overwrite: false);
                        }
                    }
                    Logger.Success("Migrate", $"Migrated {folderName} (from game/ subfolder)");
                }
                else if (Directory.Exists(legacyClientDir))
                {
                    // Direct Client/ folder structure
                    foreach (var item in Directory.GetFileSystemEntries(legacyDir))
                    {
                        var name = Path.GetFileName(item);
                        var dest = Path.Combine(targetVersion, name);
                        
                        if (Directory.Exists(item))
                        {
                            SafeCopyDirectory(item, dest);
                        }
                        else if (File.Exists(item))
                        {
                            File.Copy(item, dest, overwrite: false);
                        }
                    }
                    Logger.Success("Migrate", $"Migrated {folderName} (direct structure)");
                }
                else
                {
                    // Copy everything as-is
                    SafeCopyDirectory(legacyDir, targetVersion);
                    Logger.Success("Migrate", $"Migrated {folderName} (full copy)");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Migrate", $"Failed to migrate legacy instances: {ex.Message}");
        }
    }

    /// <summary>
    /// Restructure legacy folder format (release-v5) to new format (release/5) in-place.
    /// This is used when the instances folder is already in the correct location but has old naming.
    /// </summary>
    private void RestructureLegacyFoldersInPlace(string instanceRoot)
    {
        try
        {
            foreach (var legacyDir in Directory.GetDirectories(instanceRoot))
            {
                var folderName = Path.GetFileName(legacyDir);
                if (string.IsNullOrEmpty(folderName)) continue;
                
                // Skip folders that are already branch names (new structure)
                var normalizedFolderName = folderName.ToLowerInvariant();
                if (normalizedFolderName == "release" || normalizedFolderName == "pre-release" || 
                    normalizedFolderName == "prerelease" || normalizedFolderName == "latest")
                {
                    // This is already new structure, skip
                    continue;
                }
                
                // Only process legacy dash format: release-v5 or release-5
                if (!folderName.Contains("-"))
                {
                    continue;
                }
                
                var parts = folderName.Split('-', 2);
                var branch = parts[0];
                var versionSegment = parts.Length > 1 ? parts[1] : "latest";
                
                // Strip 'v' prefix if present
                if (versionSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    versionSegment = versionSegment.Substring(1);
                }
                
                // Normalize branch name
                branch = NormalizeVersionType(branch);
                
                // Create target path in new structure: instances/release/5
                var targetBranch = Path.Combine(instanceRoot, branch);
                var targetVersion = Path.Combine(targetBranch, versionSegment);
                
                // Skip if target already exists
                if (Directory.Exists(targetVersion))
                {
                    Logger.Info("Migrate", $"Skipping {folderName} - target {branch}/{versionSegment} already exists");
                    continue;
                }
                
                Logger.Info("Migrate", $"Restructuring {folderName} -> {branch}/{versionSegment}");
                
                // Create the branch directory
                Directory.CreateDirectory(targetBranch);
                
                // Check if legacy has game/ subfolder - if so, move contents up
                var legacyGameDir = Path.Combine(legacyDir, "game");
                
                if (Directory.Exists(legacyGameDir))
                {
                    // Legacy structure: release-v5/game/Client -> release/5/Client
                    // Move the contents of game/ to the new version folder
                    Directory.CreateDirectory(targetVersion);
                    
                    foreach (var item in Directory.GetFileSystemEntries(legacyGameDir))
                    {
                        var name = Path.GetFileName(item);
                        var dest = Path.Combine(targetVersion, name);
                        
                        if (Directory.Exists(item))
                        {
                            Directory.Move(item, dest);
                        }
                        else if (File.Exists(item))
                        {
                            File.Move(item, dest);
                        }
                    }
                    
                    // Clean up old structure
                    try
                    {
                        Directory.Delete(legacyDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Migrate", $"Could not delete old folder {legacyDir}: {ex.Message}");
                    }
                    
                    Logger.Success("Migrate", $"Restructured {folderName} (from game/ subfolder)");
                }
                else
                {
                    // Direct structure - just rename the folder
                    try
                    {
                        Directory.Move(legacyDir, targetVersion);
                        Logger.Success("Migrate", $"Restructured {folderName} (direct rename)");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Migrate", $"Failed to rename {folderName}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Migrate", $"Failed to restructure legacy folders: {ex.Message}");
        }
    }

    private string GetLatestInstancePath(string branch)
    {
        return Path.Combine(GetBranchPath(branch), "latest");
    }

    private string GetLatestInfoPath(string branch)
    {
        return Path.Combine(GetLatestInstancePath(branch), "latest.json");
    }

    private LatestInstanceInfo? LoadLatestInfo(string branch)
    {
        try
        {
            var path = GetLatestInfoPath(branch);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LatestInstanceInfo>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveLatestInfo(string branch, int version)
    {
        try
        {
            Directory.CreateDirectory(GetBranchPath(branch));
            var info = new LatestInstanceInfo { Version = version, UpdatedAt = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            File.WriteAllText(GetLatestInfoPath(branch), json);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Gets the sequence of patch versions to apply for a differential update.
    /// Returns list of versions from (currentVersion + 1) to targetVersion inclusive.
    /// </summary>
    private static List<int> GetPatchSequence(int currentVersion, int targetVersion)
    {
        var patches = new List<int>();
        for (int v = currentVersion + 1; v <= targetVersion; v++)
        {
            patches.Add(v);
        }
        return patches;
    }

    private string GetInstancePath(string branch, int version)
    {
        if (version == 0)
        {
            return GetLatestInstancePath(branch);
        }
        string normalizedBranch = NormalizeVersionType(branch);
        return Path.Combine(GetInstanceRoot(), normalizedBranch, version.ToString());
    }

    private string ResolveInstancePath(string branch, int version, bool preferExisting)
    {
        if (preferExisting)
        {
            var existing = FindExistingInstancePath(branch, version);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        return GetInstancePath(branch, version);
    }

    private async Task<(string branch, int version)> ResolveLatestCompositeAsync()
    {
        var releaseVersions = await GetVersionListAsync("release");
        var preVersions = await GetVersionListAsync("pre-release");
        int releaseLatest = releaseVersions.FirstOrDefault();
        int preLatest = preVersions.FirstOrDefault();

        // If both missing, default to release 0
        if (releaseLatest == 0 && preLatest == 0)
        {
            return ("release", 0);
        }

        // Prefer whichever has the higher version number; tie goes to pre-release
        if (preLatest >= releaseLatest)
        {
            return ("pre-release", preLatest);
        }

        return ("release", releaseLatest);
    }

    private Config LoadConfig()
    {
        Config config;
        
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
                
                // Migration: Ensure UUID exists
                if (string.IsNullOrEmpty(config.UUID))
                {
                    config.UUID = Guid.NewGuid().ToString();
                    config.Version = "2.0.0";
                    Logger.Info("Config", $"Migrated to v2.0.0, UUID: {config.UUID}");
                }
                
                // Migration: Migrate existing UUID to UserUuids mapping
                // This ensures existing users don't lose their skin when upgrading
                config.UserUuids ??= new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(config.UUID) && !string.IsNullOrEmpty(config.Nick))
                {
                    // Check if current nick already has a UUID mapping
                    var existingKey = config.UserUuids.Keys
                        .FirstOrDefault(k => k.Equals(config.Nick, StringComparison.OrdinalIgnoreCase));
                    
                    if (existingKey == null)
                    {
                        // No mapping exists for current nick - add the legacy UUID
                        config.UserUuids[config.Nick] = config.UUID;
                        Logger.Info("Config", $"Migrated existing UUID to UserUuids mapping for '{config.Nick}'");
                        SaveConfigInternal(config);
                    }
                }
                
                return config;
            }
            catch
            {
                config = new Config();
            }
        }
        else
        {
            config = new Config();
        }
        
        // New config - generate UUID
        if (string.IsNullOrEmpty(config.UUID))
        {
            config.UUID = Guid.NewGuid().ToString();
        }

        // Default nick to random name if empty or placeholder
        if (string.IsNullOrWhiteSpace(config.Nick) || config.Nick == "Player" || config.Nick == "Hyprism" || config.Nick == "HyPrism")
        {
            config.Nick = GenerateRandomUsername();
        }
        
        // Initialize UserUuids and add current user
        config.UserUuids ??= new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(config.Nick) && !string.IsNullOrEmpty(config.UUID))
        {
            config.UserUuids[config.Nick] = config.UUID;
        }

        // Migrate legacy "latest" branch to release
        if (config.VersionType == "latest")
        {
            config.VersionType = "release";
        }
        SaveConfigInternal(config);
        return config;
    }

    private void SaveConfigInternal(Config config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    public void SaveConfig()
    {
        SaveConfigInternal(_config);
    }
    
    /// <summary>
    /// Attempts to recover orphaned skin data on startup.
    /// This handles the scenario where:
    /// 1. User had skin saved with UUID A
    /// 2. Config was reset/recreated with UUID B
    /// 3. Old skin files still exist with UUID A
    /// 
    /// The method checks if:
    /// - The current UUID has NO skin data
    /// - There's an orphaned UUID with skin data
    /// If so, it either:
    /// - Adopts the orphaned UUID as the current user's UUID, OR
    /// - Copies the skin data from orphaned UUID to current UUID
    /// </summary>
    private void TryRecoverOrphanedSkinOnStartup()
    {
        try
        {
            var currentUuid = _config.UUID;
            if (string.IsNullOrEmpty(currentUuid) || string.IsNullOrEmpty(_config.Nick))
            {
                return;
            }
            
            // Get the current instance's UserData path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
            var avatarCacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
            
            // Check if current UUID already has skin data
            var currentSkinPath = Path.Combine(skinCacheDir, $"{currentUuid}.json");
            if (File.Exists(currentSkinPath))
            {
                // Current UUID has skin - no recovery needed
                return;
            }
            
            // No skin for current UUID - look for orphaned skins
            if (!Directory.Exists(skinCacheDir))
            {
                return;
            }
            
            // Get all existing UUIDs from UserUuids mapping
            var knownUuids = new HashSet<string>(
                (_config.UserUuids?.Values ?? Enumerable.Empty<string>())
                    .Concat(new[] { _config.UUID ?? "" })
                    .Where(u => !string.IsNullOrEmpty(u)),
                StringComparer.OrdinalIgnoreCase
            );
            
            // Scan for orphaned skin files
            var skinFiles = Directory.GetFiles(skinCacheDir, "*.json");
            string? orphanedUuid = null;
            DateTime latestTime = DateTime.MinValue;
            
            foreach (var file in skinFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(fileName, out var uuid))
                {
                    var uuidStr = uuid.ToString();
                    if (!knownUuids.Contains(uuidStr))
                    {
                        // This is an orphaned skin
                        var modTime = File.GetLastWriteTime(file);
                        if (modTime > latestTime)
                        {
                            latestTime = modTime;
                            orphanedUuid = uuidStr;
                        }
                    }
                }
            }
            
            if (orphanedUuid == null)
            {
                return; // No orphans found
            }
            
            Logger.Info("Startup", $"Found orphaned skin with UUID {orphanedUuid}");
            Logger.Info("Startup", $"Current user '{_config.Nick}' has no skin - recovering orphaned skin");
            
            // Strategy: Update the current user's UUID to match the orphaned skin
            // This is better than copying because it preserves the identity across server syncs
            _config.UserUuids ??= new Dictionary<string, string>();
            
            // Remove old mapping for current nick (case-insensitive)
            var existingKey = _config.UserUuids.Keys
                .FirstOrDefault(k => k.Equals(_config.Nick, StringComparison.OrdinalIgnoreCase));
            if (existingKey != null)
            {
                _config.UserUuids.Remove(existingKey);
            }
            
            // Set current user to use orphaned UUID
            _config.UserUuids[_config.Nick] = orphanedUuid;
            _config.UUID = orphanedUuid;
            SaveConfig();
            
            Logger.Success("Startup", $"Recovered orphaned skin! User '{_config.Nick}' now uses UUID {orphanedUuid}");
        }
        catch (Exception ex)
        {
            Logger.Warning("Startup", $"Failed to recover orphaned skins: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Generates a random username for new users.
    /// Format: Adjective + Noun + 4-digit number (max 16 chars total)
    /// </summary>
    private static string GenerateRandomUsername()
    {
        var random = new Random();
        
        // Short adjectives (max 5 chars)
        var adjectives = new[] { 
            "Happy", "Swift", "Brave", "Noble", "Quiet", "Bold", "Lucky", "Epic",
            "Jolly", "Lunar", "Solar", "Azure", "Royal", "Foxy", "Wacky", "Zesty",
            "Fizzy", "Dizzy", "Funky", "Jazzy", "Snowy", "Rainy", "Sunny", "Windy"
        };
        
        // Short nouns (max 6 chars)
        var nouns = new[] {
            "Panda", "Tiger", "Wolf", "Dragon", "Knight", "Ranger", "Mage", "Fox",
            "Bear", "Eagle", "Hawk", "Lion", "Falcon", "Raven", "Owl", "Shark",
            "Cobra", "Viper", "Lynx", "Badger", "Otter", "Pirate", "Ninja", "Viking"
        };
        
        var adj = adjectives[random.Next(adjectives.Length)];
        var noun = nouns[random.Next(nouns.Length)];
        var num = random.Next(1000, 9999);
        
        var name = $"{adj}{noun}{num}";
        // Safety truncate to 16 chars
        return name.Length <= 16 ? name : name.Substring(0, 16);
    }

    // Config
    public Config QueryConfig() => _config;

    public string GetNick() => _profileService.GetNick();
    
    public string GetUUID() => _profileService.GetUUID();
    
    /// <summary>
    /// Gets the avatar preview image as base64 data URL for displaying in the launcher.
    /// Returns null if no avatar preview exists.
    /// </summary>
    public string? GetAvatarPreview() => _profileService.GetAvatarPreview();
    
    /// <summary>
    /// Gets the avatar preview for a specific UUID.
    /// Checks profile folder first, then game cache, then persistent backup.
    /// </summary>
    public string? GetAvatarPreviewForUUID(string uuid) => _profileService.GetAvatarPreviewForUUID(uuid);

    public string GetCustomInstanceDir() => _config.InstanceDirectory ?? "";

    public bool SetUUID(string uuid) => _profileService.SetUUID(uuid);
    
    private bool SetUUIDInternal(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        if (!Guid.TryParse(uuid.Trim(), out var parsed)) return false;
        _config.UUID = parsed.ToString();
        SaveConfig();
        return true;
    }
    
    /// <summary>
    /// Clears the avatar cache for the current UUID.
    /// Call this when the user wants to reset their avatar.
    /// </summary>
    public bool ClearAvatarCache()
    {
        try
        {
            var uuid = GetCurrentUuid();
            if (string.IsNullOrWhiteSpace(uuid)) return false;
            
            // Clear persistent backup
            var persistentPath = Path.Combine(_appDir, "AvatarBackups", $"{uuid}.png");
            if (File.Exists(persistentPath))
            {
                File.Delete(persistentPath);
                Logger.Info("Avatar", $"Deleted persistent avatar for {uuid}");
            }
            
            // Clear game cache for all instances
            var instanceRoot = GetInstanceRoot();
            if (Directory.Exists(instanceRoot))
            {
                foreach (var branchDir in Directory.GetDirectories(instanceRoot))
                {
                    foreach (var versionDir in Directory.GetDirectories(branchDir))
                    {
                        var avatarPath = Path.Combine(versionDir, "UserData", "CachedAvatarPreviews", $"{uuid}.png");
                        if (File.Exists(avatarPath))
                        {
                            File.Delete(avatarPath);
                            Logger.Info("Avatar", $"Deleted cached avatar at {avatarPath}");
                        }
                    }
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Avatar", $"Failed to clear avatar cache: {ex.Message}");
            return false;
        }
    }
    
    public bool SetNick(string nick) => _profileService.SetNick(nick);
    
    private bool SetNickInternal(string nick)
    {
        // Validate nickname length (1-16 characters)
        var trimmed = nick?.Trim() ?? "";
        if (trimmed.Length < 1 || trimmed.Length > 16)
        {
            Logger.Warning("Config", $"Invalid nickname length: {trimmed.Length} (must be 1-16 chars)");
            return false;
        }
        _config.Nick = trimmed;
        SaveConfig();
        return true;
    }
    
    // ========== UUID Management (Username->UUID Mapping) ==========
    
    /// <summary>
    /// Gets or creates a UUID for a specific username.
    /// Uses case-insensitive lookup but preserves original username casing.
    /// This ensures each username consistently gets the same UUID across sessions.
    /// </summary>
    public string GetUuidForUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return _config.UUID; // Fallback to legacy single UUID
        }
        
        // Initialize UserUuids if null
        _config.UserUuids ??= new Dictionary<string, string>();
        
        // Case-insensitive lookup - find if any existing username matches
        var existingKey = _config.UserUuids.Keys
            .FirstOrDefault(k => k.Equals(username, StringComparison.OrdinalIgnoreCase));
        
        if (existingKey != null)
        {
            return _config.UserUuids[existingKey];
        }
        
        // No existing UUID for this username - before creating a new one,
        // check if there are orphaned skin files we should adopt.
        // This handles the case where config was reset but skin data still exists.
        var orphanedUuid = FindOrphanedSkinUuid();
        if (!string.IsNullOrEmpty(orphanedUuid))
        {
            Logger.Info("UUID", $"Recovered orphaned skin UUID for user '{username}': {orphanedUuid}");
            _config.UserUuids[username] = orphanedUuid;
            _config.UUID = orphanedUuid;
            SaveConfig();
            return orphanedUuid;
        }
        
        // No orphaned skins found - create a new UUID
        var newUuid = Guid.NewGuid().ToString();
        _config.UserUuids[username] = newUuid;
        
        // Also update the legacy UUID field for backwards compatibility
        _config.UUID = newUuid;
        
        SaveConfig();
        Logger.Info("UUID", $"Created new UUID for user '{username}': {newUuid}");
        
        return newUuid;
    }
    
    /// <summary>
    /// Finds an orphaned skin UUID from existing skin files in the game's UserData.
    /// Returns the UUID if exactly one orphaned skin is found, null otherwise.
    /// An "orphaned" skin is one whose UUID is not in the UserUuids mapping.
    /// </summary>
    private string? FindOrphanedSkinUuid()
    {
        try
        {
            // Get the current instance's UserData path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
            
            if (!Directory.Exists(skinCacheDir))
            {
                return null;
            }
            
            // Get all existing UUIDs from UserUuids mapping
            var knownUuids = new HashSet<string>(
                (_config.UserUuids?.Values ?? Enumerable.Empty<string>())
                    .Concat(new[] { _config.UUID ?? "" })
                    .Where(u => !string.IsNullOrEmpty(u)),
                StringComparer.OrdinalIgnoreCase
            );
            
            // Scan skin files for orphaned UUIDs
            var skinFiles = Directory.GetFiles(skinCacheDir, "*.json");
            var orphanedUuids = new List<string>();
            
            foreach (var file in skinFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                // Check if it looks like a UUID
                if (Guid.TryParse(fileName, out var uuid))
                {
                    var uuidStr = uuid.ToString();
                    // If this UUID is not in our known UUIDs, it's orphaned
                    if (!knownUuids.Contains(uuidStr))
                    {
                        orphanedUuids.Add(uuidStr);
                        Logger.Info("UUID", $"Found orphaned skin file: {fileName}.json");
                    }
                }
            }
            
            // If exactly one orphaned UUID found, we can safely adopt it
            // If multiple are found, we can't determine which is correct
            if (orphanedUuids.Count == 1)
            {
                return orphanedUuids[0];
            }
            else if (orphanedUuids.Count > 1)
            {
                // Multiple orphans - pick the most recently modified one
                string? mostRecent = null;
                DateTime latestTime = DateTime.MinValue;
                
                foreach (var orphanUuid in orphanedUuids)
                {
                    var skinPath = Path.Combine(skinCacheDir, $"{orphanUuid}.json");
                    if (File.Exists(skinPath))
                    {
                        var modTime = File.GetLastWriteTime(skinPath);
                        if (modTime > latestTime)
                        {
                            latestTime = modTime;
                            mostRecent = orphanUuid;
                        }
                    }
                }
                
                if (mostRecent != null)
                {
                    Logger.Info("UUID", $"Multiple orphaned skins found, using most recent: {mostRecent}");
                    return mostRecent;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning("UUID", $"Error scanning for orphaned skins: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Gets the UUID for the current user (based on Nick).
    /// </summary>
    public string GetCurrentUuid()
    {
        return GetUuidForUser(_config.Nick);
    }
    
    /// <summary>
    /// Gets all username->UUID mappings.
    /// Returns a list of objects with username, uuid, and isCurrent properties.
    /// </summary>
    public List<UuidMapping> GetAllUuidMappings()
    {
        _config.UserUuids ??= new Dictionary<string, string>();
        
        var currentNick = _config.Nick;
        return _config.UserUuids.Select(kvp => new UuidMapping
        {
            Username = kvp.Key,
            Uuid = kvp.Value,
            IsCurrent = kvp.Key.Equals(currentNick, StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }
    
    /// <summary>
    /// Sets a custom UUID for a specific username.
    /// </summary>
    public bool SetUuidForUser(string username, string uuid)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        if (!Guid.TryParse(uuid.Trim(), out var parsed)) return false;
        
        _config.UserUuids ??= new Dictionary<string, string>();
        
        // Remove any existing entry with same username (case-insensitive)
        var existingKey = _config.UserUuids.Keys
            .FirstOrDefault(k => k.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (existingKey != null)
        {
            _config.UserUuids.Remove(existingKey);
        }
        
        _config.UserUuids[username] = parsed.ToString();
        
        // Update legacy UUID if this is the current user
        if (username.Equals(_config.Nick, StringComparison.OrdinalIgnoreCase))
        {
            _config.UUID = parsed.ToString();
        }
        
        SaveConfig();
        Logger.Info("UUID", $"Set custom UUID for user '{username}': {parsed}");
        return true;
    }
    
    /// <summary>
    /// Deletes the UUID mapping for a specific username.
    /// Cannot delete the UUID for the current user.
    /// </summary>
    public bool DeleteUuidForUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        
        // Don't allow deleting current user's UUID
        if (username.Equals(_config.Nick, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warning("UUID", $"Cannot delete UUID for current user '{username}'");
            return false;
        }
        
        _config.UserUuids ??= new Dictionary<string, string>();
        
        var existingKey = _config.UserUuids.Keys
            .FirstOrDefault(k => k.Equals(username, StringComparison.OrdinalIgnoreCase));
        
        if (existingKey != null)
        {
            _config.UserUuids.Remove(existingKey);
            SaveConfig();
            Logger.Info("UUID", $"Deleted UUID for user '{username}'");
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Generates a new random UUID for the current user.
    /// Warning: This will change the player's identity and they will lose their skin!
    /// </summary>
    public string ResetCurrentUserUuid()
    {
        var newUuid = Guid.NewGuid().ToString();
        _config.UserUuids ??= new Dictionary<string, string>();
        
        // Remove old entry (case-insensitive)
        var existingKey = _config.UserUuids.Keys
            .FirstOrDefault(k => k.Equals(_config.Nick, StringComparison.OrdinalIgnoreCase));
        if (existingKey != null)
        {
            _config.UserUuids.Remove(existingKey);
        }
        
        _config.UserUuids[_config.Nick] = newUuid;
        _config.UUID = newUuid;
        
        SaveConfig();
        Logger.Info("UUID", $"Reset UUID for current user '{_config.Nick}': {newUuid}");
        return newUuid;
    }
    
    /// <summary>
    /// Switches to an existing username (and its UUID).
    /// Returns the UUID for the username.
    /// </summary>
    public string? SwitchToUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        
        _config.UserUuids ??= new Dictionary<string, string>();
        
        // Find the username (case-insensitive)
        var existingKey = _config.UserUuids.Keys
            .FirstOrDefault(k => k.Equals(username, StringComparison.OrdinalIgnoreCase));
        
        if (existingKey != null)
        {
            // Switch to existing username with its UUID
            _config.Nick = existingKey; // Use original casing
            _config.UUID = _config.UserUuids[existingKey];
            SaveConfig();
            Logger.Info("UUID", $"Switched to existing user '{existingKey}' with UUID {_config.UUID}");
            return _config.UUID;
        }
        
        // Username doesn't exist in mappings - create new entry
        var newUuid = Guid.NewGuid().ToString();
        _config.Nick = username;
        _config.UUID = newUuid;
        _config.UserUuids[username] = newUuid;
        SaveConfig();
        Logger.Info("UUID", $"Created new user '{username}' with UUID {newUuid}");
        return newUuid;
    }
    
    /// <summary>
    /// Attempts to recover orphaned skin data and associate it with the current user.
    /// This is useful when a user's config was reset but their skin data still exists.
    /// Returns true if skin data was recovered, false otherwise.
    /// </summary>
    public bool RecoverOrphanedSkinData()
    {
        try
        {
            var currentUuid = GetCurrentUuid();
            var orphanedUuid = FindOrphanedSkinUuid();
            
            if (string.IsNullOrEmpty(orphanedUuid))
            {
                Logger.Info("UUID", "No orphaned skin data found to recover");
                return false;
            }
            
            // If the current UUID already has a skin, don't overwrite
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
            var avatarCacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
            
            var currentSkinPath = Path.Combine(skinCacheDir, $"{currentUuid}.json");
            
            // If current user already has a skin, ask them to use "switch to orphan" instead
            if (File.Exists(currentSkinPath))
            {
                Logger.Info("UUID", $"Current user already has skin data. Use SetUuidForUser to switch to the orphaned UUID: {orphanedUuid}");
                return false;
            }
            
            // Copy orphaned skin to current UUID
            var orphanSkinPath = Path.Combine(skinCacheDir, $"{orphanedUuid}.json");
            if (File.Exists(orphanSkinPath))
            {
                Directory.CreateDirectory(skinCacheDir);
                File.Copy(orphanSkinPath, currentSkinPath, true);
                Logger.Success("UUID", $"Copied orphaned skin from {orphanedUuid} to {currentUuid}");
            }
            
            // Copy orphaned avatar to current UUID
            var orphanAvatarPath = Path.Combine(avatarCacheDir, $"{orphanedUuid}.png");
            var currentAvatarPath = Path.Combine(avatarCacheDir, $"{currentUuid}.png");
            if (File.Exists(orphanAvatarPath))
            {
                Directory.CreateDirectory(avatarCacheDir);
                File.Copy(orphanAvatarPath, currentAvatarPath, true);
                Logger.Success("UUID", $"Copied orphaned avatar from {orphanedUuid} to {currentUuid}");
            }
            
            // Also update the profile if one exists
            var profile = _config.Profiles?.FirstOrDefault(p => p.UUID == currentUuid);
            if (profile != null)
            {
                BackupProfileSkinData(currentUuid);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("UUID", $"Failed to recover orphaned skin data: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the UUID of any orphaned skin found in the game cache.
    /// Returns null if no orphaned skins are found.
    /// </summary>
    public string? GetOrphanedSkinUuid()
    {
        return FindOrphanedSkinUuid();
    }
    
    // ========== Profile Management ==========
    
    /// <summary>
    /// Gets all saved profiles, filtering out any with null/empty names.
    /// </summary>
    public List<Profile> GetProfiles()
    {
        // Clean up any null/empty profiles first
        if (_config.Profiles != null)
        {
            var validProfiles = _config.Profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.UUID))
                .ToList();
            
            if (validProfiles.Count != _config.Profiles.Count)
            {
                Logger.Info("Profile", $"Cleaned up {_config.Profiles.Count - validProfiles.Count} invalid profiles");
                _config.Profiles = validProfiles;
                SaveConfig();
            }
        }
        
        var profiles = _config.Profiles ?? new List<Profile>();
        Logger.Info("Profile", $"GetProfiles returning {profiles.Count} profiles");
        return profiles;
    }
    
    /// <summary>
    /// Gets the currently active profile index. -1 means no profile selected.
    /// </summary>
    public int GetActiveProfileIndex()
    {
        return _config.ActiveProfileIndex;
    }
    
    /// <summary>
    /// Creates a new profile with the given name and UUID.
    /// Returns the created profile.
    /// </summary>
    public Profile? CreateProfile(string name, string uuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uuid))
            {
                Logger.Warning("Profile", $"Cannot create profile with empty name or UUID");
                return null;
            }
            
            // Validate name length (1-16 characters)
            var trimmedName = name.Trim();
            if (trimmedName.Length < 1 || trimmedName.Length > 16)
            {
                Logger.Warning("Profile", $"Invalid name length: {trimmedName.Length} (must be 1-16 chars)");
                return null;
            }
            
            // Validate UUID format
            if (!Guid.TryParse(uuid.Trim(), out var parsedUuid))
            {
                Logger.Warning("Profile", $"Invalid UUID format: {uuid}");
                return null;
            }
            
            var profile = new Profile
            {
                Id = Guid.NewGuid().ToString(),
                UUID = parsedUuid.ToString(),
                Name = trimmedName,
                CreatedAt = DateTime.UtcNow
            };
            
            _config.Profiles ??= new List<Profile>();
            _config.Profiles.Add(profile);
            Logger.Info("Profile", $"Profile added to list. Total profiles: {_config.Profiles.Count}");
            SaveConfig();
            Logger.Info("Profile", $"Config saved to disk");
            
            // Save profile to disk folder
            SaveProfileToDisk(profile);
            
            Logger.Success("Profile", $"Created profile '{trimmedName}' with UUID {parsedUuid}");
            return profile;
        }
        catch (Exception ex)
        {
            Logger.Error("Profile", $"Failed to create profile: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Deletes a profile by its ID.
    /// Returns true if successful.
    /// </summary>
    public bool DeleteProfile(string profileId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }
            
            var index = _config.Profiles?.FindIndex(p => p.Id == profileId) ?? -1;
            if (index < 0)
            {
                return false;
            }
            
            var profile = _config.Profiles![index];
            _config.Profiles.RemoveAt(index);
            
            // Adjust active profile index if needed
            if (_config.ActiveProfileIndex == index)
            {
                _config.ActiveProfileIndex = -1;
            }
            else if (_config.ActiveProfileIndex > index)
            {
                _config.ActiveProfileIndex--;
            }
            
            SaveConfig();
            
            // Delete profile folder from disk (pass name for name-based folder)
            DeleteProfileFromDisk(profileId, profile.Name);
            
            Logger.Success("Profile", $"Deleted profile '{profile.Name}'");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Profile", $"Failed to delete profile: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Switches to a profile by its index.
    /// Returns true if successful.
    /// </summary>
    public bool SwitchProfile(int index)
    {
        try
        {
            if (_config.Profiles == null || index < 0 || index >= _config.Profiles.Count)
            {
                return false;
            }
            
            // First, backup current profile's skin data before switching
            var currentUuid = GetCurrentUuid();
            if (!string.IsNullOrWhiteSpace(currentUuid))
            {
                BackupProfileSkinData(currentUuid);
            }
            
            var profile = _config.Profiles[index];
            
            // Restore the new profile's skin data
            RestoreProfileSkinData(profile);
            
            // Update current UUID and Nick - also update UserUuids mapping
            _config.UUID = profile.UUID;
            _config.Nick = profile.Name;
            _config.ActiveProfileIndex = index;
            
            // Ensure the profile's UUID is in the UserUuids mapping
            _config.UserUuids ??= new Dictionary<string, string>();
            _config.UserUuids[profile.Name] = profile.UUID;
            
            // Switch mods symlink to the new profile's mods folder
            SwitchProfileModsSymlink(profile);
            
            SaveConfig();
            
            Logger.Success("Profile", $"Switched to profile '{profile.Name}'");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Profile", $"Failed to switch profile: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the path to a profile's mods folder.
    /// </summary>
    private string GetProfileModsFolder(Profile profile)
    {
        var profilesDir = GetProfilesFolder();
        var safeName = SanitizeFileName(profile.Name);
        var profileDir = Path.Combine(profilesDir, safeName);
        var modsDir = Path.Combine(profileDir, "Mods");
        Directory.CreateDirectory(modsDir);
        return modsDir;
    }
    
    /// <summary>
    /// Gets the path to a profile's mods folder by name.
    /// </summary>
    private string GetProfileModsFolderByName(string profileName)
    {
        var profilesDir = GetProfilesFolder();
        var safeName = SanitizeFileName(profileName);
        var profileDir = Path.Combine(profilesDir, safeName);
        var modsDir = Path.Combine(profileDir, "Mods");
        Directory.CreateDirectory(modsDir);
        return modsDir;
    }
    
    /// <summary>
    /// Switches the mods symlink to point to the new profile's mods folder.
    /// On Windows, creates a directory junction. On Unix, creates a symlink.
    /// </summary>
    private void SwitchProfileModsSymlink(Profile profile)
    {
        try
        {
            // Get the game's UserData/Mods path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = Path.Combine(versionPath, "UserData");
            var gameModsPath = Path.Combine(userDataPath, "Mods");
            
            // Get the profile's mods folder
            var profileModsPath = GetProfileModsFolder(profile);
            
            // If the game mods path exists and is not a symlink, migrate existing mods
            if (Directory.Exists(gameModsPath))
            {
                var dirInfo = new DirectoryInfo(gameModsPath);
                bool isSymlink = dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
                
                if (!isSymlink)
                {
                    // Real directory - migrate mods to profile folder then delete
                    Logger.Info("Mods", "Migrating existing mods to profile folder...");
                    
                    foreach (var file in Directory.GetFiles(gameModsPath))
                    {
                        var destFile = Path.Combine(profileModsPath, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Copy(file, destFile);
                        }
                    }
                    
                    // Also copy manifest.json if it exists
                    var manifestPath = Path.Combine(gameModsPath, "manifest.json");
                    var destManifest = Path.Combine(profileModsPath, "manifest.json");
                    if (File.Exists(manifestPath) && !File.Exists(destManifest))
                    {
                        File.Copy(manifestPath, destManifest);
                    }
                    
                    // Delete the original directory
                    Directory.Delete(gameModsPath, true);
                    Logger.Success("Mods", $"Migrated mods from game folder to profile: {profile.Name}");
                }
                else
                {
                    // It's already a symlink - just delete it
                    Directory.Delete(gameModsPath, false);
                }
            }
            
            // Create the symlink/junction
            Directory.CreateDirectory(userDataPath);
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, create a directory junction (works without admin rights)
                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{gameModsPath}\" \"{profileModsPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                
                using var process = Process.Start(processInfo);
                process?.WaitForExit(5000);
                
                if (process?.ExitCode != 0)
                {
                    Logger.Warning("Mods", "Failed to create junction, falling back to directory copy");
                    // Fallback: just create the directory
                    Directory.CreateDirectory(gameModsPath);
                }
                else
                {
                    Logger.Success("Mods", $"Created junction: {gameModsPath} -> {profileModsPath}");
                }
            }
            else
            {
                // On Unix (macOS/Linux), create a symbolic link
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ln",
                    Arguments = $"-s \"{profileModsPath}\" \"{gameModsPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                
                using var process = Process.Start(processInfo);
                process?.WaitForExit(5000);
                
                if (process?.ExitCode != 0)
                {
                    Logger.Warning("Mods", "Failed to create symlink, falling back to directory copy");
                    Directory.CreateDirectory(gameModsPath);
                }
                else
                {
                    Logger.Success("Mods", $"Created symlink: {gameModsPath} -> {profileModsPath}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Mods", $"Failed to switch profile mods symlink: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Initializes the profile mods symlink on startup if an active profile exists.
    /// </summary>
    private void InitializeProfileModsSymlink()
    {
        try
        {
            if (_config.ActiveProfileIndex < 0 || _config.Profiles == null || 
                _config.ActiveProfileIndex >= _config.Profiles.Count)
            {
                Logger.Info("Mods", "No active profile, skipping symlink initialization");
                return;
            }
            
            var profile = _config.Profiles[_config.ActiveProfileIndex];
            
            // Check if the game instance folder exists
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = Path.Combine(versionPath, "UserData");
            var gameModsPath = Path.Combine(userDataPath, "Mods");
            
            // Check if symlink already exists and points to correct profile
            if (Directory.Exists(gameModsPath))
            {
                var dirInfo = new DirectoryInfo(gameModsPath);
                bool isSymlink = dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
                
                if (isSymlink)
                {
                    // Symlink exists, verify it points to correct profile
                    var profileModsPath = GetProfileModsFolder(profile);
                    
                    // Get symlink target
                    string? targetPath = null;
                    try
                    {
                        targetPath = dirInfo.ResolveLinkTarget(true)?.FullName;
                    }
                    catch { /* Ignore errors getting target */ }
                    
                    if (targetPath != null && Path.GetFullPath(targetPath) == Path.GetFullPath(profileModsPath))
                    {
                        Logger.Info("Mods", $"Mods symlink already points to active profile: {profile.Name}");
                        return;
                    }
                    
                    // Wrong target, recreate
                    Logger.Info("Mods", "Mods symlink points to wrong profile, updating...");
                }
            }
            
            // Create/update symlink
            SwitchProfileModsSymlink(profile);
        }
        catch (Exception ex)
        {
            Logger.Warning("Mods", $"Failed to initialize profile mods symlink: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates an existing profile.
    /// </summary>
    public bool UpdateProfile(string profileId, string? newName, string? newUuid)
    {
        try
        {
            var profile = _config.Profiles?.FirstOrDefault(p => p.Id == profileId);
            if (profile == null)
            {
                return false;
            }
            
            if (!string.IsNullOrWhiteSpace(newName))
            {
                profile.Name = newName.Trim();
            }
            
            if (!string.IsNullOrWhiteSpace(newUuid) && Guid.TryParse(newUuid.Trim(), out var parsedUuid))
            {
                profile.UUID = parsedUuid.ToString();
            }
            
            // If this is the active profile, also update current UUID/Nick
            var index = _config.Profiles!.FindIndex(p => p.Id == profileId);
            if (index == _config.ActiveProfileIndex)
            {
                _config.UUID = profile.UUID;
                _config.Nick = profile.Name;
            }
            
            SaveConfig();
            
            // Update profile on disk
            UpdateProfileOnDisk(profile);
            
            Logger.Success("Profile", $"Updated profile '{profile.Name}'");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Profile", $"Failed to update profile: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the path to the Profiles folder.
    /// </summary>
    private string GetProfilesFolder()
    {
        var profilesDir = Path.Combine(_appDir, "Profiles");
        Directory.CreateDirectory(profilesDir);
        return profilesDir;
    }
    
    /// <summary>
    /// Opens the current profile's folder in the file manager.
    /// </summary>
    public bool OpenCurrentProfileFolder()
    {
        try
        {
            if (_config.ActiveProfileIndex < 0 || _config.Profiles == null || 
                _config.ActiveProfileIndex >= _config.Profiles.Count)
            {
                Logger.Warning("Profile", "No active profile to open folder for");
                return false;
            }
            
            var profile = _config.Profiles[_config.ActiveProfileIndex];
            var safeName = SanitizeFileName(profile.Name);
            var profileDir = Path.Combine(GetProfilesFolder(), safeName);
            
            if (!Directory.Exists(profileDir))
            {
                Directory.CreateDirectory(profileDir);
            }
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"\"{profileDir}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{profileDir}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start("xdg-open", $"\"{profileDir}\"");
            }
            
            Logger.Success("Profile", $"Opened profile folder: {profileDir}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Profile", $"Failed to open profile folder: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Saves a profile to disk as a .sh file with name and UUID, plus avatar if available.
    /// </summary>
    private void SaveProfileToDisk(Profile profile)
    {
        try
        {
            var profilesDir = GetProfilesFolder();
            // Use profile name as folder name (sanitize for filesystem)
            var safeName = SanitizeFileName(profile.Name);
            var profileDir = Path.Combine(profilesDir, safeName);
            Directory.CreateDirectory(profileDir);
            
            // Create the Mods folder for this profile
            var modsDir = Path.Combine(profileDir, "Mods");
            Directory.CreateDirectory(modsDir);
            
            // Create the shell script with profile info
            var shPath = Path.Combine(profileDir, $"{profile.Name}.sh");
            var shContent = $@"#!/bin/bash
# HyPrism Profile - {profile.Name}
# Created: {profile.CreatedAt:yyyy-MM-dd HH:mm:ss}

export HYPRISM_PROFILE_NAME=""{profile.Name}""
export HYPRISM_PROFILE_UUID=""{profile.UUID}""
export HYPRISM_PROFILE_ID=""{profile.Id}""

# This file is auto-generated by HyPrism launcher
# You can source this file to use this profile's settings
";
            File.WriteAllText(shPath, shContent);
            
            // Copy skin and avatar from game cache to profile folder
            CopyProfileSkinData(profile.UUID, profileDir);
            
            Logger.Info("Profile", $"Saved profile to disk: {profileDir}");
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to save profile to disk: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Copies skin and avatar data from game cache to a profile folder.
    /// </summary>
    private void CopyProfileSkinData(string uuid, string profileDir)
    {
        try
        {
            // Get game UserData path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            
            // Copy skin JSON
            var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
            var skinPath = Path.Combine(skinCacheDir, $"{uuid}.json");
            if (File.Exists(skinPath))
            {
                var destPath = Path.Combine(profileDir, "skin.json");
                File.Copy(skinPath, destPath, true);
                Logger.Info("Profile", $"Copied skin for UUID {uuid}");
            }
            
            // Copy avatar PNG
            var avatarCacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
            var avatarPath = Path.Combine(avatarCacheDir, $"{uuid}.png");
            if (File.Exists(avatarPath))
            {
                var destPath = Path.Combine(profileDir, "avatar.png");
                File.Copy(avatarPath, destPath, true);
                Logger.Info("Profile", $"Copied avatar for UUID {uuid}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to copy skin data: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Copies the avatar for a given UUID to a profile folder (legacy, now uses CopyProfileSkinData).
    /// </summary>
    private void CopyProfileAvatar(string uuid, string profileDir)
    {
        try
        {
            // Check persistent backup first
            var persistentPath = Path.Combine(_appDir, "AvatarBackups", $"{uuid}.png");
            if (File.Exists(persistentPath))
            {
                var destPath = Path.Combine(profileDir, "avatar.png");
                File.Copy(persistentPath, destPath, true);
                Logger.Info("Profile", $"Copied avatar from backup for {uuid}");
                return;
            }
            
            // Check game cache
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            var cacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
            var avatarPath = Path.Combine(cacheDir, $"{uuid}.png");
            
            if (File.Exists(avatarPath))
            {
                var destPath = Path.Combine(profileDir, "avatar.png");
                File.Copy(avatarPath, destPath, true);
                Logger.Info("Profile", $"Copied avatar from cache for {uuid}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to copy avatar: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates a profile's disk files when it's modified.
    /// </summary>
    private void UpdateProfileOnDisk(Profile profile)
    {
        try
        {
            var profilesDir = GetProfilesFolder();
            var safeName = SanitizeFileName(profile.Name);
            var profileDir = Path.Combine(profilesDir, safeName);
            
            // Also check for old folder with ID and rename it
            var oldProfileDir = Path.Combine(profilesDir, profile.Id);
            if (Directory.Exists(oldProfileDir) && !Directory.Exists(profileDir))
            {
                Directory.Move(oldProfileDir, profileDir);
            }
            
            if (!Directory.Exists(profileDir))
            {
                SaveProfileToDisk(profile);
                return;
            }
            
            // Remove old .sh files
            foreach (var oldSh in Directory.GetFiles(profileDir, "*.sh"))
            {
                File.Delete(oldSh);
            }
            
            // Create new .sh file
            var shPath = Path.Combine(profileDir, $"{profile.Name}.sh");
            var shContent = $@"#!/bin/bash
# HyPrism Profile - {profile.Name}
# Created: {profile.CreatedAt:yyyy-MM-dd HH:mm:ss}
# Updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}

export HYPRISM_PROFILE_NAME=""{profile.Name}""
export HYPRISM_PROFILE_UUID=""{profile.UUID}""
export HYPRISM_PROFILE_ID=""{profile.Id}""

# This file is auto-generated by HyPrism launcher
# You can source this file to use this profile's settings
";
            File.WriteAllText(shPath, shContent);
            
            // Update avatar
            CopyProfileAvatar(profile.UUID, profileDir);
            
            Logger.Info("Profile", $"Updated profile on disk: {profileDir}");
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to update profile on disk: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Deletes a profile's disk folder.
    /// </summary>
    private void DeleteProfileFromDisk(string profileId, string? profileName = null)
    {
        try
        {
            var profilesDir = GetProfilesFolder();
            
            // Try to delete by name first if provided
            if (!string.IsNullOrEmpty(profileName))
            {
                var safeName = SanitizeFileName(profileName);
                var profileDirByName = Path.Combine(profilesDir, safeName);
                if (Directory.Exists(profileDirByName))
                {
                    Directory.Delete(profileDirByName, true);
                    Logger.Info("Profile", $"Deleted profile from disk: {profileDirByName}");
                    return;
                }
            }
            
            // Fallback to ID-based folder (for migration)
            var profileDir = Path.Combine(profilesDir, profileId);
            if (Directory.Exists(profileDir))
            {
                Directory.Delete(profileDir, true);
                Logger.Info("Profile", $"Deleted profile from disk: {profileDir}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to delete profile from disk: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Sanitizes a string to be safe for use as a filename.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "profile" : sanitized;
    }
    
    /// <summary>
    /// Backs up the skin data for a profile (from game cache to profile folder).
    /// </summary>
    private void BackupProfileSkinData(string uuid)
    {
        try
        {
            // Find the profile by UUID
            var profile = _config.Profiles?.FirstOrDefault(p => p.UUID == uuid);
            if (profile == null)
            {
                return;
            }
            
            var profilesDir = GetProfilesFolder();
            var safeName = SanitizeFileName(profile.Name);
            var profileDir = Path.Combine(profilesDir, safeName);
            Directory.CreateDirectory(profileDir);
            
            // Get game UserData path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            
            // Backup skin JSON
            var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
            var skinPath = Path.Combine(skinCacheDir, $"{uuid}.json");
            if (File.Exists(skinPath))
            {
                var destPath = Path.Combine(profileDir, "skin.json");
                var skinJson = File.ReadAllText(skinPath);
                File.Copy(skinPath, destPath, true);
                Logger.Info("Profile", $"Backed up skin for {profile.Name} ({skinJson.Length} bytes)");
            }
            else
            {
                Logger.Warning("Profile", $"No skin file found to backup for {profile.Name} at {skinPath}");
            }
            
            // Backup avatar preview
            var avatarCacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
            var avatarPath = Path.Combine(avatarCacheDir, $"{uuid}.png");
            if (File.Exists(avatarPath))
            {
                var destPath = Path.Combine(profileDir, "avatar.png");
                File.Copy(avatarPath, destPath, true);
                Logger.Info("Profile", $"Backed up avatar for {profile.Name}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to backup skin data: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Restores the skin data for a profile (from profile folder to game cache).
    /// </summary>
    private void RestoreProfileSkinData(Profile profile)
    {
        try
        {
            var profilesDir = GetProfilesFolder();
            var safeName = SanitizeFileName(profile.Name);
            var profileDir = Path.Combine(profilesDir, safeName);
            
            if (!Directory.Exists(profileDir))
            {
                Logger.Info("Profile", $"No profile folder to restore from for {profile.Name}");
                return;
            }
            
            // Get game UserData path
            var branch = NormalizeVersionType(_config.VersionType);
            var versionPath = ResolveInstancePath(branch, 0, preferExisting: true);
            var userDataPath = GetInstanceUserDataPath(versionPath);
            
            // Restore skin JSON
            var skinBackupPath = Path.Combine(profileDir, "skin.json");
            if (File.Exists(skinBackupPath))
            {
                var skinCacheDir = Path.Combine(userDataPath, "CachedPlayerSkins");
                Directory.CreateDirectory(skinCacheDir);
                var destPath = Path.Combine(skinCacheDir, $"{profile.UUID}.json");
                File.Copy(skinBackupPath, destPath, true);
                Logger.Success("Profile", $"Restored skin for {profile.Name}");
                
                // Also push the complete skin data to the auth server to ensure it's in sync
                // This is crucial because the auth server MERGES updates, so if it has stale data,
                // it will corrupt the skin. By sending the full skin.json, we ensure a fresh start.
                // Note: This happens in the background and doesn't block profile switching
                if (_config.OnlineMode && !string.IsNullOrWhiteSpace(_config.AuthDomain))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Read the complete skin JSON from the backup
                            var skinJson = await File.ReadAllTextAsync(skinBackupPath);
                            
                            using var httpClient = new HttpClient();
                            httpClient.Timeout = TimeSpan.FromSeconds(10);
                            
                            var content = new StringContent(skinJson, System.Text.Encoding.UTF8, "application/json");
                            
                            // Use the account-data endpoint which accepts skin updates
                            // The UUID in the path tells the server which profile to update
                            // AuthDomain is typically sessions.X.ws, we need account-data.X.ws
                            var baseDomain = _config.AuthDomain.Replace("sessions.", "");
                            var accountDataUrl = $"https://account-data.{baseDomain}/account-data/skin/{profile.UUID}";
                            Logger.Info("Profile", $"Syncing skin to: {accountDataUrl}");
                            
                            var response = await httpClient.PutAsync(accountDataUrl, content);
                            
                            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                            {
                                Logger.Success("Profile", $"Synced complete skin to auth server for {profile.Name}");
                            }
                            else
                            {
                                Logger.Warning("Profile", $"Failed to sync skin to auth server: {response.StatusCode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning("Profile", $"Failed to sync skin to auth server: {ex.Message}");
                        }
                    });
                }
            }
            
            // Restore avatar preview
            var avatarBackupPath = Path.Combine(profileDir, "avatar.png");
            if (File.Exists(avatarBackupPath))
            {
                var avatarCacheDir = Path.Combine(userDataPath, "CachedAvatarPreviews");
                Directory.CreateDirectory(avatarCacheDir);
                var destPath = Path.Combine(avatarCacheDir, $"{profile.UUID}.png");
                File.Copy(avatarBackupPath, destPath, true);
                Logger.Success("Profile", $"Restored avatar for {profile.Name}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Profile", $"Failed to restore skin data: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Starts monitoring the skin file for the given profile to prevent overwrites during gameplay.
    /// If the game tries to overwrite the skin with data from the server, we restore it immediately.
    /// </summary>
    private void StartSkinProtection(Profile profile, string skinCachePath)
    {
        try
        {
            StopSkinProtection(); // Clean up any existing watcher
            
            if (!File.Exists(skinCachePath))
            {
                Logger.Warning("SkinProtection", $"Skin file doesn't exist, cannot protect: {skinCachePath}");
                return;
            }
            
            // Store the original skin content
            lock (_skinProtectionLock)
            {
                _protectedSkinPath = skinCachePath;
                _protectedSkinContent = File.ReadAllText(skinCachePath);
                _skinProtectionEnabled = true;
            }
            
            // Set file to READ-ONLY to prevent game from overwriting it
            // This is more reliable than FileSystemWatcher because the game will fail to write
            try
            {
                var fileInfo = new FileInfo(skinCachePath);
                fileInfo.IsReadOnly = true;
                Logger.Success("SkinProtection", $"Set skin file to READ-ONLY to prevent overwrites");
            }
            catch (Exception ex)
            {
                Logger.Warning("SkinProtection", $"Failed to set read-only: {ex.Message}");
            }
            
            var directory = Path.GetDirectoryName(skinCachePath);
            var filename = Path.GetFileName(skinCachePath);
            
            if (string.IsNullOrEmpty(directory))
            {
                Logger.Warning("SkinProtection", "Invalid skin path");
                return;
            }
            
            _skinWatcher = new FileSystemWatcher(directory, filename)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            
            _skinWatcher.Changed += OnSkinFileChanged;
            _skinWatcher.Created += OnSkinFileChanged;
            
            Logger.Success("SkinProtection", $"Started protecting skin file for {profile.Name}");
        }
        catch (Exception ex)
        {
            Logger.Warning("SkinProtection", $"Failed to start skin protection: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Handles skin file changes - restores the protected content if it was overwritten.
    /// </summary>
    private void OnSkinFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_skinProtectionLock)
        {
            if (!_skinProtectionEnabled || string.IsNullOrEmpty(_protectedSkinPath) || string.IsNullOrEmpty(_protectedSkinContent))
                return;
            
            try
            {
                // Small delay to let the file write complete
                Thread.Sleep(100);
                
                // Read current content
                var currentContent = File.ReadAllText(_protectedSkinPath);
                
                // Compare - if different, the game overwrote our skin
                if (currentContent != _protectedSkinContent)
                {
                    Logger.Warning("SkinProtection", "Detected skin overwrite - restoring protected skin!");
                    
                    // Temporarily disable watcher to avoid triggering ourselves
                    _skinProtectionEnabled = false;
                    
                    // Restore the protected content
                    File.WriteAllText(_protectedSkinPath, _protectedSkinContent);
                    
                    // Re-enable protection
                    _skinProtectionEnabled = true;
                    
                    Logger.Success("SkinProtection", "Skin restored successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("SkinProtection", $"Failed to check/restore skin: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Stops the skin file watcher.
    /// </summary>
    private void StopSkinProtection()
    {
        try
        {
            string? pathToUnprotect = null;
            lock (_skinProtectionLock)
            {
                pathToUnprotect = _protectedSkinPath;
                _skinProtectionEnabled = false;
                _protectedSkinPath = null;
                _protectedSkinContent = null;
            }
            
            // Remove READ-ONLY flag so file can be modified again
            if (!string.IsNullOrEmpty(pathToUnprotect) && File.Exists(pathToUnprotect))
            {
                try
                {
                    var fileInfo = new FileInfo(pathToUnprotect);
                    fileInfo.IsReadOnly = false;
                    Logger.Info("SkinProtection", "Removed READ-ONLY flag from skin file");
                }
                catch (Exception ex)
                {
                    Logger.Warning("SkinProtection", $"Failed to remove read-only: {ex.Message}");
                }
            }
            
            if (_skinWatcher != null)
            {
                _skinWatcher.EnableRaisingEvents = false;
                _skinWatcher.Changed -= OnSkinFileChanged;
                _skinWatcher.Created -= OnSkinFileChanged;
                _skinWatcher.Dispose();
                _skinWatcher = null;
                Logger.Info("SkinProtection", "Stopped skin protection");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("SkinProtection", $"Failed to stop skin protection: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves the current UUID/Nick as a new profile.
    /// Returns the created profile.
    /// </summary>
    public Profile? SaveCurrentAsProfile()
    {
        var uuid = GetCurrentUuid();
        var name = _config.Nick;
        
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        
        // Check if a profile with this UUID already exists
        var existing = _config.Profiles?.FirstOrDefault(p => p.UUID == uuid);
        if (existing != null)
        {
            // Update existing profile
            existing.Name = name;
            SaveConfig();
            UpdateProfileOnDisk(existing);
            return existing;
        }
        
        // Create new profile
        return CreateProfile(name, uuid);
    }

    public Task<string?> SetInstanceDirectoryAsync(string path)
    {
        try
        {
            // If path is empty or whitespace, clear the custom instance directory
            if (string.IsNullOrWhiteSpace(path))
            {
                _config.InstanceDirectory = null!;
                SaveConfig();
                Logger.Success("Config", "Instance directory cleared, using default");
                return Task.FromResult<string?>(null);
            }

            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());

            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.GetFullPath(Path.Combine(_appDir, expanded));
            }

            Directory.CreateDirectory(expanded);

            _config.InstanceDirectory = expanded;
            SaveConfig();

            Logger.Success("Config", $"Instance directory set to {expanded}");
            return Task.FromResult<string?>(expanded);
        }
        catch (Exception ex)
        {
            Logger.Error("Config", $"Failed to set instance directory: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    public string GetLauncherVersion()
    {
        // Unified launcher version for all branches
        return "2.0.3";
    }

    /// <summary>
    /// Check if Rosetta 2 is installed on macOS Apple Silicon.
    /// Returns null if not on macOS or if Rosetta is installed.
    /// Returns a warning object if Rosetta is needed but not installed.
    /// </summary>
    public RosettaStatus? CheckRosettaStatus()
    {
        // Only relevant on macOS
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        // Only relevant on Apple Silicon (ARM64)
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return null;
        }

        try
        {
            // Check if Rosetta is installed by checking for the runtime at /Library/Apple/usr/share/rosetta
            var rosettaPath = "/Library/Apple/usr/share/rosetta";
            if (Directory.Exists(rosettaPath))
            {
                Logger.Info("Rosetta", "Rosetta 2 is installed");
                return null; // Rosetta is installed, no warning needed
            }

            // Also try running arch -x86_64 to verify
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/arch",
                    Arguments = "-x86_64 /usr/bin/true",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process?.WaitForExit(5000);
                if (process?.ExitCode == 0)
                {
                    Logger.Info("Rosetta", "Rosetta 2 is installed (verified via arch command)");
                    return null;
                }
            }
            catch
            {
                // Ignore, proceed with warning
            }

            Logger.Warning("Rosetta", "Rosetta 2 is NOT installed - Hytale requires it to run on Apple Silicon");
            return new RosettaStatus
            {
                NeedsInstall = true,
                Message = "Rosetta 2 is required to run Hytale on Apple Silicon Macs.",
                Command = "softwareupdate --install-rosetta --agree-to-license",
                TutorialUrl = "https://www.youtube.com/watch?v=1W2vuSfnpXw"
            };
        }
        catch (Exception ex)
        {
            Logger.Warning("Rosetta", $"Failed to check Rosetta status: {ex.Message}");
            return null;
        }
    }

    // Version Management
    public string GetVersionType() => _config.VersionType;
    
    public bool SetVersionType(string versionType)
    {
        _config.VersionType = NormalizeVersionType(versionType);
        SaveConfig();
        return true;
    }

    // Returns list of available version numbers by checking Hytale's patch server
    // Uses caching to start from the last known version instead of version 1
    public async Task<List<int>> GetVersionListAsync(string branch) => await _versionService.GetVersionListAsync(branch);

    public bool SetSelectedVersion(int versionNumber)
    {
        _config.SelectedVersion = versionNumber;
        SaveConfig();
        return true;
    }

    public bool IsVersionInstalled(string branch, int versionNumber)
    {
        var normalizedBranch = NormalizeVersionType(branch);

        // Version 0 means "latest" - check if any version is installed
        if (versionNumber == 0)
        {
            var resolvedLatest = ResolveInstancePath(normalizedBranch, 0, preferExisting: true);
            bool hasClient = IsClientPresent(resolvedLatest);
            Logger.Info("Version", $"IsVersionInstalled check for version 0 (latest): path={resolvedLatest}, hasClient={hasClient}");
            return hasClient;
        }
        
        string versionPath = ResolveInstancePath(normalizedBranch, versionNumber, preferExisting: true);

        if (!IsClientPresent(versionPath))
        {
            // Last chance: try legacy dash naming in legacy roots
            var legacy = FindExistingInstancePath(normalizedBranch, versionNumber);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                Logger.Info("Version", $"IsVersionInstalled: found legacy layout at {legacy}");
                return IsClientPresent(legacy);
            }
            return false;
        }

        return true;
    }

    private bool IsClientPresent(string versionPath)
    {
        // Try multiple layouts: new layout (Client/...) and legacy layout (game/Client/...)
        var subfolders = new[] { "", "game" };

        foreach (var sub in subfolders)
        {
            string basePath = string.IsNullOrEmpty(sub) ? versionPath : Path.Combine(versionPath, sub);
            string clientPath;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                clientPath = Path.Combine(basePath, "Client", "Hytale.app", "Contents", "MacOS", "HytaleClient");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                clientPath = Path.Combine(basePath, "Client", "HytaleClient.exe");
            }
            else
            {
                clientPath = Path.Combine(basePath, "Client", "HytaleClient");
            }

            if (File.Exists(clientPath))
            {
                Logger.Info("Version", $"IsClientPresent: found at {clientPath}");
                return true;
            }
        }

        Logger.Info("Version", $"IsClientPresent: not found in {versionPath}");
        return false;
    }

    private bool AreAssetsPresent(string versionPath)
    {
        string assetsCheck;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetsCheck = Path.Combine(versionPath, "Client", "Hytale.app", "Contents", "Assets");
        }
        else
        {
            assetsCheck = Path.Combine(versionPath, "Client", "Assets");
        }

        bool exists = Directory.Exists(assetsCheck) && Directory.EnumerateFileSystemEntries(assetsCheck).Any();
        Logger.Info("Version", $"AreAssetsPresent: path={assetsCheck}, exists={exists}");
        return exists;
    }

    /// <summary>
    /// Checks if Assets.zip exists for the specified branch and version.
    /// Assets.zip is required for the skin customizer to work.
    /// </summary>
    public bool HasAssetsZip(string branch, int version)
    {
        var normalizedBranch = NormalizeVersionType(branch);
        var versionPath = ResolveInstancePath(normalizedBranch, version, preferExisting: true);
        return HasAssetsZipInternal(versionPath);
    }
    
    /// <summary>
    /// Gets the path to Assets.zip if it exists, or null if not found.
    /// </summary>
    public string? GetAssetsZipPath(string branch, int version)
    {
        var normalizedBranch = NormalizeVersionType(branch);
        var versionPath = ResolveInstancePath(normalizedBranch, version, preferExisting: true);
        var assetsZipPath = GetAssetsZipPathInternal(versionPath);
        return File.Exists(assetsZipPath) ? assetsZipPath : null;
    }
    
    private bool HasAssetsZipInternal(string versionPath)
    {
        var assetsZipPath = GetAssetsZipPathInternal(versionPath);
        bool exists = File.Exists(assetsZipPath);
        Logger.Info("Assets", $"HasAssetsZip: path={assetsZipPath}, exists={exists}");
        return exists;
    }
    
    private string GetAssetsZipPathInternal(string versionPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(versionPath, "Client", "Hytale.app", "Contents", "Assets.zip");
        }
        else
        {
            return Path.Combine(versionPath, "Client", "Assets.zip");
        }
    }
    
    // Cosmetic category file mappings (matching auth server structure)
    private static readonly Dictionary<string, string> CosmeticCategoryMap = new()
    {
        { "BodyCharacteristics.json", "bodyCharacteristic" },
        { "Capes.json", "cape" },
        { "EarAccessory.json", "earAccessory" },
        { "Ears.json", "ears" },
        { "Eyebrows.json", "eyebrows" },
        { "Eyes.json", "eyes" },
        { "Faces.json", "face" },
        { "FaceAccessory.json", "faceAccessory" },
        { "FacialHair.json", "facialHair" },
        { "Gloves.json", "gloves" },
        { "Haircuts.json", "haircut" },
        { "HeadAccessory.json", "headAccessory" },
        { "Mouths.json", "mouth" },
        { "Overpants.json", "overpants" },
        { "Overtops.json", "overtop" },
        { "Pants.json", "pants" },
        { "Shoes.json", "shoes" },
        { "SkinFeatures.json", "skinFeature" },
        { "Undertops.json", "undertop" },
        { "Underwear.json", "underwear" }
    };
    
    /// <summary>
    /// Gets the available cosmetics from the Assets.zip file for the specified instance.
    /// Returns a dictionary where keys are category names and values are lists of cosmetic IDs.
    /// </summary>
    public Dictionary<string, List<string>>? GetCosmeticsList(string branch, int version)
    {
        try
        {
            var normalizedBranch = NormalizeVersionType(branch);
            var versionPath = ResolveInstancePath(normalizedBranch, version, preferExisting: true);
            var assetsZipPath = GetAssetsZipPathInternal(versionPath);
            
            if (!File.Exists(assetsZipPath))
            {
                Logger.Warning("Cosmetics", $"Assets.zip not found: {assetsZipPath}");
                return null;
            }
            
            var cosmetics = new Dictionary<string, List<string>>();
            
            using var zip = ZipFile.OpenRead(assetsZipPath);
            
            foreach (var (fileName, categoryName) in CosmeticCategoryMap)
            {
                var entryPath = $"Cosmetics/CharacterCreator/{fileName}";
                var entry = zip.GetEntry(entryPath);
                
                if (entry == null)
                {
                    Logger.Info("Cosmetics", $"Entry not found: {entryPath}");
                    continue;
                }
                
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                
                var items = JsonSerializer.Deserialize<List<CosmeticItem>>(json, JsonOptions);
                if (items != null)
                {
                    var ids = items
                        .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                        .Select(item => item.Id!)
                        .ToList();
                    
                    if (ids.Count > 0)
                    {
                        cosmetics[categoryName] = ids;
                        Logger.Info("Cosmetics", $"Loaded {ids.Count} {categoryName} items");
                    }
                }
            }
            
            Logger.Success("Cosmetics", $"Loaded cosmetics from {assetsZipPath}: {cosmetics.Count} categories");
            return cosmetics;
        }
        catch (Exception ex)
        {
            Logger.Error("Cosmetics", $"Failed to load cosmetics: {ex.Message}");
            return null;
        }
    }

    public List<int> GetInstalledVersionsForBranch(string branch)
    {
        var normalizedBranch = NormalizeVersionType(branch);
        var result = new HashSet<int>();

        foreach (var root in GetInstanceRootsIncludingLegacy())
        {
            // New layout: branch/version
            string branchPath = Path.Combine(root, normalizedBranch);
            if (Directory.Exists(branchPath))
            {
                foreach (var dir in Directory.GetDirectories(branchPath))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, "latest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsClientPresent(dir))
                        {
                            result.Add(0);
                            Logger.Info("Version", $"Installed versions include latest for {normalizedBranch} at {dir}");
                        }
                        continue;
                    }

                    if (int.TryParse(name, out int version))
                    {
                        if (IsClientPresent(dir))
                        {
                            result.Add(version);
                            Logger.Info("Version", $"Installed version detected: {normalizedBranch}/{version} at {dir}");
                        }
                    }
                }
            }

            // Legacy dash layout: release-29 or release-v29
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.StartsWith(normalizedBranch + "-", StringComparison.OrdinalIgnoreCase)) continue;

                var suffix = name.Substring(normalizedBranch.Length + 1);
                
                // Remove 'v' prefix if present (e.g., "v5" -> "5")
                if (suffix.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    suffix = suffix.Substring(1);
                }

                if (string.Equals(suffix, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsClientPresent(dir))
                    {
                        result.Add(0);
                        Logger.Info("Version", $"Installed legacy latest detected: {name} at {dir}");
                    }
                    continue;
                }

                if (int.TryParse(suffix, out int version))
                {
                    if (IsClientPresent(dir))
                    {
                        result.Add(version);
                        Logger.Info("Version", $"Installed legacy version detected: {name} at {dir}");
                    }
                }
            }
        }
        
        return result.ToList();
    }

    public async Task<bool> CheckLatestNeedsUpdateAsync(string branch)
    {
        var normalizedBranch = NormalizeVersionType(branch);
        var versions = await GetVersionListAsync(normalizedBranch);
        if (versions.Count == 0) return false;

        var latest = versions[0];
        var latestPath = GetLatestInstancePath(normalizedBranch);
        var info = LoadLatestInfo(normalizedBranch);
        var baseOk = IsClientPresent(latestPath);
        if (!baseOk) return true;
        if (info == null)
        {
            // Game is installed but no version tracking - assume needs update to be safe
            // Don't write anything here - let the user decide via UPDATE button
            Logger.Info("Update", $"No latest.json found for {normalizedBranch}, assuming update may be needed");
            return true;
        }
        return info.Version != latest;
    }
    
    /// <summary>
    /// Forces the latest instance to update by resetting its version info.
    /// This will trigger a differential update on next launch.
    /// </summary>
    public async Task<bool> ForceUpdateLatestAsync(string branch)
    {
        try
        {
            var normalizedBranch = NormalizeVersionType(branch);
            var versions = await GetVersionListAsync(normalizedBranch);
            if (versions.Count == 0) return false;

            var latestPath = GetLatestInstancePath(normalizedBranch);
            var info = LoadLatestInfo(normalizedBranch);
            
            if (info == null)
            {
                // No version info, assume version 1 to force full update path
                SaveLatestInfo(normalizedBranch, 1);
                Logger.Info("Update", $"No version info found, set to v1 to force update");
            }
            else
            {
                // Set installed version to one less than latest to trigger update
                int latestVersion = versions[0];
                if (info.Version < latestVersion)
                {
                    // Already behind, just return true
                    Logger.Info("Update", $"Already needs update: v{info.Version} -> v{latestVersion}");
                    return true;
                }
                // If somehow at or ahead of latest, force update by going back one version
                int forcedVersion = Math.Max(1, latestVersion - 1);
                SaveLatestInfo(normalizedBranch, forcedVersion);
                Logger.Info("Update", $"Forced version to v{forcedVersion} to trigger update to v{latestVersion}");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Update", $"Failed to force update: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get information about the pending update, including old version details.
    /// Returns null if no update is pending.
    /// </summary>
    public async Task<UpdateInfo?> GetPendingUpdateInfoAsync(string branch)
    {
        try
        {
            var normalizedBranch = NormalizeVersionType(branch);
            var versions = await GetVersionListAsync(normalizedBranch);
            if (versions.Count == 0) return null;

            var latestVersion = versions[0];
            var latestPath = GetLatestInstancePath(normalizedBranch);
            var info = LoadLatestInfo(normalizedBranch);
            
            // Check if update is needed
            if (info == null || info.Version == latestVersion) return null;
            
            // Check if old version has userdata
            var oldUserDataPath = Path.Combine(latestPath, "UserData");
            var hasOldUserData = Directory.Exists(oldUserDataPath) && 
                                 Directory.GetFileSystemEntries(oldUserDataPath).Length > 0;
            
            return new UpdateInfo
            {
                OldVersion = info.Version,
                NewVersion = latestVersion,
                HasOldUserData = hasOldUserData,
                Branch = normalizedBranch
            };
        }
        catch (Exception ex)
        {
            Logger.Warning("Update", $"Failed to get pending update info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copy userdata from one version to another.
    /// </summary>
    public async Task<bool> CopyUserDataAsync(string branch, int fromVersion, int toVersion)
    {
        try
        {
            var normalizedBranch = NormalizeVersionType(branch);
            
            // Get source path (if fromVersion is 0, use latest)
            string fromPath;
            if (fromVersion == 0)
            {
                fromPath = GetLatestInstancePath(normalizedBranch);
            }
            else
            {
                fromPath = ResolveInstancePath(normalizedBranch, fromVersion, preferExisting: true);
            }
            
            // Get destination path (if toVersion is 0, use latest)
            string toPath;
            if (toVersion == 0)
            {
                toPath = GetLatestInstancePath(normalizedBranch);
            }
            else
            {
                toPath = ResolveInstancePath(normalizedBranch, toVersion, preferExisting: true);
            }
            
            var fromUserData = Path.Combine(fromPath, "UserData");
            var toUserData = Path.Combine(toPath, "UserData");
            
            if (!Directory.Exists(fromUserData))
            {
                Logger.Warning("UserData", $"Source UserData does not exist: {fromUserData}");
                return false;
            }
            
            // Create destination if needed
            Directory.CreateDirectory(toUserData);
            
            // Copy all contents
            await Task.Run(() => CopyDirectory(fromUserData, toUserData, true));
            
            Logger.Success("UserData", $"Copied UserData from v{fromVersion} to v{toVersion}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("UserData", $"Failed to copy userdata: {ex.Message}");
            return false;
        }
    }

    private void CopyDirectory(string sourceDir, string destDir, bool overwrite)
    {
        // Create destination directory
        Directory.CreateDirectory(destDir);
        
        // Copy files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);
        }
        
        // Copy subdirectories
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir, overwrite);
        }
    }

    // Game
    public async Task<DownloadProgress> DownloadAndLaunchAsync()
    {
        try
        {
            _downloadCts = new CancellationTokenSource();
            
            string branch = NormalizeVersionType(_config.VersionType);
            var versions = await GetVersionListAsync(branch);
            if (versions.Count == 0)
            {
                return new DownloadProgress { Error = "No versions available for this branch" };
            }

            bool isLatestInstance = _config.SelectedVersion == 0;
            int targetVersion = _config.SelectedVersion > 0 ? _config.SelectedVersion : versions[0];
            if (!versions.Contains(targetVersion))
            {
                targetVersion = versions[0];
            }

            string versionPath = ResolveInstancePath(branch, isLatestInstance ? 0 : targetVersion, preferExisting: true);
            Directory.CreateDirectory(versionPath);

            // Check if we need to download/install - verify all components
            // The game is installed if the Client executable exists - that's all we need to check
            bool gameIsInstalled = IsClientPresent(versionPath);
            
            Logger.Info("Download", $"=== INSTALL CHECK ===");
            Logger.Info("Download", $"Version path: {versionPath}");
            Logger.Info("Download", $"Is latest instance: {isLatestInstance}");
            Logger.Info("Download", $"Target version: {targetVersion}");
            Logger.Info("Download", $"Client exists (game installed): {gameIsInstalled}");
            
            // If game is already installed, check for updates then launch
            if (gameIsInstalled)
            {
                Logger.Success("Download", "Game is already installed");
                
                // Check if we need a differential update (only for latest instance)
                if (isLatestInstance)
                {
                    var info = LoadLatestInfo(branch);
                    int installedVersion = info?.Version ?? 0;
                    int latestVersion = versions[0];
                    
                    // If no latest.json exists, we need to determine the installed version
                    if (installedVersion == 0)
                    {
                        // First, check if there's a Butler receipt which indicates the game was installed via Butler
                        var receiptPath = Path.Combine(versionPath, ".itch", "receipt.json.gz");
                        bool hasButlerReceipt = File.Exists(receiptPath);
                        
                        if (hasButlerReceipt)
                        {
                            // Butler receipt exists - the game was installed/patched by Butler
                            // Check if we have any cached PWR files that indicate a version
                            var cacheDir = Path.Combine(_appDir, "cache");
                            if (Directory.Exists(cacheDir))
                            {
                                var pwrFiles = Directory.GetFiles(cacheDir, $"{branch}_patch_*.pwr")
                                    .Concat(Directory.GetFiles(cacheDir, $"{branch}_*.pwr"))
                                    .Select(f => Path.GetFileNameWithoutExtension(f))
                                    .SelectMany(n => {
                                        // Try to extract version from filename patterns like "release_patch_7" or "release_7"
                                        var parts = n.Split('_');
                                        var versions = new List<int>();
                                        foreach (var part in parts)
                                        {
                                            if (int.TryParse(part, out var v) && v > 0)
                                            {
                                                versions.Add(v);
                                            }
                                        }
                                        return versions;
                                    })
                                    .OrderByDescending(v => v)
                                    .ToList();
                                
                                if (pwrFiles.Count > 0)
                                {
                                    // The highest version in cache is likely the installed version
                                    installedVersion = pwrFiles[0];
                                    Logger.Info("Download", $"Detected installed version from cache: v{installedVersion}");
                                    // Save the detected version
                                    SaveLatestInfo(branch, installedVersion);
                                }
                            }
                            
                            // If still no version detected but receipt exists, don't assume anything
                            // User can click UPDATE button if they want to ensure they're on latest
                            if (installedVersion == 0)
                            {
                                // Game has Butler receipt but no version info - don't assume version
                                // Just launch as-is, user can click UPDATE if needed
                                Logger.Info("Download", $"Butler receipt exists but no version info, launching as-is (user can UPDATE manually)");
                            }
                        }
                        else
                        {
                            // No Butler receipt - this is a legacy installation or was installed differently
                            // Don't assume version, just launch as-is
                            Logger.Info("Download", $"No Butler receipt, launching current installation as-is (user can UPDATE manually)");
                        }
                        
                        // Only save if we actually detected a version from cache
                        // Don't assume latest - that breaks update detection
                    }
                    
                    Logger.Info("Download", $"Installed version: {installedVersion}, Latest version: {latestVersion}");
                    
                    // Only apply differential update if we're BEHIND the latest version
                    if (installedVersion > 0 && installedVersion < latestVersion)
                    {
                        Logger.Info("Download", $"Differential update available: {installedVersion} -> {latestVersion}");
                        SendProgress("update", 0, $"Updating game from v{installedVersion} to v{latestVersion}...", 0, 0);
                        
                        try
                        {
                            // Apply differential updates for each version step
                            var patchesToApply = GetPatchSequence(installedVersion, latestVersion);
                            Logger.Info("Download", $"Patches to apply: {string.Join(" -> ", patchesToApply)}");
                            
                            for (int i = 0; i < patchesToApply.Count; i++)
                            {
                                int patchVersion = patchesToApply[i];
                                ThrowIfCancelled();
                                
                                // Progress: each patch gets an equal share of 0-90%
                                int baseProgress = (i * 90) / patchesToApply.Count;
                                int progressPerPatch = 90 / patchesToApply.Count;
                                
                                SendProgress("update", baseProgress, $"Downloading patch {i + 1}/{patchesToApply.Count} (v{patchVersion})...", 0, 0);
                                
                                // Ensure Butler is installed
                                await _butlerService.EnsureButlerInstalledAsync((p, m) => { });
                                
                                // Download the PWR patch
                                var patchOs = GetOS();
                                var patchArch = GetArch();
                                var patchBranchType = NormalizeVersionType(branch);
                                string patchUrl = $"https://game-patches.hytale.com/patches/{patchOs}/{patchArch}/{patchBranchType}/0/{patchVersion}.pwr";
                                string patchPwrPath = Path.Combine(_appDir, "cache", $"{branch}_patch_{patchVersion}.pwr");
                                
                                Directory.CreateDirectory(Path.GetDirectoryName(patchPwrPath)!);
                                Logger.Info("Download", $"Downloading patch: {patchUrl}");
                                
                                // Check if patch file is very large (> 500MB) - might indicate wrong version detection
                                // In that case, we should fall back to the existing installation
                                try
                                {
                                    using var headRequest = new HttpRequestMessage(HttpMethod.Head, patchUrl);
                                    using var headResponse = await HttpClient.SendAsync(headRequest);
                                    
                                    if (!headResponse.IsSuccessStatusCode)
                                    {
                                        Logger.Warning("Download", $"Patch file not found at {patchUrl}, skipping differential update");
                                        throw new Exception("Patch file not available");
                                    }
                                    
                                    var contentLength = headResponse.Content.Headers.ContentLength ?? 0;
                                    Logger.Info("Download", $"Patch file size: {contentLength / 1024 / 1024} MB");
                                    
                                    // If patch is > 500MB, something is wrong - patches should be small
                                    if (contentLength > 500 * 1024 * 1024)
                                    {
                                        Logger.Warning("Download", $"Patch file is too large ({contentLength / 1024 / 1024} MB), likely wrong version detection");
                                        throw new Exception("Patch file unexpectedly large - version detection may be incorrect");
                                    }
                                }
                                catch (HttpRequestException)
                                {
                                    Logger.Warning("Download", $"Cannot check patch file at {patchUrl}, skipping differential update");
                                    throw new Exception("Cannot access patch file");
                                }
                                
                                await DownloadFileAsync(patchUrl, patchPwrPath, (progress, downloaded, total) =>
                                {
                                    int mappedProgress = baseProgress + (int)(progress * 0.5 * progressPerPatch / 100);
                                    SendProgress("update", mappedProgress, $"Downloading patch {i + 1}/{patchesToApply.Count}... {progress}%", downloaded, total);
                                }, _downloadCts.Token);
                                
                                ThrowIfCancelled();
                                
                                // Apply the patch using Butler (differential update)
                                int applyBaseProgress = baseProgress + (progressPerPatch / 2);
                                SendProgress("update", applyBaseProgress, $"Applying patch {i + 1}/{patchesToApply.Count}...", 0, 0);
                                
                                await _butlerService.ApplyPwrAsync(patchPwrPath, versionPath, (progress, message) =>
                                {
                                    int mappedProgress = applyBaseProgress + (int)(progress * 0.5 * progressPerPatch / 100);
                                    SendProgress("update", mappedProgress, message, 0, 0);
                                }, _downloadCts.Token);
                                
                                // Clean up patch file
                                if (File.Exists(patchPwrPath))
                                {
                                    try { File.Delete(patchPwrPath); } catch { }
                                }
                                
                                // Save progress after each patch
                                SaveLatestInfo(branch, patchVersion);
                                Logger.Success("Download", $"Patch {patchVersion} applied successfully");
                            }
                            
                            Logger.Success("Download", $"Differential update complete: now at v{latestVersion}");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Download", $"Differential update failed: {ex.Message}");
                            // Don't update latest.json - keep the old version so user can try UPDATE again
                            // Just launch the game as-is with whatever version is installed
                            Logger.Warning("Download", "Keeping current version, user can try UPDATE again later");
                        }
                    }
                    else if (installedVersion >= latestVersion)
                    {
                        Logger.Info("Download", "Already at latest version, no update needed");
                        // Ensure latest.json is correct
                        SaveLatestInfo(branch, latestVersion);
                    }
                }
                
                // Ensure VC++ Redistributable is installed on Windows before launching
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SendProgress("install", 94, "Checking Visual C++ Runtime...", 0, 0);
                    try
                    {
                        await EnsureVCRedistInstalledAsync((progress, message) =>
                        {
                            int mappedProgress = 94 + (int)(progress * 0.02);
                            SendProgress("install", mappedProgress, message, 0, 0);
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("VCRedist", $"VC++ install warning: {ex.Message}");
                        // Don't fail - continue anyway
                    }
                }
                
                // Just ensure JRE is available (download if needed, but don't touch the game)
                string jrePath = GetJavaPath();
                if (!File.Exists(jrePath))
                {
                    Logger.Info("Download", "JRE missing, installing...");
                    SendProgress("install", 96, "Installing Java Runtime...", 0, 0);
                    try
                    {
                        await EnsureJREInstalledAsync((progress, message) =>
                        {
                            int mappedProgress = 96 + (int)(progress * 0.03);
                            SendProgress("install", mappedProgress, message, 0, 0);
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("JRE", $"JRE install failed: {ex.Message}");
                        return new DownloadProgress { Error = $"Failed to install Java Runtime: {ex.Message}" };
                    }
                }
                
                SendProgress("complete", 100, "Launching game...", 0, 0);
                try
                {
                    await LaunchGameAsync(versionPath, branch);
                    return new DownloadProgress { Success = true, Progress = 100 };
                }
                catch (Exception ex)
                {
                    Logger.Error("Game", $"Launch failed: {ex.Message}");
                    SendErrorEvent("launch", "Failed to launch game", ex.ToString());
                    return new DownloadProgress { Error = $"Failed to launch game: {ex.Message}" };
                }
            }
            
            // Game is NOT installed - need to download
            Logger.Info("Download", "Game not installed, starting download...");

            SendProgress("download", 0, "Preparing download...", 0, 0);
            
            // First, ensure Butler is installed (0-5% progress)
            try
            {
                await _butlerService.EnsureButlerInstalledAsync((progress, message) =>
                {
                    // Map butler install progress to 0-5%
                    int mappedProgress = (int)(progress * 0.05);
                    SendProgress("download", mappedProgress, message, 0, 0);
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Download", $"Butler install failed: {ex.Message}");
                return new DownloadProgress { Error = $"Failed to install Butler: {ex.Message}" };
            }

            ThrowIfCancelled();
            
            // Download PWR file (5-70% progress)
            string osName = GetOS();
            string arch = GetArch();
            string apiVersionType = NormalizeVersionType(branch);
            string downloadUrl = $"https://game-patches.hytale.com/patches/{osName}/{arch}/{apiVersionType}/0/{targetVersion}.pwr";
            string pwrPath = Path.Combine(_appDir, "cache", $"{branch}_{(isLatestInstance ? "latest" : "version")}_{targetVersion}.pwr");
            
            Directory.CreateDirectory(Path.GetDirectoryName(pwrPath)!);
            
            Logger.Info("Download", $"Downloading: {downloadUrl}");
            
            await DownloadFileAsync(downloadUrl, pwrPath, (progress, downloaded, total) =>
            {
                // Map download progress to 5-65%
                int mappedProgress = 5 + (int)(progress * 0.60);
                SendProgress("download", mappedProgress, $"Downloading... {progress}%", downloaded, total);
            }, _downloadCts.Token);
            
            // Extract PWR file using Butler (65-85% progress)
            SendProgress("install", 65, "Installing game with Butler...", 0, 0);
            
            try
            {
                await _butlerService.ApplyPwrAsync(pwrPath, versionPath, (progress, message) =>
                {
                    // Map install progress (0-100) to 65-85%
                    int mappedProgress = 65 + (int)(progress * 0.20);
                    SendProgress("install", mappedProgress, message, 0, 0);
                }, _downloadCts.Token);
                
                // Clean up PWR file after successful extraction
                if (File.Exists(pwrPath))
                {
                    try { File.Delete(pwrPath); } catch { }
                }
                
                // Skip assets extraction on install to match legacy layout
                ThrowIfCancelled();
            }
            catch (OperationCanceledException)
            {
                // Re-throw cancellation to be handled by outer catch
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error("Download", $"PWR extraction failed: {ex.Message}");
                return new DownloadProgress { Error = $"Failed to install game: {ex.Message}" };
            }

            if (isLatestInstance)
            {
                SaveLatestInfo(branch, targetVersion);
            }
            
            SendProgress("complete", 95, "Download complete!", 0, 0);

            // Ensure VC++ Redistributable is installed on Windows before launching
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SendProgress("install", 95, "Checking Visual C++ Runtime...", 0, 0);
                try
                {
                    await EnsureVCRedistInstalledAsync((progress, message) =>
                    {
                        int mappedProgress = 95 + (int)(progress * 0.01);
                        SendProgress("install", mappedProgress, message, 0, 0);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning("VCRedist", $"VC++ install warning: {ex.Message}");
                    // Don't fail - continue anyway
                }
            }

            // Ensure JRE is installed before launching
            SendProgress("install", 96, "Checking Java Runtime...", 0, 0);
            try
            {
                await EnsureJREInstalledAsync((progress, message) =>
                {
                    int mappedProgress = 96 + (int)(progress * 0.03); // 96-99%
                    SendProgress("install", mappedProgress, message, 0, 0);
                });
            }
            catch (Exception ex)
            {
                Logger.Error("JRE", $"JRE install failed: {ex.Message}");
                return new DownloadProgress { Error = $"Failed to install Java Runtime: {ex.Message}" };
            }

            ThrowIfCancelled();

            SendProgress("complete", 100, "Launching game...", 0, 0);

            // Launch the game
            try
            {
                await LaunchGameAsync(versionPath, branch);
                return new DownloadProgress { Success = true, Progress = 100 };
            }
            catch (Exception ex)
            {
                Logger.Error("Game", $"Launch failed: {ex.Message}");
                SendErrorEvent("launch", "Failed to launch game", ex.ToString());
                return new DownloadProgress { Error = $"Failed to launch game: {ex.Message}" };
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("Download", "Download cancelled");
            try
            {
                SendProgress("cancelled", 0, "Cancelled", 0, 0);
            }
            catch { }
            return new DownloadProgress { Error = "Download cancelled" };
        }
        catch (Exception ex)
        {
            Logger.Error("Download", $"Error: {ex.Message}");
            return new DownloadProgress { Error = ex.Message };
        }
        finally
        {
            _downloadCts = null;
        }
    }

    public bool CancelDownload()
    {
        Logger.Info("Download", "CancelDownload called");
        if (_downloadCts != null)
        {
            Logger.Info("Download", "Cancelling download...");
            _downloadCts.Cancel();
            Logger.Info("Download", "Download cancellation requested");
            return true;
        }
        Logger.Warning("Download", "No download in progress to cancel");
        return false;
    }

    private void ThrowIfCancelled()
    {
        if (_downloadCts?.IsCancellationRequested == true)
        {
            throw new OperationCanceledException();
        }
    }

    private void SendProgress(string stage, int progress, string message, long downloaded, long total)
    {
        DownloadProgressChanged?.Invoke(stage, progress, message, downloaded, total);
        
        // Don't update Discord during download/install to avoid showing extraction messages
        // Only update on complete or idle
        if (stage == "complete")
        {
            _discordService.SetPresence(DiscordService.PresenceState.Idle);
        }
    }

    private async Task DownloadFileAsync(string url, string path, Action<int, long, long> progressCallback, CancellationToken cancellationToken = default)
    {
        await _downloadService.DownloadFileAsync(url, path, progressCallback, cancellationToken);
    }

    // Extract Assets.zip to the correct location for macOS
    private async Task ExtractAssetsIfNeededAsync(string versionPath, Action<int, string> progressCallback)
    {
        // Check if Assets.zip exists
        string assetsZip = Path.Combine(versionPath, "Assets.zip");
        if (!File.Exists(assetsZip))
        {
            Logger.Info("Assets", "No Assets.zip found, skipping extraction");
            progressCallback(100, "No assets extraction needed");
            return;
        }
        
        // Determine target path based on OS
        string assetsDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetsDir = Path.Combine(versionPath, "Client", "Hytale.app", "Contents", "Assets");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            assetsDir = Path.Combine(versionPath, "Client", "Assets");
        }
        else
        {
            assetsDir = Path.Combine(versionPath, "Client", "Assets");
        }
        
        // Check if already extracted
        if (Directory.Exists(assetsDir) && Directory.GetFiles(assetsDir, "*", SearchOption.AllDirectories).Length > 0)
        {
            Logger.Info("Assets", "Assets already extracted");
            progressCallback(100, "Assets ready");
            return;
        }
        
        Logger.Info("Assets", $"Extracting Assets.zip to {assetsDir}...");
        progressCallback(0, "Extracting game assets...");
        
        try
        {
            Directory.CreateDirectory(assetsDir);
            
            // Extract using ZipFile
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(assetsZip);
                var totalEntries = archive.Entries.Count;
                var extracted = 0;
                
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    
                    // Get relative path - Assets.zip may have "Assets/" prefix or not
                    var relativePath = entry.FullName;
                    if (relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = relativePath.Substring(7);
                    }
                    else if (relativePath.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = relativePath.Substring(7);
                    }
                    
                    var destPath = Path.Combine(assetsDir, relativePath);
                    var destDir = Path.GetDirectoryName(destPath);
                    
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    
                    entry.ExtractToFile(destPath, true);
                    extracted++;
                    
                    if (totalEntries > 0 && extracted % 100 == 0)
                    {
                        var progress = (int)((extracted * 100) / totalEntries);
                        progressCallback(progress, $"Extracting assets... {progress}%");
                    }
                }
            });
            
            // Optionally delete the zip after extraction to save space
            try { File.Delete(assetsZip); } catch { }
            
            // On macOS, create symlink at root level for game compatibility
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string rootAssetsLink = Path.Combine(versionPath, "Assets");
                
                try
                {
                    // Remove existing symlink/directory if it exists
                    if (Directory.Exists(rootAssetsLink) || File.Exists(rootAssetsLink))
                    {
                        try 
                        { 
                            // Check if it's a symlink
                            FileAttributes attrs = File.GetAttributes(rootAssetsLink);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                            {
                                // It's a symlink - delete it
                                File.Delete(rootAssetsLink);
                                Logger.Info("Assets", "Removed existing Assets symlink");
                            }
                            else if (Directory.Exists(rootAssetsLink))
                            {
                                // It's a real directory - delete it
                                Directory.Delete(rootAssetsLink, true);
                                Logger.Info("Assets", "Removed existing Assets directory");
                            }
                        } 
                        catch (Exception ex)
                        {
                            Logger.Warning("Assets", $"Could not remove existing Assets: {ex.Message}");
                        }
                    }
                    
                    // Use relative path for symlink so it works even if directory moves
                    string relativeAssetsPath = "Client/Hytale.app/Contents/Assets";
                    
                    // Create symlink using ln command - run from version directory
                    var lnAssets = new ProcessStartInfo("ln", new[] { "-s", relativeAssetsPath, "Assets" })
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = versionPath
                    };
                    var lnProcess = Process.Start(lnAssets);
                    if (lnProcess != null)
                    {
                        string errors = await lnProcess.StandardError.ReadToEndAsync();
                        string output = await lnProcess.StandardOutput.ReadToEndAsync();
                        await lnProcess.WaitForExitAsync();
                        
                        if (lnProcess.ExitCode == 0)
                        {
                            Logger.Success("Assets", $"Created Assets symlink: {rootAssetsLink} -> {relativeAssetsPath}");
                            
                            // Verify the symlink works
                            if (Directory.Exists(rootAssetsLink))
                            {
                                Logger.Success("Assets", "Assets symlink verified - directory is accessible");
                            }
                            else
                            {
                                Logger.Error("Assets", "Assets symlink created but directory not accessible");
                            }
                        }
                        else
                        {
                            Logger.Error("Assets", $"Symlink creation failed with exit code {lnProcess.ExitCode}");
                            if (!string.IsNullOrEmpty(errors))
                            {
                                Logger.Error("Assets", $"Error output: {errors}");
                            }
                            if (!string.IsNullOrEmpty(output))
                            {
                                Logger.Info("Assets", $"Standard output: {output}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Assets", $"Failed to create Assets symlink: {ex.Message}");
                }
            }
            
            Logger.Success("Assets", "Assets extracted successfully");
            progressCallback(100, "Assets extracted");
        }
        catch (Exception ex)
        {
            Logger.Error("Assets", $"Failed to extract assets: {ex.Message}");
            throw;
        }
    }

    // JRE Download - uses official Hytale JRE from launcher.hytale.com
    private const string RequiredJreVersion = "25.0.1_8";
    
    // VC++ Redistributable for Windows
    private const string VCRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    
    /// <summary>
    /// Checks if Visual C++ Redistributable is installed on Windows.
    /// Returns true if installed or not on Windows.
    /// </summary>
    private bool IsVCRedistInstalled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;
        
        try
        {
            // Check for VC++ 2015-2022 Redistributable (x64) in registry
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (key != null)
            {
                var installed = key.GetValue("Installed");
                if (installed != null && (int)installed == 1)
                {
                    Logger.Info("VCRedist", "VC++ Redistributable is already installed");
                    return true;
                }
            }
            
            // Also check alternative registry path
            using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (key2 != null)
            {
                var installed = key2.GetValue("Installed");
                if (installed != null && (int)installed == 1)
                {
                    Logger.Info("VCRedist", "VC++ Redistributable is already installed (WoW64)");
                    return true;
                }
            }
            
            Logger.Info("VCRedist", "VC++ Redistributable not found");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warning("VCRedist", $"Failed to check VC++ Redistributable: {ex.Message}");
            return false; // Assume not installed if we can't check
        }
    }
    
    /// <summary>
    /// Ensures Visual C++ Redistributable is installed on Windows.
    /// Downloads and runs the installer if not present.
    /// </summary>
    private async Task EnsureVCRedistInstalledAsync(Action<int, string> progressCallback)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            progressCallback(100, "VC++ not required on this platform");
            return;
        }
        
        if (IsVCRedistInstalled())
        {
            progressCallback(100, "VC++ Redistributable ready");
            return;
        }
        
        progressCallback(0, "Downloading Visual C++ Redistributable...");
        Logger.Info("VCRedist", "Downloading VC++ Redistributable...");
        
        string cacheDir = Path.Combine(_appDir, "cache");
        Directory.CreateDirectory(cacheDir);
        string installerPath = Path.Combine(cacheDir, "vc_redist.x64.exe");
        
        try
        {
            // Download the installer
            using var response = await HttpClient.GetAsync(VCRedistUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            
            var buffer = new byte[8192];
            long downloadedBytes = 0;
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloadedBytes += bytesRead;
                
                if (totalBytes > 0)
                {
                    int percent = (int)((downloadedBytes * 50) / totalBytes); // 0-50%
                    progressCallback(percent, $"Downloading VC++ Redistributable... {percent * 2}%");
                }
            }
            
            Logger.Info("VCRedist", "Download complete, running installer...");
            progressCallback(50, "Installing Visual C++ Redistributable...");
            
            // Run the installer silently
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = true,
                Verb = "runas" // Request elevation
            };
            
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0 || process.ExitCode == 1638) // 1638 = already installed
                {
                    Logger.Success("VCRedist", "VC++ Redistributable installed successfully");
                    progressCallback(100, "VC++ Redistributable installed");
                }
                else if (process.ExitCode == 3010) // Restart required
                {
                    Logger.Success("VCRedist", "VC++ Redistributable installed (restart may be required)");
                    progressCallback(100, "VC++ Redistributable installed");
                }
                else
                {
                    Logger.Warning("VCRedist", $"VC++ installer exited with code: {process.ExitCode}");
                    progressCallback(100, "VC++ installation completed");
                }
            }
            
            // Clean up installer
            try { File.Delete(installerPath); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Error("VCRedist", $"Failed to install VC++ Redistributable: {ex.Message}");
            // Don't fail the game launch - the game might work anyway
            progressCallback(100, "VC++ installation skipped");
        }
    }
    
    private async Task EnsureJREInstalledAsync(Action<int, string> progressCallback)
    {
        string jreDir = Path.Combine(_appDir, "jre");
        string javaBin;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            javaBin = Path.Combine(jreDir, "bin", "java.exe");
        }
        else
        {
            javaBin = Path.Combine(jreDir, "bin", "java");
        }
        
        // Check if correct JRE version is installed by looking for version marker file
        string versionMarkerPath = Path.Combine(jreDir, ".jre_version");
        
        if (File.Exists(javaBin) && File.Exists(versionMarkerPath))
        {
            try
            {
                string installedVersion = await File.ReadAllTextAsync(versionMarkerPath);
                if (installedVersion.Trim() == RequiredJreVersion)
                {
                    Logger.Info("JRE", $"Java Runtime {RequiredJreVersion} already installed");
                    EnsureJavaWrapper(javaBin);
                    progressCallback(100, "Java Runtime ready");
                    return;
                }
                Logger.Warning("JRE", $"Installed JRE version {installedVersion.Trim()} != required {RequiredJreVersion}. Reinstalling...");
            }
            catch (Exception ex)
            {
                Logger.Warning("JRE", $"Failed to check JRE version: {ex.Message}. Reinstalling...");
            }
        }
        else if (File.Exists(javaBin))
        {
            // Old installation without version marker - reinstall
            Logger.Warning("JRE", "JRE version marker not found. Reinstalling official Hytale JRE...");
        }
        
        // Delete old JRE if exists
        if (Directory.Exists(jreDir))
        {
            try
            {
                Directory.Delete(jreDir, true);
                Logger.Info("JRE", "Removed old JRE installation");
            }
            catch (Exception ex)
            {
                Logger.Warning("JRE", $"Failed to remove old JRE: {ex.Message}");
            }
        }
        
        progressCallback(0, "Downloading Java Runtime...");
        Logger.Info("JRE", "Downloading official Hytale Java Runtime...");
        
        // Determine platform - Hytale uses different naming convention
        string osName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : 
                        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux";
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        string archiveType = osName == "windows" ? "zip" : "tar.gz";
        
        // First try to fetch latest JRE info from Hytale launcher directly
        string? url = null;
        string? expectedSha256 = null;
        
        try
        {
            Logger.Info("JRE", "Fetching JRE info from launcher.hytale.com...");
            var jreInfoResponse = await HttpClient.GetStringAsync("https://launcher.hytale.com/version/release/jre.json");
            var jreInfo = JsonSerializer.Deserialize<JsonElement>(jreInfoResponse);
            
            if (jreInfo.TryGetProperty("download_url", out var downloadUrls) &&
                downloadUrls.TryGetProperty(osName, out var osUrls) &&
                osUrls.TryGetProperty(arch, out var archInfo))
            {
                if (archInfo.TryGetProperty("url", out var urlProp))
                {
                    url = urlProp.GetString();
                }
                if (archInfo.TryGetProperty("sha256", out var sha256Prop))
                {
                    expectedSha256 = sha256Prop.GetString();
                }
                Logger.Info("JRE", $"Got JRE URL from Hytale launcher: {url}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("JRE", $"Failed to fetch from launcher.hytale.com: {ex.Message}");
        }
        
        // Fallback to local jre.json config
        if (string.IsNullOrEmpty(url))
        {
            try
            {
                var jreConfigPath = Path.Combine(AppContext.BaseDirectory, "jre.json");
                if (File.Exists(jreConfigPath))
                {
                    var jreConfigJson = await File.ReadAllTextAsync(jreConfigPath);
                    var jreConfig = JsonSerializer.Deserialize<JsonElement>(jreConfigJson);
                    
                    if (jreConfig.TryGetProperty("download_url", out var downloadUrls) &&
                        downloadUrls.TryGetProperty(osName, out var osUrls) &&
                        osUrls.TryGetProperty(arch, out var archInfo))
                    {
                        if (archInfo.TryGetProperty("url", out var urlProp))
                        {
                            url = urlProp.GetString();
                        }
                        if (archInfo.TryGetProperty("sha256", out var sha256Prop))
                        {
                            expectedSha256 = sha256Prop.GetString();
                        }
                        Logger.Info("JRE", $"Using JRE URL from local config: {url}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("JRE", $"Failed to load local jre.json: {ex.Message}");
            }
        }
        
        // Ultimate fallback - hardcoded URLs for official Hytale JRE
        if (string.IsNullOrEmpty(url))
        {
            url = $"https://launcher.hytale.com/redist/jre/{osName}/{arch}/jre-{RequiredJreVersion}.{archiveType}";
            Logger.Info("JRE", $"Using hardcoded Hytale JRE URL: {url}");
        }
        
        string cacheDir = Path.Combine(_appDir, "cache");
        Directory.CreateDirectory(cacheDir);
        string archivePath = Path.Combine(cacheDir, $"jre.{archiveType}");
        
        // Download with proper headers for Adoptium API
        using var jreClient = new HttpClient();
        jreClient.Timeout = TimeSpan.FromMinutes(10);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "HyPrism/1.0");
        request.Headers.Add("Accept", "*/*");
        
        using var response = await jreClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        
        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;
            
            if (totalBytes > 0)
            {
                var progress = (int)((totalRead * 80) / totalBytes); // 0-80%
                progressCallback(progress, $"Downloading Java Runtime... {progress}%");
            }
        }
        fileStream.Close();
        
        progressCallback(85, "Extracting Java Runtime...");
        Logger.Info("JRE", "Extracting Java Runtime...");
        
        // Create jre directory
        Directory.CreateDirectory(jreDir);
        
        // Extract
        if (archiveType == "zip")
        {
            ZipFile.ExtractToDirectory(archivePath, jreDir, true);
        }
        else
        {
            // Use tar on Unix systems
            var tarProcess = new ProcessStartInfo("tar", $"-xzf \"{archivePath}\" -C \"{jreDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var tar = Process.Start(tarProcess);
            tar?.WaitForExit();
        }
        
        // Normalize JRE structure - move contents up if nested
        var entries = Directory.GetDirectories(jreDir);
        if (entries.Length == 1)
        {
            var subDir = entries[0];
            
            // On macOS, structure is different
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var contentsDir = Path.Combine(subDir, "Contents", "Home");
                if (Directory.Exists(contentsDir))
                {
                    subDir = contentsDir;
                }
            }
            
            // Move files from subdirectory to jreDir
            foreach (var entry in Directory.GetFileSystemEntries(subDir))
            {
                var name = Path.GetFileName(entry);
                var dest = Path.Combine(jreDir, name);
                if (!File.Exists(dest) && !Directory.Exists(dest))
                {
                    Directory.Move(entry, dest);
                }
            }
            
            // Remove empty subdirectory
            try { Directory.Delete(entries[0], true); } catch { }
        }
        
        // Make java executable on Unix
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var chmod = new ProcessStartInfo("chmod", $"+x \"{javaBin}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(chmod)?.WaitForExit();
        }
        
        // Cleanup archive
        try { File.Delete(archivePath); } catch { }
        
        // On macOS, create java symlink structure like old launcher
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await SetupMacOSJavaSymlinksAsync(jreDir);
        }

        // Wrap java to strip unsupported flags and point to the freshly installed JRE
        EnsureJavaWrapper(javaBin);
        
        // Write version marker file to track installed version
        try
        {
            await File.WriteAllTextAsync(versionMarkerPath, RequiredJreVersion);
            Logger.Info("JRE", $"Written version marker: {RequiredJreVersion}");
        }
        catch (Exception ex)
        {
            Logger.Warning("JRE", $"Failed to write version marker: {ex.Message}");
        }
        
        progressCallback(100, "Java Runtime installed");
        Logger.Success("JRE", $"Hytale Java Runtime {RequiredJreVersion} installed successfully");
    }

    private async Task SetupMacOSJavaSymlinksAsync(string jreDir)
    {
        // Create java directory structure like old launcher
        string javaDir = Path.Combine(_appDir, "java");
        string javaHomeBin = Path.Combine(javaDir, "Contents", "Home", "bin");
        
        if (!Directory.Exists(javaHomeBin))
        {
            try
            {
                if (Directory.Exists(javaDir))
                {
                    Directory.Delete(javaDir, true);
                }
                
                Directory.CreateDirectory(Path.Combine(javaDir, "Contents", "Home"));
                
                // Create symlinks
                var lnBin = new ProcessStartInfo("ln", $"-sf \"{Path.Combine(jreDir, "bin")}\" \"{Path.Combine(javaDir, "Contents", "Home", "bin")}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(lnBin)?.WaitForExit();
                
                var lnLib = new ProcessStartInfo("ln", $"-sf \"{Path.Combine(jreDir, "lib")}\" \"{Path.Combine(javaDir, "Contents", "Home", "lib")}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(lnLib)?.WaitForExit();
            }
            catch (Exception ex)
            {
                Logger.Warning("JRE", $"Failed to create Java symlinks: {ex.Message}");
            }
        }
        
        // Sign JRE
        Logger.Info("JRE", "Signing Java Runtime...");
        RunSilentProcess("xattr", $"-cr \"{jreDir}\"");
        RunSilentProcess("codesign", $"--force --deep --sign - \"{jreDir}\"");
        await Task.CompletedTask;
    }

    private async Task<int> GetJavaFeatureVersionAsync(string javaBin)
    {
        try
        {
            var psi = new ProcessStartInfo(javaBin, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                return 0;
            }

            string stderr = await proc.StandardError.ReadToEndAsync();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout + "\n" + stderr;
            var match = Regex.Match(combined, "version \"?([0-9][^\"\\s]*)");
            if (match.Success)
            {
                return ParseJavaMajor(match.Groups[1].Value);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("JRE", $"Failed to read Java version: {ex.Message}");
        }

        return 0;
    }

    private int ParseJavaMajor(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return 0;
        }

        var parts = versionString.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        if (int.TryParse(parts[0], out var major))
        {
            if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var minor))
            {
                return minor;
            }

            return major;
        }

        return 0;
    }

    private async Task<bool> SupportsShenandoahAsync(string javaBin)
    {
        try
        {
            var psi = new ProcessStartInfo(javaBin, "-XX:+UseShenandoahGC -version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            string stderr = await proc.StandardError.ReadToEndAsync();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                return true;
            }

            var combined = (stdout + "\n" + stderr).ToLowerInvariant();
            if (combined.Contains("unrecognized") || combined.Contains("could not create the java virtual machine"))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("JRE", $"Shenandoah probe failed: {ex.Message}");
        }

        return false;
    }

    private void EnsureJavaWrapper(string javaBin)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows already uses java.exe; wrapper not required.
            return;
        }

        try
        {
            var javaDir = Path.GetDirectoryName(javaBin);
            if (string.IsNullOrEmpty(javaDir))
            {
                return;
            }

            var realJava = Path.Combine(javaDir, "java.real");

            if (!File.Exists(realJava))
            {
                try
                {
                    if (File.Exists(javaBin))
                    {
                        // If javaBin is already a wrapper script, avoid moving it over realJava
                        byte[] headBytes = new byte[2];
                        using (var fs = new FileStream(javaBin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            _ = fs.Read(headBytes, 0, 2);
                        }

                        bool looksLikeScript = headBytes[0] == (byte)'#' && headBytes[1] == (byte)'!';
                        if (looksLikeScript)
                        {
                            Logger.Warning("JRE", "Wrapper detected but java.real missing; skipping move to avoid clobbering wrapper");
                            return;
                        }

                        File.Move(javaBin, realJava, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("JRE", $"Failed to move java binary for wrapping: {ex.Message}");
                    return;
                }
            }

            var wrapper = "#!/bin/bash\n" +
                         "REAL_JAVA=\"$(cd \"$(dirname \"$0\")\" && pwd)/java.real\"\n" +
                         "ARGS=()\n" +
                         "for arg in \"$@\"; do\n" +
                         "  if [[ \"$arg\" == -XX:ShenandoahGCMode=* ]]; then\n" +
                         "    continue\n" +
                         "  fi\n" +
                         "  ARGS+=(\"$arg\")\n" +
                         "done\n" +
                         "exec \"$REAL_JAVA\" \"${ARGS[@]}\"\n";

            File.WriteAllText(javaBin, wrapper);
            var chmod = new ProcessStartInfo("chmod", $"+x \"{javaBin}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(chmod)?.WaitForExit();
        }
        catch (Exception ex)
        {
            Logger.Warning("JRE", $"Failed to create Java wrapper: {ex.Message}");
        }
    }

    private void CleanupCorruptedInstall(string versionPath)
    {
        string backupRoot = Path.Combine(Path.GetTempPath(), "HyPrismBackup", Guid.NewGuid().ToString());
        // Preserve UserData and Client/Assets to avoid re-downloading game
        string[] preserve = { "UserData", "Client" };

        try
        {
            Directory.CreateDirectory(backupRoot);
            foreach (var dirName in preserve)
            {
                var src = Path.Combine(versionPath, dirName);
                if (Directory.Exists(src))
                {
                    var dest = Path.Combine(backupRoot, dirName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CopyDirectory(src, dest);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Download", $"Failed to backup user data before cleanup: {ex.Message}");
        }

        try
        {
            if (Directory.Exists(versionPath))
            {
                Directory.Delete(versionPath, true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Download", $"Failed to clean corrupted install at {versionPath}: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(versionPath);
            foreach (var dirName in preserve)
            {
                var backup = Path.Combine(backupRoot, dirName);
                var dest = Path.Combine(versionPath, dirName);
                if (Directory.Exists(backup))
                {
                    CopyDirectory(backup, dest);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Download", $"Failed to recreate install directory {versionPath}: {ex.Message}");
        }
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }

    private void RunSilentProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Exception ex)
        {
            Logger.Warning("Process", $"Failed to run {fileName} {arguments}: {ex.Message}");
        }
    }

    private bool IsMacAppSignatureCurrent(string executablePath, string stampPath)
    {
        try
        {
            if (!File.Exists(executablePath) || !File.Exists(stampPath))
            {
                return false;
            }

            var stamp = File.ReadAllText(stampPath).Trim();
            var currentTicks = File.GetLastWriteTimeUtc(executablePath).Ticks.ToString();
            return string.Equals(stamp, currentTicks, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void MarkMacAppSigned(string executablePath, string stampPath)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(executablePath).Ticks.ToString();
            File.WriteAllText(stampPath, ticks);
        }
        catch (Exception ex)
        {
            Logger.Warning("Game", $"Failed to record app sign stamp: {ex.Message}");
        }
    }

    private void ClearMacQuarantine(string path)
    {
        try
        {
            RunSilentProcess("xattr", $"-cr \"{path}\"");
        }
        catch (Exception ex)
        {
            Logger.Warning("Game", $"Failed to clear quarantine on {path}: {ex.Message}");
        }
    }

    private string GetJavaPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Use the symlinked java path on macOS like old launcher
            return Path.Combine(_appDir, "java", "Contents", "Home", "bin", "java");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(_appDir, "jre", "bin", "java.exe");
        }
        else
        {
            return Path.Combine(_appDir, "jre", "bin", "java");
        }
    }

    private async Task LaunchGameAsync(string versionPath, string branch)
    {
        Logger.Info("Game", $"Preparing to launch from {versionPath}");
        
        string executable;
        string workingDir;
        string gameDir = versionPath;
        
        // Determine client path based on OS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            executable = Path.Combine(versionPath, "Client", "Hytale.app", "Contents", "MacOS", "HytaleClient");
            // Set working directory to the MacOS folder where the executable is located
            workingDir = Path.Combine(versionPath, "Client", "Hytale.app", "Contents", "MacOS");
            
            if (!File.Exists(executable))
            {
                Logger.Error("Game", $"Game client not found at {executable}");
                throw new Exception($"Game client not found at {executable}");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            executable = Path.Combine(versionPath, "Client", "HytaleClient.exe");
            workingDir = Path.Combine(versionPath, "Client");
            
            if (!File.Exists(executable))
            {
                Logger.Error("Game", $"Game client not found at {executable}");
                throw new Exception($"Game client not found at {executable}");
            }
        }
        else
        {
            // Linux
            executable = Path.Combine(versionPath, "Client", "HytaleClient");
            workingDir = Path.Combine(versionPath, "Client");
            
            if (!File.Exists(executable))
            {
                Logger.Error("Game", $"Game client not found at {executable}");
                throw new Exception($"Game client not found at {executable}");
            }
        }

        // On macOS, clear quarantine attributes BEFORE patching
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string appBundle = Path.Combine(versionPath, "Client", "Hytale.app");
            ClearMacQuarantine(appBundle);
            Logger.Info("Game", "Cleared macOS quarantine attributes before patching");
        }

        // Patch binary to accept custom auth server tokens
        // The auth domain is "sessions.sanasol.ws" but we need to patch "hytale.com" -> "sanasol.ws"
        // so that sessions.hytale.com becomes sessions.sanasol.ws
        // NOTE: Patching is needed even in offline/insecure mode because the game still validates domains
        bool enablePatching = true;
        if (enablePatching && !string.IsNullOrWhiteSpace(_config.AuthDomain))
        {
            try
            {
                // Extract the base domain from auth domain (e.g., "sessions.sanasol.ws" -> "sanasol.ws")
                string baseDomain = _config.AuthDomain;
                if (baseDomain.StartsWith("sessions."))
                {
                    baseDomain = baseDomain.Substring("sessions.".Length);
                }
                
                Logger.Info("Game", $"Patching binary: hytale.com -> {baseDomain}");
                var patcher = new ClientPatcher(baseDomain);
                
                // Patch client binary first
                var patchResult = patcher.EnsureClientPatched(versionPath, (msg, progress) =>
                {
                    if (progress.HasValue)
                    {
                        Logger.Info("Patcher", $"{msg} ({progress}%)");
                    }
                    else
                    {
                        Logger.Info("Patcher", msg);
                    }
                });
                
                // Also patch server JAR (required for singleplayer to work)
                Logger.Info("Game", $"Patching server JAR: sessions.hytale.com -> sessions.{baseDomain}");
                var serverPatchResult = patcher.PatchServerJar(versionPath, (msg, progress) =>
                {
                    if (progress.HasValue)
                    {
                        Logger.Info("Patcher", $"{msg} ({progress}%)");
                    }
                    else
                    {
                        Logger.Info("Patcher", msg);
                    }
                });
                
                if (patchResult.Success)
                {
                    if (patchResult.AlreadyPatched)
                    {
                        Logger.Info("Game", "Client binary already patched");
                    }
                    else if (patchResult.PatchCount > 0)
                    {
                        Logger.Success("Game", $"Client binary patched successfully ({patchResult.PatchCount} occurrences)");
                        
                        // Re-sign the binary after patching (macOS requirement)
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            try
                            {
                                Logger.Info("Game", "Re-signing patched binary...");
                                string appBundle = Path.Combine(versionPath, "Client", "Hytale.app");
                                bool signed = ClientPatcher.SignMacOSBinary(appBundle);
                                if (signed)
                                {
                                    Logger.Success("Game", "Binary re-signed successfully");
                                }
                                else
                                {
                                    Logger.Warning("Game", "Binary signing failed - game may not launch");
                                }
                            }
                            catch (Exception signEx)
                            {
                                Logger.Warning("Game", $"Error re-signing binary: {signEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        Logger.Info("Game", "No client patches needed - binary uses unknown encoding or already patched");
                    }
                }
                else
                {
                    Logger.Warning("Game", $"Client binary patching failed: {patchResult.Error}");
                    Logger.Info("Game", "Continuing launch anyway - may not connect to custom auth server");
                }
                
                // Log server JAR patch result
                if (serverPatchResult.Success)
                {
                    if (serverPatchResult.AlreadyPatched)
                    {
                        Logger.Info("Game", "Server JAR already patched");
                    }
                    else if (serverPatchResult.PatchCount > 0)
                    {
                        Logger.Success("Game", $"Server JAR patched successfully ({serverPatchResult.PatchCount} occurrences)");
                    }
                    else if (!string.IsNullOrEmpty(serverPatchResult.Warning))
                    {
                        Logger.Info("Game", $"Server JAR: {serverPatchResult.Warning}");
                    }
                }
                else
                {
                    Logger.Warning("Game", $"Server JAR patching failed: {serverPatchResult.Error}");
                    Logger.Info("Game", "Singleplayer may not work properly");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Game", $"Error during binary patching: {ex.Message}");
                Logger.Info("Game", "Continuing launch anyway - may not connect to custom auth server");
            }
        }

        // STEP 1: Determine UUID to use for this session
        // Use the username->UUID mapping to ensure consistent UUIDs across sessions
        // This is the key fix for skin persistence - each username always gets the same UUID
        string sessionUuid = GetUuidForUser(_config.Nick);
        Logger.Info("Game", $"Using UUID for user '{_config.Nick}': {sessionUuid}");

        // STEP 2: Fetch auth token - only if OnlineMode is enabled
        // If user wants offline mode, skip token fetching entirely
        string? identityToken = null;
        string? sessionToken = null;
        
        if (_config.OnlineMode && !string.IsNullOrWhiteSpace(_config.AuthDomain))
        {
            Logger.Info("Game", $"Online mode enabled - fetching auth tokens from {_config.AuthDomain}...");
            
            try
            {
                var authService = new AuthService(HttpClient, _config.AuthDomain);
                
                // Use sessionUuid (which may be fresh/random) for auth
                var tokenResult = await authService.GetGameSessionTokenAsync(sessionUuid, _config.Nick);
                
                if (tokenResult.Success && !string.IsNullOrEmpty(tokenResult.Token))
                {
                    identityToken = tokenResult.Token;
                    sessionToken = tokenResult.SessionToken ?? tokenResult.Token; // Fallback to identity token
                    Logger.Success("Game", "Identity token obtained successfully");
                    Logger.Success("Game", "Session token obtained successfully");
                }
                else
                {
                    Logger.Warning("Game", $"Could not get auth token: {tokenResult.Error}");
                    Logger.Info("Game", "Will try launching with offline mode instead");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Game", $"Error fetching auth token: {ex.Message}");
                Logger.Info("Game", "Will try launching with offline mode instead");
            }
        }

        // Get Java path
        string javaPath = GetJavaPath();
        if (!File.Exists(javaPath))
        {
            Logger.Error("Game", $"Java not found at {javaPath}");
            throw new Exception($"Java not found at {javaPath}");
        }
        
        // Verify Java is executable by running --version
        try
        {
            var javaCheck = new ProcessStartInfo(javaPath, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var javaProcess = Process.Start(javaCheck);
            if (javaProcess != null)
            {
                string javaOutput = await javaProcess.StandardOutput.ReadToEndAsync();
                await javaProcess.WaitForExitAsync();
                if (javaProcess.ExitCode == 0)
                {
                    Logger.Success("Game", $"Java verified: {javaOutput.Split('\n')[0]}");
                }
                else
                {
                    Logger.Warning("Game", $"Java check returned exit code {javaProcess.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Game", $"Could not verify Java: {ex.Message}");
        }

        // Use per-instance UserData folder - this keeps skins/settings with the game instance
        string userDataDir = GetInstanceUserDataPath(versionPath);
        Directory.CreateDirectory(userDataDir);
        
        // Restore current profile's skin data before launching the game
        // This ensures the player's custom skin is loaded from their profile
        var currentProfile = _config.Profiles?.FirstOrDefault(p => p.UUID == sessionUuid);
        string? skinCachePath = null;
        if (currentProfile != null)
        {
            RestoreProfileSkinData(currentProfile);
            Logger.Info("Game", $"Restored skin data for profile '{currentProfile.Name}'");
            
            // Start skin protection - this watches the skin file and restores it if the game overwrites it
            // The game may fetch skin data from the server on startup which could overwrite our local cache
            skinCachePath = Path.Combine(userDataDir, "CachedPlayerSkins", $"{currentProfile.UUID}.json");
            if (File.Exists(skinCachePath))
            {
                StartSkinProtection(currentProfile, skinCachePath);
            }
        }

        Logger.Info("Game", $"Launching: {executable}");
        Logger.Info("Game", $"Java: {javaPath}");
        Logger.Info("Game", $"AppDir: {gameDir}");
        Logger.Info("Game", $"UserData: {userDataDir}");
        Logger.Info("Game", $"Online Mode: {_config.OnlineMode}");
        Logger.Info("Game", $"Session UUID: {sessionUuid}");

        // On macOS/Linux, create a launch script to run with clean environment
        ProcessStartInfo startInfo;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Use ArgumentList for proper escaping
            startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            
            // Add arguments using ArgumentList for proper Windows escaping
            startInfo.ArgumentList.Add("--app-dir");
            startInfo.ArgumentList.Add(gameDir);
            startInfo.ArgumentList.Add("--user-dir");
            startInfo.ArgumentList.Add(userDataDir);
            startInfo.ArgumentList.Add("--java-exec");
            startInfo.ArgumentList.Add(javaPath);
            startInfo.ArgumentList.Add("--name");
            startInfo.ArgumentList.Add(_config.Nick);
            
            // Add auth mode based on user's OnlineMode preference
            // If OnlineMode is OFF, always use offline mode regardless of tokens
            // If OnlineMode is ON and we have tokens, use authenticated mode
            if (_config.OnlineMode && !string.IsNullOrEmpty(identityToken) && !string.IsNullOrEmpty(sessionToken))
            {
                startInfo.ArgumentList.Add("--auth-mode");
                startInfo.ArgumentList.Add("authenticated");
                startInfo.ArgumentList.Add("--uuid");
                startInfo.ArgumentList.Add(sessionUuid);
                startInfo.ArgumentList.Add("--identity-token");
                startInfo.ArgumentList.Add(identityToken);
                startInfo.ArgumentList.Add("--session-token");
                startInfo.ArgumentList.Add(sessionToken);
                Logger.Info("Game", $"Using authenticated mode with session UUID: {sessionUuid}");
            }
            else
            {
                // Offline mode - either user selected it or no tokens available
                startInfo.ArgumentList.Add("--auth-mode");
                startInfo.ArgumentList.Add("offline");
                startInfo.ArgumentList.Add("--uuid");
                startInfo.ArgumentList.Add(sessionUuid);
                Logger.Info("Game", $"Using offline mode with UUID: {sessionUuid}");
            }
            
            // Log the arguments for debugging
            Logger.Info("Game", $"Windows launch args: {string.Join(" ", startInfo.ArgumentList)}");
        }
        else
        {
            // Build arguments for the launch script - use only documented game arguments
            var gameArgs = new List<string>
            {
                $"--app-dir \"{gameDir}\"",
                $"--user-dir \"{userDataDir}\"",
                $"--java-exec \"{javaPath}\"",
                $"--name \"{_config.Nick}\""
            };
            
            // Add auth mode based on user's OnlineMode preference
            if (_config.OnlineMode && !string.IsNullOrEmpty(identityToken) && !string.IsNullOrEmpty(sessionToken))
            {
                gameArgs.Add("--auth-mode authenticated");
                gameArgs.Add($"--uuid \"{sessionUuid}\"");
                gameArgs.Add($"--identity-token \"{identityToken}\"");
                gameArgs.Add($"--session-token \"{sessionToken}\"");
                Logger.Info("Game", $"Using authenticated mode with session UUID: {sessionUuid}");
            }
            else
            {
                // Offline mode - either user selected it or no tokens available
                gameArgs.Add("--auth-mode offline");
                gameArgs.Add($"--uuid \"{sessionUuid}\"");
                Logger.Info("Game", $"Using offline mode with UUID: {sessionUuid}");
            }
            
            // macOS/Linux: Use env to run with completely clean environment
            // This prevents .NET runtime environment variables from interfering
            string argsString = string.Join(" ", gameArgs);
            string launchScript = Path.Combine(versionPath, "launch.sh");
            
            string homeDir = Environment.GetEnvironmentVariable("HOME") ?? "/Users/" + Environment.UserName;
            string userName = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
            
            // Get the Client directory for LD_LIBRARY_PATH (needed for shared libraries like SDL3_image.so)
            string clientDir = Path.Combine(versionPath, "Client");
            
            // Write the launch script with env to start with empty environment
            string scriptContent = $@"#!/bin/bash
# Launch script generated by HyPrism
# Uses env to clear ALL environment variables before launching game

# Set LD_LIBRARY_PATH to include Client directory for shared libraries (SDL3_image.so, etc.)
CLIENT_DIR=""{clientDir}""

exec env \
    HOME=""{homeDir}"" \
    USER=""{userName}"" \
    PATH=""/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin"" \
    SHELL=""/bin/zsh"" \
    TMPDIR=""{Path.GetTempPath().TrimEnd('/')}"" \
    LD_LIBRARY_PATH=""$CLIENT_DIR:$LD_LIBRARY_PATH"" \
    ""{executable}"" {argsString}
";
            File.WriteAllText(launchScript, scriptContent);
            
            // Make it executable
            var chmod = new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = $"+x \"{launchScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(chmod)?.WaitForExit();
            
            // Use /bin/bash to run the script
            startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            startInfo.ArgumentList.Add(launchScript);
            
            Logger.Info("Game", $"Launch script: {launchScript}");
        }
        
        try
        {
            _gameProcess = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Logger.Error("Game", $"Failed to start game process: {ex.Message}");
            SendErrorEvent("launch", "Failed to start game", ex.Message);
            throw new Exception($"Failed to start game: {ex.Message}");
        }
        
        if (_gameProcess == null)
        {
            Logger.Error("Game", "Process.Start returned null - game failed to launch");
            SendErrorEvent("launch", "Failed to start game", "Process.Start returned null");
            throw new Exception("Failed to start game process");
        }
        
        Logger.Success("Game", $"Game started with PID: {_gameProcess.Id}");
        
        // Set Discord presence to Playing
        _discordService.SetPresence(DiscordService.PresenceState.Playing, $"Playing as {_config.Nick}");
        
        // Notify frontend that game has launched
        SendGameStateEvent("started");
        
        // Handle process exit in background
        _ = Task.Run(async () =>
        {
            await _gameProcess.WaitForExitAsync();
            var exitCode = _gameProcess.ExitCode;
            Logger.Info("Game", $"Game process exited with code: {exitCode}");
            _gameProcess = null;
            
            // Stop skin protection first - allow normal skin file operations now
            StopSkinProtection();
            
            // Backup current profile's skin data after game exits (save any changes made during gameplay)
            BackupProfileSkinData(GetCurrentUuid());
            
            // Set Discord presence back to Idle
            _discordService.SetPresence(DiscordService.PresenceState.Idle);
            
            // Notify frontend that game has exited with exit code
            SendGameStateEvent("stopped", exitCode);
        });
    }

    private void SendGameStateEvent(string state, int? exitCode = null)
    {
        try
        {
            GameStateChanged?.Invoke(state, exitCode ?? 0);
        }
        catch (Exception ex)
        {
            Logger.Warning("Game", $"Failed to send game state event: {ex.Message}");
        }
    }

    private void SendErrorEvent(string type, string message, string? technical = null)
    {
        try
        {
            ErrorOccurred?.Invoke(type, message, technical);
        }
        catch (Exception ex)
        {
            Logger.Warning("Events", $"Failed to send error event: {ex.Message}");
        }
    }

    // Check for launcher updates and emit event if available
    public async Task CheckForLauncherUpdatesAsync()
    {

        try
        {
            var launcherBranch = GetLauncherBranch();
            var isBetaChannel = launcherBranch == "beta";
            
            // Get all releases (not just latest) to support beta channel
            var apiUrl = "https://api.github.com/repos/yyyumeniku/HyPrism/releases?per_page=50";
            var json = await HttpClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);

            var currentVersion = GetLauncherVersion();
            string? bestVersion = null;
            JsonElement? bestRelease = null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tagName = release.GetProperty("tag_name").GetString();
                if (string.IsNullOrWhiteSpace(tagName)) continue;
                
                // Check GitHub's native prerelease flag
                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseVal) && prereleaseVal.GetBoolean();
                
                // Match channel: beta channel gets prereleases, stable gets stable releases
                if (isBetaChannel && !isPrerelease)
                {
                    // User wants beta, skip stable releases
                    continue;
                }
                else if (!isBetaChannel && isPrerelease)
                {
                    // User wants stable, skip prereleases
                    continue;
                }
                
                // Parse version from tag
                // Formats: "v2.0.1", "2.0.1", "beta3-3.0.0", "beta-3.0.0"
                var version = ParseVersionFromTag(tagName);
                if (string.IsNullOrWhiteSpace(version)) continue;
                
                // Compare with current version
                if (IsNewerVersion(version, currentVersion))
                {
                    // Check if this is better than our current best
                    if (bestVersion == null || IsNewerVersion(version, bestVersion))
                    {
                        bestVersion = version;
                        bestRelease = release;
                    }
                }
            }

            if (bestRelease.HasValue && !string.IsNullOrWhiteSpace(bestVersion))
            {
                var release = bestRelease.Value;
                Logger.Info("Update", $"Update available: {currentVersion} -> {bestVersion} (channel: {launcherBranch})");
                
                // Pick the right asset for this platform
                string? downloadUrl = null;
                string? assetName = null;
                var assets = release.GetProperty("assets");
                var arch = RuntimeInformation.ProcessArchitecture;

                string? targetAsset = null;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    targetAsset = "macos-arm64.dmg";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    targetAsset = "windows-x64.exe";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    targetAsset = arch == Architecture.Arm64 ? "linux-arm64.tar.gz" : "linux-x64.AppImage";

                if (!string.IsNullOrWhiteSpace(targetAsset))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        if (!string.IsNullOrWhiteSpace(name) && name.Contains(targetAsset, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            assetName = name;
                            break;
                        }
                    }
                }

                var updateInfo = new
                {
                        version = bestVersion,
                        currentVersion = currentVersion,
                        downloadUrl = downloadUrl ?? "",
                        assetName = assetName ?? "",
                        releaseUrl = release.GetProperty("html_url").GetString() ?? "",
                        isBeta = launcherBranch == "beta"
                };
                    
                LauncherUpdateAvailable?.Invoke(updateInfo);
            }
            else
            {
                Logger.Info("Update", $"Launcher is up to date: {currentVersion} (channel: {launcherBranch})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Update", $"Failed to check for updates: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse version string from various tag formats.
    /// Supports: "v2.0.1", "2.0.1", "beta3-3.0.0", "beta-3.0.0", "beta3-v3.0.0"
    /// </summary>
    private static string? ParseVersionFromTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        
        var tagLower = tag.ToLowerInvariant();
        
        // Handle beta format: "beta3-3.0.0" or "beta-3.0.0"
        if (tagLower.StartsWith("beta"))
        {
            var dashIndex = tag.IndexOf('-');
            if (dashIndex >= 0 && dashIndex < tag.Length - 1)
            {
                var versionPart = tag.Substring(dashIndex + 1).TrimStart('v', 'V');
                return versionPart;
            }
            // beta without dash, try to extract version after "beta" text
            var afterBeta = tag.Substring(4).TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimStart('-', '_').TrimStart('v', 'V');
            return string.IsNullOrWhiteSpace(afterBeta) ? null : afterBeta;
        }
        
        // Handle standard format: "v2.0.1" or "2.0.1"
        return tag.TrimStart('v', 'V');
    }

    private static bool IsNewerVersion(string remote, string current)
    {
        // Parse versions like "2.0.1" into comparable parts
        var remoteParts = remote.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var currentParts = current.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

        for (int i = 0; i < Math.Max(remoteParts.Length, currentParts.Length); i++)
        {
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            var c = i < currentParts.Length ? currentParts[i] : 0;
            if (r > c) return true;
            if (r < c) return false;
        }
        return false;
    }

    private string GenerateOfflineUUID(string playerName)
    {
        // Generate UUID v3 from player name (same as old launcher)
        using var md5 = System.Security.Cryptography.MD5.Create();
        var inputBytes = System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName);
        var hashBytes = md5.ComputeHash(inputBytes);
        
        // Set version to 3 (MD5 based)
        hashBytes[6] = (byte)((hashBytes[6] & 0x0F) | 0x30);
        // Set variant
        hashBytes[8] = (byte)((hashBytes[8] & 0x3F) | 0x80);
        
        return new Guid(hashBytes).ToString();
    }

    public bool IsGameRunning()
    {
        return _gameProcess != null && !_gameProcess.HasExited;
    }

    public List<string> GetRecentLogs(int count = 10)
    {
        return Logger.GetRecentLogs(count);
    }

    public bool ExitGame()
    {
        if (_gameProcess != null && !_gameProcess.HasExited)
        {
            _gameProcess.Kill();
            _gameProcess = null;
            return true;
        }
        return false;
    }

    public bool DeleteGame(string branch, int versionNumber)
    {
        try
        {
            string normalizedBranch = NormalizeVersionType(branch);
            string versionPath = ResolveInstancePath(normalizedBranch, versionNumber, preferExisting: true);
            if (Directory.Exists(versionPath))
            {
                Directory.Delete(versionPath, true);
            }
            if (versionNumber == 0)
            {
                var infoPath = GetLatestInfoPath(normalizedBranch);
                if (File.Exists(infoPath))
                {
                    File.Delete(infoPath);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Game", $"Error deleting game: {ex.Message}");
            return false;
        }
    }

    // Folder
    public bool OpenFolder()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"\"{_appDir}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{_appDir}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start("xdg-open", $"\"{_appDir}\"");
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> SelectInstanceDirectoryAsync()
    {
        // Folder picker is not available in Photino. Return the current/active
        // instance root so the frontend can show it and collect user input manually.
        return Task.FromResult<string?>(GetInstanceRoot());
    }
    
    /// <summary>
    /// Opens a folder browser dialog and returns the selected path.
    /// </summary>
    public async Task<string?> BrowseFolder(string? initialPath = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var script = $@"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.FolderBrowserDialog; ";
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                    script += $@"$dialog.SelectedPath = '{initialPath.Replace("'", "''")}'; ";
                script += @"if ($dialog.ShowDialog() -eq 'OK') { $dialog.SelectedPath }";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return null;
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var initialDir = !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath) 
                    ? $"default location \"{initialPath}\"" 
                    : "";
                    
                var script = $@"tell application ""Finder""
                    activate
                    set theFolder to choose folder with prompt ""Select Folder"" {initialDir}
                    return POSIX path of theFolder
                end tell";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return null;
                
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
            else
            {
                // Linux - use zenity
                var args = "--file-selection --directory --title=\"Select Folder\"";
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                    args += $" --filename=\"{initialPath}/\"";
                    
                var psi = new ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return null;
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Files", $"Failed to browse folder: {ex.Message}");
            return null;
        }
    }

    // News - matches Go implementation
    public async Task<List<NewsItemResponse>> GetNewsAsync(int count) => await _newsService.GetNewsAsync(count);
    
    /// <summary>
    /// Synchronous wrapper for GetNewsAsync to maintain compatibility with frontend.
    /// </summary>
    public Task<List<NewsItemResponse>> GetNews(int count) => GetNewsAsync(count);

    /// <summary>
    /// Cleans news excerpt by removing HTML tags, duplicate title, and date prefixes.
    /// From PR #294
    /// </summary>
    private static string CleanNewsExcerpt(string? rawExcerpt, string? title)
    {
        var excerpt = HttpUtility.HtmlDecode(rawExcerpt ?? "");
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return "";
        }

        // Remove HTML tags
        excerpt = Regex.Replace(excerpt, @"<[^>]+>", " ");
        excerpt = Regex.Replace(excerpt, @"\s+", " ").Trim();

        // Remove title prefix if present
        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalizedTitle = Regex.Replace(title.Trim(), @"\s+", " ");
            var escapedTitle = Regex.Escape(normalizedTitle);
            excerpt = Regex.Replace(excerpt, $@"^\s*{escapedTitle}\s*[:\-–—]?\s*", "", RegexOptions.IgnoreCase);
        }

        // Remove date prefixes like "January 30, 2026 –"
        excerpt = Regex.Replace(excerpt, @"^\s*\p{L}+\s+\d{1,2},\s*\d{4}\s*[–—\-:]?\s*", "", RegexOptions.IgnoreCase);
        excerpt = Regex.Replace(excerpt, @"^\s*\d{1,2}\s+\p{L}+\s+\d{4}\s*[–—\-:]?\s*", "", RegexOptions.IgnoreCase);
        excerpt = Regex.Replace(excerpt, @"^[\-–—:\s]+", "");
        
        // Add space between lowercase and uppercase (fix run-together words)
        excerpt = Regex.Replace(excerpt, @"(\p{Ll})(\p{Lu})", "$1: $2");

        return excerpt.Trim();
    }

    // Update - download latest launcher per platform instead of in-place update
    public async Task<bool> UpdateAsync(JsonElement[]? args)
    {
        // TESTING: Using TEST repo instead of HyPrism
        const string releasesPage = "https://github.com/yyyumeniku/HyPrism/releases/latest";

        try
        {
            var launcherBranch = GetLauncherBranch();
            var isBetaChannel = launcherBranch == "beta";
            var currentVersion = GetLauncherVersion();
            
            // Get all releases to find the best match for user's channel
            var apiUrl = "https://api.github.com/repos/yyyumeniku/HyPrism/releases?per_page=50";
            var json = await HttpClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            
            // Find the best release for the user's channel
            JsonElement? targetRelease = null;
            string? targetVersion = null;
            
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseVal) && prereleaseVal.GetBoolean();
                
                // Match channel
                if (isBetaChannel && !isPrerelease) continue; // Beta wants prereleases
                if (!isBetaChannel && isPrerelease) continue; // Stable wants releases
                
                var tagName = release.GetProperty("tag_name").GetString();
                if (string.IsNullOrWhiteSpace(tagName)) continue;
                
                var version = ParseVersionFromTag(tagName);
                if (string.IsNullOrWhiteSpace(version)) continue;
                
                // Take the first matching release (they're sorted newest first)
                if (targetRelease == null)
                {
                    targetRelease = release;
                    targetVersion = version;
                    break;
                }
            }
            
            if (!targetRelease.HasValue || string.IsNullOrWhiteSpace(targetVersion))
            {
                Logger.Error("Update", $"No suitable {(isBetaChannel ? "pre-release" : "release")} found");
                BrowserOpenURL(releasesPage);
                return false;
            }
            
            Logger.Info("Update", $"Downloading {(isBetaChannel ? "pre-release" : "release")} {targetVersion} (current: {currentVersion})");
            
            var assets = targetRelease.Value.GetProperty("assets");

            // Pick asset by platform/arch
            string? targetAsset = null;
            var arch = RuntimeInformation.ProcessArchitecture;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                targetAsset = "macos-arm64.dmg"; // Apple Silicon only
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                targetAsset = "windows-x64.exe";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                targetAsset = arch == Architecture.Arm64 ? "linux-arm64.tar.gz" : "linux-x64.AppImage";
            }

            if (string.IsNullOrWhiteSpace(targetAsset))
            {
                Logger.Warning("Update", "Unsupported OS for auto-download, opening releases page");
                BrowserOpenURL(releasesPage);
                return false;
            }

            string? downloadUrl = null;
            string? assetName = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name) && name.Contains(targetAsset, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
            {
                Logger.Error("Update", "Could not find matching asset in latest release; opening releases page");
                BrowserOpenURL(releasesPage);
                return false;
            }

            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadsDir);
            var targetPath = Path.Combine(downloadsDir, assetName);

            Logger.Info("Update", $"Downloading latest launcher to {targetPath}");
            using (var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            // Platform-specific installation
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    Logger.Info("Update", "Mounting DMG and installing...");
                    
                    // Mount the DMG
                    var mountProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "hdiutil",
                        Arguments = $"attach \"{targetPath}\" -nobrowse -readonly",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    
                    if (mountProcess == null)
                    {
                        throw new Exception("Failed to mount DMG");
                    }
                    
                    await mountProcess.WaitForExitAsync();
                    var mountOutput = await mountProcess.StandardOutput.ReadToEndAsync();
                    
                    // Parse mount point from hdiutil output (last line, last column)
                    var mountPoint = mountOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .LastOrDefault()?
                        .Split('\t', StringSplitOptions.RemoveEmptyEntries)
                        .LastOrDefault()?
                        .Trim();
                    
                    if (string.IsNullOrWhiteSpace(mountPoint) || !Directory.Exists(mountPoint))
                    {
                        throw new Exception($"Could not find mount point. Output: {mountOutput}");
                    }
                    
                    Logger.Info("Update", $"DMG mounted at: {mountPoint}");
                    
                    // Find the .app in the mounted DMG
                    var appInDmg = Directory.GetDirectories(mountPoint, "*.app").FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(appInDmg) || !Directory.Exists(appInDmg))
                    {
                        Process.Start("hdiutil", $"detach \"{mountPoint}\" -force");
                        throw new Exception("No .app found in DMG");
                    }
                    
                    // Get current app path
                    var currentExe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(currentExe))
                    {
                        Process.Start("hdiutil", $"detach \"{mountPoint}\" -force");
                        throw new Exception("Could not determine current executable path");
                    }
                    
                    // Navigate up to get the .app bundle path
                    // currentExe is likely: /Applications/HyPrism.app/Contents/MacOS/HyPrism
                    var currentAppPath = currentExe;
                    for (int i = 0; i < 3; i++) // Go up 3 levels to get to .app
                    {
                        currentAppPath = Path.GetDirectoryName(currentAppPath);
                        if (string.IsNullOrEmpty(currentAppPath)) break;
                    }
                    
                    if (string.IsNullOrEmpty(currentAppPath) || !currentAppPath.EndsWith(".app"))
                    {
                        Process.Start("hdiutil", $"detach \"{mountPoint}\" -force");
                        throw new Exception($"Could not determine .app path from: {currentExe}");
                    }
                    
                    Logger.Info("Update", $"Current app: {currentAppPath}");
                    Logger.Info("Update", $"New app: {appInDmg}");
                    
                    // Create update script to replace app and restart
                    var updateScript = Path.Combine(Path.GetTempPath(), "hyprism_update.sh");
                    var scriptContent = $@"#!/bin/bash
sleep 2
rm -rf ""{currentAppPath}""
cp -R ""{appInDmg}"" ""{currentAppPath}""
hdiutil detach ""{mountPoint}"" -force
rm -f ""{targetPath}""
open ""{currentAppPath}""
rm -f ""$0""
";
                    
                    File.WriteAllText(updateScript, scriptContent);
                    Process.Start("chmod", $"+x \"{updateScript}\"")?.WaitForExit();
                    
                    // Start the update script and exit
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"\"{updateScript}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    
                    Logger.Info("Update", "Update script started, exiting launcher...");
                    Environment.Exit(0);
                }
                catch (Exception macEx)
                {
                    Logger.Error("Update", $"Auto-update failed: {macEx.Message}");
                    // Fallback: open the DMG manually
                    try { Process.Start("open", targetPath); } catch { }
                    throw new Exception($"Please install the update manually from Downloads. {macEx.Message}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    // Get current executable path
                    var currentExe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(currentExe))
                    {
                        Logger.Error("Update", "Could not determine current executable path");
                        Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                        return true;
                    }

                    // Create a batch script to replace the exe and restart
                    var batchPath = Path.Combine(Path.GetTempPath(), "hyprism_update.bat");
                    var batchContent = $@"@echo off
timeout /t 2 /nobreak >nul
del ""{currentExe}"" 2>nul
move /y ""{targetPath}"" ""{currentExe}""
start """" ""{currentExe}""
del ""%~f0""
";
                    File.WriteAllText(batchPath, batchContent);

                    // Start the batch script and exit
                    var psi = new ProcessStartInfo
                    {
                        FileName = batchPath,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);

                    // Exit this application so the batch can replace the exe
                    Logger.Info("Update", "Starting update script and exiting...");
                    Environment.Exit(0);
                }
                catch (Exception updateEx)
                {
                    Logger.Warning("Update", $"Auto-update failed, opening Explorer: {updateEx.Message}");
                    Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    var currentExe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(currentExe))
                    {
                        throw new Exception("Could not determine current executable path");
                    }
                    
                    // For AppImage, just replace the file
                    if (targetPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                    {
                        // Make the new AppImage executable
                        Process.Start("chmod", $"+x \"{targetPath}\"")?.WaitForExit();
                        
                        // Create update script
                        var updateScript = Path.Combine(Path.GetTempPath(), "hyprism_update.sh");
                        var scriptContent = $@"#!/bin/bash
sleep 2
rm -f ""{currentExe}""
mv ""{targetPath}"" ""{currentExe}""
chmod +x ""{currentExe}""
""{currentExe}"" &
rm -f ""$0""
";
                        File.WriteAllText(updateScript, scriptContent);
                        Process.Start("chmod", $"+x \"{updateScript}\"")?.WaitForExit();
                        
                        // Start the update script and exit
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "/bin/bash",
                            Arguments = $"\"{updateScript}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        
                        Logger.Info("Update", "Update script started, exiting launcher...");
                        Environment.Exit(0);
                    }
                    else
                    {
                        // For other formats, just open the file manager
                        try { Process.Start("xdg-open", Path.GetDirectoryName(targetPath) ?? ""); } catch { }
                        throw new Exception("Please install the update manually from Downloads.");
                    }
                }
                catch (Exception linuxEx)
                {
                    Logger.Error("Update", $"Auto-update failed: {linuxEx.Message}");
                    try { Process.Start("xdg-open", targetPath); } catch { }
                    throw new Exception($"Please install the update manually from Downloads. {linuxEx.Message}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Update", $"Update failed: {ex.Message}");
            BrowserOpenURL(releasesPage);
            return false;
        }
    }

    // Browser
    public bool BrowserOpenURL(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) return false;
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Music
    public bool GetMusicEnabled() => _config.MusicEnabled;
    
    public bool SetMusicEnabled(bool enabled)
    {
        _config.MusicEnabled = enabled;
        SaveConfig();
        return true;
    }

    // Launcher Branch (release/beta update channel)
    public string GetLauncherBranch() => string.IsNullOrWhiteSpace(_config.LauncherBranch) ? "release" : _config.LauncherBranch;
    
    public bool SetLauncherBranch(string branch)
    {
        var normalizedBranch = branch?.ToLowerInvariant() ?? "release";
        if (normalizedBranch != "release" && normalizedBranch != "beta")
        {
            normalizedBranch = "release";
        }
        _config.LauncherBranch = normalizedBranch;
        SaveConfig();
        Logger.Info("Config", $"Launcher branch set to: {normalizedBranch}");
        return true;
    }

    // Close After Launch setting
    public bool GetCloseAfterLaunch() => _config.CloseAfterLaunch;
    
    public bool SetCloseAfterLaunch(bool enabled)
    {
        _config.CloseAfterLaunch = enabled;
        SaveConfig();
        Logger.Info("Config", $"Close after launch set to: {enabled}");
        return true;
    }

    // Discord Announcements settings
    public bool GetShowDiscordAnnouncements() => _config.ShowDiscordAnnouncements;
    
    public bool SetShowDiscordAnnouncements(bool enabled)
    {
        _config.ShowDiscordAnnouncements = enabled;
        SaveConfig();
        Logger.Info("Config", $"Show Discord announcements set to: {enabled}");
        return true;
    }

    public bool IsAnnouncementDismissed(string announcementId)
    {
        return _config.DismissedAnnouncementIds.Contains(announcementId);
    }

    public bool DismissAnnouncement(string announcementId)
    {
        if (!_config.DismissedAnnouncementIds.Contains(announcementId))
        {
            _config.DismissedAnnouncementIds.Add(announcementId);
            SaveConfig();
            Logger.Info("Discord", $"Announcement {announcementId} dismissed");
        }
        return true;
    }

    // News settings
    public bool GetDisableNews() => _config.DisableNews;
    
    public bool SetDisableNews(bool disabled)
    {
        _config.DisableNews = disabled;
        SaveConfig();
        Logger.Info("Config", $"News disabled set to: {disabled}");
        return true;
    }

    // Background settings
    public string GetBackgroundMode() => _config.BackgroundMode;
    
    public bool SetBackgroundMode(string mode)
    {
        _config.BackgroundMode = mode;
        SaveConfig();
        Logger.Info("Config", $"Background mode set to: {mode}");
        return true;
    }

    // Accent color settings
    public string GetAccentColor() => _config.AccentColor;
    
    public bool SetAccentColor(string color)
    {
        _config.AccentColor = color;
        SaveConfig();
        Logger.Info("Config", $"Accent color set to: {color}");
        return true;
    }

    // Onboarding state
    public bool GetHasCompletedOnboarding() => _config.HasCompletedOnboarding;
    
    public bool SetHasCompletedOnboarding(bool completed)
    {
        _config.HasCompletedOnboarding = completed;
        SaveConfig();
        Logger.Info("Config", $"Onboarding completed: {completed}");
        return true;
    }

    /// <summary>
    /// Generates a random username for the onboarding flow.
    /// </summary>
    public string GetRandomUsername()
    {
        return GenerateRandomUsername();
    }

    /// <summary>
    /// Resets the onboarding so it will show again on next launch.
    /// </summary>
    public bool ResetOnboarding()
    {
        _config.HasCompletedOnboarding = false;
        SaveConfig();
        Logger.Info("Config", "Onboarding reset - will show on next launch");
        return true;
    }

    // Online mode settings
    public bool GetOnlineMode() => _config.OnlineMode;
    
    public bool SetOnlineMode(bool online)
    {
        _config.OnlineMode = online;
        SaveConfig();
        Logger.Info("Config", $"Online mode set to: {online}");
        return true;
    }
    
    // Auth domain settings
    public string GetAuthDomain() => _config.AuthDomain;
    
    public bool SetAuthDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = "sessions.sanasol.ws";
        }
        _config.AuthDomain = domain;
        SaveConfig();
        Logger.Info("Config", $"Auth domain set to: {domain}");
        return true;
    }

    /// <summary>
    /// Gets the list of available background filenames
    /// </summary>
    public List<string> GetAvailableBackgrounds()
    {
        var backgrounds = new List<string>();
        // These match the backgrounds in the frontend assets
        for (int i = 1; i <= 30; i++)
        {
            backgrounds.Add($"bg_{i}");
        }
        return backgrounds;
    }

    // Launcher Data Directory settings
    public string GetLauncherDataDirectory() => _config.LauncherDataDirectory;
    
    public Task<string?> SetLauncherDataDirectoryAsync(string path)
    {
        try
        {
            // If path is empty or whitespace, clear the custom launcher data directory
            if (string.IsNullOrWhiteSpace(path))
            {
                _config.LauncherDataDirectory = "";
                SaveConfig();
                Logger.Success("Config", "Launcher data directory cleared, will use default on next restart");
                return Task.FromResult<string?>(null);
            }

            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());

            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.GetFullPath(expanded);
            }

            // Just save the path, the change takes effect on next restart
            _config.LauncherDataDirectory = expanded;
            SaveConfig();

            Logger.Success("Config", $"Launcher data directory set to {expanded} (takes effect on restart)");
            return Task.FromResult<string?>(expanded);
        }
        catch (Exception ex)
        {
            Logger.Error("Config", $"Failed to set launcher data directory: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    // Delegate to ModService
    public List<InstalledMod> GetInstanceInstalledMods(string instancePath) => 
        ModService.GetInstanceInstalledMods(instancePath);
    
    /// <summary>
    /// Convenience overload that gets installed mods by branch and version.
    /// </summary>
    public List<InstalledMod> GetInstanceInstalledMods(string branch, int version)
    {
        var instancePath = GetInstancePath(branch, version);
        return ModService.GetInstanceInstalledMods(instancePath);
    }
    
    /// <summary>
    /// Opens the instance folder in the file manager.
    /// </summary>
    public bool OpenInstanceFolder(string branch, int version)
    {
        try
        {
            var instancePath = GetInstancePath(branch, version);
            if (!Directory.Exists(instancePath))
            {
                Logger.Warning("Files", $"Instance folder does not exist: {instancePath}");
                return false;
            }
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"\"{instancePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{instancePath}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start("xdg-open", $"\"{instancePath}\"");
            }
            
            Logger.Success("Files", $"Opened instance folder: {instancePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Files", $"Failed to open instance folder: {ex.Message}");
            return false;
        }
    }

    // CurseForge API constants
    private const string CurseForgeBaseUrl = "https://api.curseforge.com/v1";
    private const int HytaleGameId = 70216; // Hytale game ID on CurseForge
    private const string CurseForgeApiKey = "$2a$10$bL4bIL5pUWqfcO7KQtnMReakwtfHbNKh6v1uTpKlzhwoueEJQnPnm";

    // Mod Manager with CurseForge API
    public async Task<ModSearchResult> SearchModsAsync(string query, int page, int pageSize, string[] categories, int sortField, int sortOrder)
        => await _modService.SearchModsAsync(query, page, pageSize, categories, sortField, sortOrder);

    public async Task<ModFilesResult> GetModFilesAsync(string modId, int page, int pageSize)
        => await _modService.GetModFilesAsync(modId, page, pageSize);

    public async Task<List<ModCategory>> GetModCategoriesAsync()
        => await _modService.GetModCategoriesAsync();

    /// <summary>
    /// Browse for mod files using native OS dialog.
    /// Returns array of selected file paths or empty array if cancelled.
    /// </summary>
    public async Task<string[]> BrowseModFilesAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use osascript for macOS file picker
                var script = @"tell application ""Finder""
                    activate
                    set theFiles to choose file with prompt ""Select Mod Files"" of type {""jar"", ""zip"", ""hmod"", ""litemod"", ""json""} with multiple selections allowed
                    set filePaths to """"
                    repeat with aFile in theFiles
                        set filePaths to filePaths & POSIX path of aFile & ""\n""
                    end repeat
                    return filePaths
                end tell";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return Array.Empty<string>();
                
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use PowerShell to show file picker on Windows with multiselect
                var script = @"Add-Type -AssemblyName System.Windows.Forms; $dialog = New-Object System.Windows.Forms.OpenFileDialog; $dialog.Filter = 'Mod Files (*.jar;*.zip;*.hmod;*.litemod;*.json)|*.jar;*.zip;*.hmod;*.litemod;*.json|All Files (*.*)|*.*'; $dialog.Multiselect = $true; $dialog.Title = 'Select Mod Files'; if ($dialog.ShowDialog() -eq 'OK') { $dialog.FileNames -join ""`n"" }";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return Array.Empty<string>();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (!string.IsNullOrWhiteSpace(output))
                {
                    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();
                }
            }
            else
            {
                // Linux - use zenity
                var psi = new ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments = "--file-selection --multiple --title=\"Select Mod Files\" --file-filter=\"Mod Files | *.jar *.zip *.hmod *.litemod *.json\" --separator=\"\\n\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return Array.Empty<string>();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();
                }
            }
            
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Logger.Warning("Files", $"Failed to browse files: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Triggers a test Discord announcement popup for developer testing.
    /// </summary>
    public DiscordAnnouncement? GetTestAnnouncement()
    {
        return new DiscordAnnouncement
        {
            Id = "test-announcement-" + DateTime.UtcNow.Ticks,
            AuthorName = "HyPrism Bot",
            AuthorAvatar = null,
            AuthorRole = "Developer",
            RoleColor = "#FFA845",
            Content = "🎉 This is a test announcement!\n\nThis is used to preview how Discord announcements will appear in the launcher. You can dismiss this by clicking the X button or disabling announcements.\n\n✨ Features:\n• Author info with avatar\n• Role colors\n• Images and attachments\n• Smooth animations",
            ImageUrl = null,
            Timestamp = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>
    /// Sets the game language by copying translated language files to the game's language folder.
    /// Maps launcher locale codes to game locale codes and copies appropriate language files.
    /// </summary>
    /// <param name="languageCode">The launcher language code (e.g., "en", "es", "de", "fr")</param>
    /// <returns>True if language files were successfully copied, false otherwise</returns>
    public async Task<bool> SetGameLanguageAsync(string languageCode)
    {
        try
        {
            // Map launcher language codes to game language folder names
            var languageMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "en", "en-US" },
                { "es", "es-ES" },
                { "de", "de-DE" },
                { "fr", "fr-FR" },
                { "ja", "ja-JP" },
                { "ko", "ko-KR" },
                { "pt", "pt-BR" },
                { "ru", "ru-RU" },
                { "tr", "tr-TR" },
                { "uk", "uk-UA" },
                { "zh", "zh-CN" },
                { "be", "be-BY" }
            };

            // Get the game language code
            if (!languageMapping.TryGetValue(languageCode, out var gameLanguageCode))
            {
                Logger.Warning("Language", $"Unknown language code: {languageCode}, defaulting to en-US");
                gameLanguageCode = "en-US";
            }

            Logger.Info("Language", $"Setting game language to: {gameLanguageCode}");

            // Find the game language source directory
            // First check if running from published app (game-lang folder next to executable)
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            var exeDir = !string.IsNullOrEmpty(exePath) ? Path.GetDirectoryName(exePath) : null;
            
            string? sourceLangDir = null;
            
            // Check various possible locations
            var possibleLocations = new List<string>();
            
            if (!string.IsNullOrEmpty(exeDir))
            {
                possibleLocations.Add(Path.Combine(exeDir, "game-lang", gameLanguageCode));
                possibleLocations.Add(Path.Combine(exeDir, "..", "Resources", "game-lang", gameLanguageCode)); // macOS bundle
            }
            
            // Development location
            possibleLocations.Add(Path.Combine(AppContext.BaseDirectory, "game-lang", gameLanguageCode));
            possibleLocations.Add(Path.Combine(AppContext.BaseDirectory, "assets", "game-lang", gameLanguageCode));
            
            foreach (var loc in possibleLocations)
            {
                if (Directory.Exists(loc))
                {
                    sourceLangDir = loc;
                    break;
                }
            }

            if (sourceLangDir == null)
            {
                Logger.Warning("Language", $"Language files not found for {gameLanguageCode}. Checked: {string.Join(", ", possibleLocations)}");
                return false;
            }

            Logger.Info("Language", $"Found language files at: {sourceLangDir}");

            // Get all installed game versions and update their language files
            var branches = new[] { "release", "pre-release" };
            int copiedCount = 0;

            foreach (var branch in branches)
            {
                try
                {
                    var versions = await GetVersionListAsync(branch);
                    foreach (var version in versions)
                    {
                        var instancePath = GetInstancePath(branch, version);
                        var targetLangDir = Path.Combine(instancePath, "Client", "Data", "Shared", "language", gameLanguageCode);

                        if (!Directory.Exists(instancePath))
                            continue;

                        // Create target language directory
                        Directory.CreateDirectory(targetLangDir);

                        // Copy all language files
                        await CopyDirectoryRecursiveAsync(sourceLangDir, targetLangDir);
                        copiedCount++;
                        Logger.Info("Language", $"Copied language files to: {targetLangDir}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("Language", $"Failed to update language for branch {branch}: {ex.Message}");
                }
            }

            // Also update "latest" instance if it exists
            foreach (var branch in branches)
            {
                try
                {
                    var latestPath = GetLatestInstancePath(branch);
                    if (Directory.Exists(latestPath))
                    {
                        var targetLangDir = Path.Combine(latestPath, "Client", "Data", "Shared", "language", gameLanguageCode);
                        Directory.CreateDirectory(targetLangDir);
                        await CopyDirectoryRecursiveAsync(sourceLangDir, targetLangDir);
                        copiedCount++;
                        Logger.Info("Language", $"Copied language files to latest: {targetLangDir}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("Language", $"Failed to update latest language for branch {branch}: {ex.Message}");
                }
            }

            Logger.Info("Language", $"Successfully updated language files for {copiedCount} game instance(s)");
            return copiedCount > 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Language", $"Failed to set game language: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Recursively copies all files from source directory to target directory.
    /// </summary>
    private async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Copy files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetPath = Path.Combine(targetDir, fileName);
            await Task.Run(() => File.Copy(file, targetPath, overwrite: true));
        }

        // Copy subdirectories
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            await CopyDirectoryRecursiveAsync(dir, targetSubDir);
        }
    }

    /// <summary>
    /// Gets the list of available game languages that have translation files.
    /// </summary>
    public List<string> GetAvailableGameLanguages()
    {
        var languages = new List<string>();
        
        // Check for game-lang folders
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        var exeDir = !string.IsNullOrEmpty(exePath) ? Path.GetDirectoryName(exePath) : null;
        
        var possibleBaseDirs = new List<string>();
        
        if (!string.IsNullOrEmpty(exeDir))
        {
            possibleBaseDirs.Add(Path.Combine(exeDir, "game-lang"));
            possibleBaseDirs.Add(Path.Combine(exeDir, "..", "Resources", "game-lang"));
        }
        
        possibleBaseDirs.Add(Path.Combine(AppContext.BaseDirectory, "game-lang"));
        possibleBaseDirs.Add(Path.Combine(AppContext.BaseDirectory, "assets", "game-lang"));
        
        foreach (var baseDir in possibleBaseDirs)
        {
            if (Directory.Exists(baseDir))
            {
                foreach (var dir in Directory.GetDirectories(baseDir))
                {
                    var langCode = Path.GetFileName(dir);
                    if (!languages.Contains(langCode))
                    {
                        languages.Add(langCode);
                    }
                }
                break; // Use first found location
            }
        }
        
        return languages;
    }
}

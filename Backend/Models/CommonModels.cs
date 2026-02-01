using System.Text.Json.Serialization;

namespace HyPrism.Backend.Models;

/// <summary>
/// Represents a username->UUID mapping for the frontend.
/// </summary>
public class UuidMapping
{
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public bool IsCurrent { get; set; } = false;
}

public class DownloadProgress
{
    public bool Success { get; set; }
    public int Progress { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Status of Rosetta 2 installation on macOS Apple Silicon.
/// </summary>
public class RosettaStatus
{
    public bool NeedsInstall { get; set; }
    public string Message { get; set; } = "";
    public string Command { get; set; } = "";
    public string? TutorialUrl { get; set; }
}

/// <summary>
/// Represents a cosmetic item from Assets.zip
/// </summary>
public class CosmeticItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Detailed information about an installed game instance.
/// </summary>
public class InstalledVersionInfo
{
    public int Version { get; set; }
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public long UserDataSize { get; set; }
    public bool HasUserData { get; set; }
}

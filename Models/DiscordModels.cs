using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HyPrism.Models;

public class DiscordMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("content")]
    public string? Content { get; set; }
    
    [JsonPropertyName("author")]
    public DiscordAuthor? Author { get; set; }
    
    [JsonPropertyName("member")]
    public DiscordMember? Member { get; set; }
    
    [JsonPropertyName("attachments")]
    public List<DiscordAttachment>? Attachments { get; set; }
    
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
    
    [JsonPropertyName("reactions")]
    public List<DiscordReaction>? Reactions { get; set; }
}

public class DiscordReaction
{
    [JsonPropertyName("count")]
    public int Count { get; set; }
    
    [JsonPropertyName("me")]
    public bool Me { get; set; }
    
    [JsonPropertyName("emoji")]
    public DiscordEmoji? Emoji { get; set; }
}

public class DiscordEmoji
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class DiscordAuthor
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("username")]
    public string? Username { get; set; }
    
    [JsonPropertyName("global_name")]
    public string? GlobalName { get; set; }
    
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }
}

public class DiscordMember
{
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }
    
    [JsonPropertyName("nick")]
    public string? Nick { get; set; }
}

public class DiscordAttachment
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }
}

/// <summary>
/// Announcement data fetched from Discord channel.
/// </summary>
public class DiscordAnnouncement
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string? AuthorAvatar { get; set; }
    public string? AuthorRole { get; set; }
    public string? RoleColor { get; set; }
    public string? ImageUrl { get; set; }
    public string Timestamp { get; set; } = "";
}

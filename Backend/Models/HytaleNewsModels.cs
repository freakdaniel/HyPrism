using System.Text.Json.Serialization;

namespace HyPrism.Backend.Models;

// News models matching Hytale API
public class HytaleNewsItem
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("bodyExcerpt")]
    public string? BodyExcerpt { get; set; }
    
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
    
    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; set; }
    
    [JsonPropertyName("coverImage")]
    public CoverImage? CoverImage { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }
}

public class CoverImage
{
    [JsonPropertyName("s3Key")]
    public string? S3Key { get; set; }
}

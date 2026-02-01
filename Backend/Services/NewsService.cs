using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using HyPrism.Backend.Models;

namespace HyPrism.Backend.Services;

/// <summary>
/// Fetches news from Hytale API
/// </summary>
public class NewsService
{
    private readonly HttpClient _httpClient;
    private const string HytaleNewsUrl = "https://hytale.com/api/blog/post/published";
    
    public NewsService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "HyPrism/2.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }
    
    public async Task<List<NewsItemResponse>> GetNewsAsync(int count = 10)
    {
        try
        {
            Logger.Info("News", "Fetching news from Hytale API...");
            var response = await _httpClient.GetStringAsync(HytaleNewsUrl);
            
            Logger.Info("News", $"Received response, length: {response.Length}");
            
            var jsonDoc = JsonDocument.Parse(response);
            
            var posts = jsonDoc.RootElement.ValueKind == JsonValueKind.Array 
                ? jsonDoc.RootElement 
                : jsonDoc.RootElement.GetProperty("data");
            
            var news = new List<NewsItemResponse>();
            
            var itemCount = 0;
            foreach (var post in posts.EnumerateArray())
            {
                if (itemCount >= count) break;
                
                try
                {
                    if (itemCount == 0)
                    {
                        Logger.Info("News", $"First post keys: {string.Join(", ", post.EnumerateObject().Select(p => p.Name))}");
                    }
                    
                    var title = post.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                    var excerpt = post.TryGetProperty("bodyExcerpt", out var excerptProp) ? excerptProp.GetString() : null;
                    
                    if (string.IsNullOrEmpty(excerpt))
                    {
                        excerpt = post.TryGetProperty("excerpt", out var excerptProp2) ? excerptProp2.GetString() : null;
                    }
                    var slug = post.TryGetProperty("slug", out var slugProp) ? slugProp.GetString() : null;
                    var publishedAt = post.TryGetProperty("publishedAt", out var pubProp) ? pubProp.GetString() : null;
                    
                    string? imageUrl = null;
                    if (post.TryGetProperty("coverImage", out var img) && img.ValueKind == JsonValueKind.Object)
                    {
                        if (img.TryGetProperty("url", out var urlProp))
                        {
                            imageUrl = urlProp.GetString();
                        }
                    }
                    
                    news.Add(new NewsItemResponse
                    {
                        Title = title ?? "",
                        Excerpt = CleanNewsExcerpt(excerpt, title),
                        Url = !string.IsNullOrEmpty(slug) ? $"https://hytale.com/news/{slug}" : "",
                        Date = publishedAt ?? "",
                        Author = "Hytale Team",
                        ImageUrl = imageUrl
                    });
                    
                    itemCount++;
                }
                catch (Exception ex)
                {
                    Logger.Warning("News", $"Failed to parse news item: {ex.Message}");
                    continue;
                }
            }
            
            Logger.Success("News", $"Successfully fetched {news.Count} news items");
            return news;
        }
        catch (Exception ex)
        {
            Logger.Error("News", $"Failed to fetch news: {ex.Message}");
            Logger.Error("News", $"Stack trace: {ex.StackTrace}");
            return new List<NewsItemResponse>();
        }
    }
    
    /// <summary>
    /// Cleans news excerpt by removing HTML tags, duplicate title, and date prefixes.
    /// </summary>
    private static string CleanNewsExcerpt(string? rawExcerpt, string? title)
    {
        var excerpt = HttpUtility.HtmlDecode(rawExcerpt ?? "");
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return "";
        }

        excerpt = Regex.Replace(excerpt, @"<[^>]+>", " ");
        excerpt = Regex.Replace(excerpt, @"\s+", " ").Trim();

        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalizedTitle = Regex.Replace(title.Trim(), @"\s+", " ");
            var escapedTitle = Regex.Escape(normalizedTitle);
            excerpt = Regex.Replace(excerpt, $@"^\s*{escapedTitle}\s*[:\-–—]?\s*", "", RegexOptions.IgnoreCase);
        }

        excerpt = Regex.Replace(excerpt, @"^\s*\p{L}+\s+\d{1,2},\s*\d{4}\s*[–—\-:]?\s*", "", RegexOptions.IgnoreCase);
        excerpt = Regex.Replace(excerpt, @"^\s*\d{1,2}\s+\p{L}+\s+\d{4}\s*[–—\-:]?\s*", "", RegexOptions.IgnoreCase);
        excerpt = Regex.Replace(excerpt, @"^[\-–—:\s]+", "");
        excerpt = Regex.Replace(excerpt, @"(\p{Ll})(\p{Lu})", "$1: $2");

        return excerpt.Trim();
    }
}

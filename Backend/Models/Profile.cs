using System;

namespace HyPrism.Backend.Models;

/// <summary>
/// A user profile with UUID and display name.
/// </summary>
public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}


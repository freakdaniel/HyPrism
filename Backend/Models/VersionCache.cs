using System;
using System.Collections.Generic;

namespace HyPrism.Backend.Models;

/// <summary>
/// Cache for version information to avoid checking from version 1 every time.
/// </summary>
public class VersionCache
{
    public Dictionary<string, List<int>> KnownVersions { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}

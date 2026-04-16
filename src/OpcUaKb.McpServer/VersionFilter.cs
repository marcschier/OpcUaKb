using System.Text;

/// <summary>
/// Shared version filtering logic for MCP tools.
/// Supports: latest (default), previous, oldest, specific version, all_versions.
/// Implements two-pass retrieval: latest first, fallback to older if too few results.
/// </summary>
static class VersionFilter
{
    /// <summary>Valid version_mode values for tool parameters.</summary>
    public const string ModeDescription =
        "Version mode: 'latest' (default, current spec version only), " +
        "'previous' (one version before latest), " +
        "'oldest' (earliest available version), " +
        "'all' (search across all versions), " +
        "or a specific version like 'v104', 'v105', 'v200'";

    /// <summary>
    /// Builds the OData filter clause for version filtering.
    /// Returns null if no version filter should be applied.
    /// </summary>
    public static string? BuildVersionFilter(string? versionMode, string? specVersion)
    {
        // Explicit spec_version takes priority
        if (!string.IsNullOrWhiteSpace(specVersion))
            return $"spec_version eq '{specVersion}'";

        return (versionMode?.ToLowerInvariant()) switch
        {
            null or "" or "latest" => "is_latest eq true",
            "previous" => "version_rank eq 2",
            "oldest" => "version_rank gt 1",  // will sort desc by rank to get oldest
            "all" => null,  // no filter
            _ when versionMode.StartsWith('v') => $"spec_version eq '{versionMode}'",
            _ => "is_latest eq true",
        };
    }

    /// <summary>
    /// Two-pass search: first with version filter, then without if too few results.
    /// Returns results and whether fallback was used.
    /// </summary>
    public static async Task<(List<SearchService.SearchResult> results, bool usedFallback)> SearchWithFallbackAsync(
        SearchService search, string? query, List<string> baseFilters,
        IEnumerable<string>? select, int top,
        string? versionMode, string? specVersion, int minResultsForFallback = 3)
    {
        var versionFilter = BuildVersionFilter(versionMode, specVersion);
        var filters = new List<string>(baseFilters);
        if (versionFilter != null)
            filters.Add(versionFilter);

        var filter = string.Join(" and ", filters);
        var results = await search.SearchAsync(
            string.IsNullOrWhiteSpace(query) ? "*" : query,
            filter, select, top);

        // If we got enough results or no version filter was applied, return
        if (results.Count >= minResultsForFallback || versionFilter == null)
            return (results, false);

        // Fallback: search without version filter
        var fallbackFilter = string.Join(" and ", baseFilters);
        var fallbackResults = await search.SearchAsync(
            string.IsNullOrWhiteSpace(query) ? "*" : query,
            fallbackFilter, select, top);

        return (fallbackResults, true);
    }

    /// <summary>Appends a version context note to the output.</summary>
    public static void AppendVersionNote(StringBuilder sb, string? versionMode, string? specVersion, bool usedFallback)
    {
        if (usedFallback)
            sb.AppendLine("ℹ️ Few results in latest version — showing results from all versions.");
        else if (!string.IsNullOrWhiteSpace(specVersion))
            sb.AppendLine($"📌 Filtered to version: {specVersion}");
        else if (versionMode is "previous")
            sb.AppendLine("📌 Showing results from the previous spec version.");
        else if (versionMode is "oldest")
            sb.AppendLine("📌 Showing results from the oldest available spec version.");
        else if (versionMode is "all")
            sb.AppendLine("📌 Showing results from all spec versions.");
    }
}

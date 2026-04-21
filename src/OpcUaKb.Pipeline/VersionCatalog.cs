using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// Version Catalog — parses the crawled reference.opcfoundation.org main
// page to build a spec → version → date → rank mapping.
// ═══════════════════════════════════════════════════════════════════════

sealed class VersionCatalog
{
    readonly Dictionary<string, VersionEntry> _byPathPrefix = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Metadata for a single version of a specification.</summary>
    internal sealed class VersionEntry
    {
        public required string SpecName { get; init; }
        public required string VersionLabel { get; init; }
        public required string PathPrefix { get; init; }
        public required DateOnly PublishDate { get; init; }
        public required int Rank { get; init; }  // 1 = latest
        public bool IsLatest => Rank == 1;
    }

    public IReadOnlyDictionary<string, VersionEntry> Entries => _byPathPrefix;

    /// <summary>
    /// Builds the catalog by parsing the crawled main page (index.html) from blob storage.
    /// Falls back to an empty catalog if the blob doesn't exist.
    /// </summary>
    public static async Task<VersionCatalog> BuildFromCrawledPageAsync(
        string storageConnectionString, ILogger logger)
    {
        var catalog = new VersionCatalog();

        try
        {
            var container = new BlobContainerClient(storageConnectionString, "opcua-content");
            var blob = container.GetBlobClient("index.html");

            if (!await blob.ExistsAsync())
            {
                logger.LogWarning("[VERSION] Crawled index.html not found — version catalog empty");
                return catalog;
            }

            var dl = await blob.DownloadContentAsync();
            var html = dl.Value.Content.ToString();
            catalog.ParseMainPage(html, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[VERSION] Failed to build version catalog: {Error}", ex.Message);
        }

        return catalog;
    }

    /// <summary>
    /// Looks up a blob path and returns its version entry, or null if not in catalog.
    /// </summary>
    public VersionEntry? Lookup(string blobPath)
    {
        // Normalize: remove leading slashes, use forward slash
        var normalized = blobPath.Replace('\\', '/').TrimStart('/');

        // Try progressively shorter prefixes to find a match
        // e.g., "Core/Part3/v105/docs/5.8.3/index.html" → try "Core/Part3/v105/docs/5.8.3", "Core/Part3/v105/docs", "Core/Part3/v105"
        var segments = normalized.Split('/');
        for (int len = segments.Length; len >= 2; len--)
        {
            var prefix = string.Join('/', segments[..len]);
            if (_byPathPrefix.TryGetValue(prefix, out var entry))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Extracts (spec_part, spec_version) from a blob path using path segments.
    /// More robust than the previous regex approach.
    /// </summary>
    public static (string specPart, string specVersion) ExtractSpecInfoFromPath(string blobPath)
    {
        var normalized = blobPath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Pattern 1: "Core/Part3/v105/docs/..." → Part3, v105
        // Pattern 2: "DI/v104/docs/..." → DI, v104
        // Pattern 3: "Robotics/v200/docs/..." → Robotics, v200
        // Pattern 4: "UAFX/Part84/v100/docs/..." → UAFX-Part84, v100
        // Pattern 5: "opcconnect_opcfoundation_org/..." → opcconnect, Unknown
        // Pattern 6: "opcfoundation_org/..." → opcfoundation_org, Unknown

        string specPart = "Unknown";
        string specVersion = "Unknown";

        for (int i = 0; i < segments.Length; i++)
        {
            // Find the version segment (starts with v followed by digits)
            if (Regex.IsMatch(segments[i], @"^v\d+\w*$", RegexOptions.IgnoreCase))
            {
                specVersion = segments[i];

                // Everything before the version segment is the spec identity
                if (i == 0)
                {
                    // Version is first segment (e.g., "v104/Core/docs/Part9/...")
                    // Look ahead for a "Part*" or spec name segment after "Core/docs/"
                    for (int j = i + 1; j < segments.Length; j++)
                    {
                        if (segments[j].StartsWith("Part", StringComparison.OrdinalIgnoreCase))
                        {
                            specPart = segments[j];
                            break;
                        }
                        // Stop at known non-spec segments
                        if (segments[j].Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                            segments[j].Equals("Core", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // First meaningful segment after Core/docs that isn't a "Part" —
                        // could be a companion spec name like "DI", "Pumps"
                        if (!segments[j].Contains('.') && segments[j].Length > 1)
                        {
                            specPart = segments[j];
                            break;
                        }
                    }
                }
                else if (i == 1)
                {
                    // Single segment before version: "DI/v104" → DI
                    specPart = segments[0];
                }
                else
                {
                    // Multiple segments before version: "Core/Part3/v105" → Part3
                    // But "UAFX/Part84/v100" → Part84
                    // Use the last segment before version if it starts with "Part"
                    specPart = segments[i - 1].StartsWith("Part", StringComparison.OrdinalIgnoreCase)
                        ? segments[i - 1]
                        : segments[i - 1];
                }
                break;
            }
        }

        // If no version found, try to extract spec from first meaningful segment
        if (specPart == "Unknown" && segments.Length > 0)
        {
            var first = segments[0];
            // Don't use version-like segments as spec names
            if (!first.Contains('_') && !first.Contains('.') && first.Length > 1
                && !Regex.IsMatch(first, @"^v\d+\w*$", RegexOptions.IgnoreCase))
                specPart = first;
        }

        return (specPart, specVersion);
    }

    void ParseMainPage(string html, ILogger logger)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // Each spec is an accordion-item with:
        //   <h2><button>OPC 10000-3: UA Part 3: Address Space Model</button></h2>
        //   <div class="accordion-collapse">
        //     <div class="accordion-body"> (one per version, first = latest)
        //       <a href="/Core/Part3/v105/docs/">1.05.06</a>
        //       <span>2025-10-31</span>
        //     </div>
        //   </div>
        var items = doc.QuerySelectorAll(".accordion-item");
        int totalSpecs = 0, totalVersions = 0;

        foreach (var item in items)
        {
            var title = item.QuerySelector(".accordion-button")?.TextContent.Trim() ?? "";
            var bodies = item.QuerySelectorAll(".accordion-body");

            int rank = 0;
            foreach (var body in bodies)
            {
                rank++;
                var link = body.QuerySelector("a.nav-link");
                var dateSpan = body.QuerySelector("span");
                if (link == null) continue;

                var href = link.GetAttribute("href")?.Trim('/') ?? "";
                var versionLabel = link.TextContent.Trim();
                var dateStr = dateSpan?.TextContent.Trim() ?? "";

                if (string.IsNullOrEmpty(href)) continue;

                var (specPart, _) = ExtractSpecInfoFromPath(href);
                DateOnly.TryParse(dateStr, out var publishDate);

                var entry = new VersionEntry
                {
                    SpecName = specPart,
                    VersionLabel = versionLabel,
                    PathPrefix = href.TrimEnd('/'),
                    PublishDate = publishDate,
                    Rank = rank,
                };

                // Register by path prefix (without trailing /docs)
                var key = href.TrimEnd('/');
                _byPathPrefix.TryAdd(key, entry);

                // Also register with /docs suffix for matching
                _byPathPrefix.TryAdd(key + "/docs", entry);

                totalVersions++;
            }
            if (rank > 0) totalSpecs++;
        }

        logger.LogInformation(
            "[VERSION] Catalog built: {Specs} specs, {Versions} versions, {Latest} latest",
            totalSpecs, totalVersions, _byPathPrefix.Values.Count(v => v.IsLatest));
    }
}

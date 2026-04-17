using System.ComponentModel;
using System.Text;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ModelContextProtocol.Server;

[McpServerToolType]
static class ListSpecsTool
{
    [McpServerTool(Name = "list_specs"),
     Description("List all indexed OPC UA companion specifications and CloudLibrary NodeSets. " +
        "Returns a catalog with name, source (opcfoundation vs cloudlib), version, " +
        "publication date, namespace URI, popularity (download count), and short description. " +
        "Use this to answer questions like 'what specs are indexed?', 'what CloudLib NodeSets are in the KB?', " +
        "or 'which CloudLib NodeSets aren't already covered by the official companion specs?' " +
        "Filter by source='cloudlib' with unique_to_source=true to see only CloudLib entries whose namespace " +
        "is NOT already in the crawled opcfoundation index. " +
        "Results default to popularity-sorted (downloads desc). By default returns only latest versions.")]
    public static async Task<string> ListSpecs(
        SearchService search,
        [Description("Filter by source: 'opcfoundation' (official specs) or 'cloudlib' (UA-CloudLibrary). Omit for all.")] string? source = null,
        [Description("When true AND source='cloudlib', only return cloudlib entries whose namespace is NOT already in the opcfoundation index. Answers 'which CloudLib nodesets aren't already in our companion spec coverage?'")] bool unique_to_source = false,
        [Description("Version mode: 'latest' (default), 'all' to include all versions")] string? version_mode = "latest",
        [Description("Sort order: 'popularity' (default — most downloaded first), 'name' (alphabetical), 'date' (newest publication first)")] string? order_by = "popularity",
        [Description("Max results (1-500, default 200)")] int top = 200)
    {
        top = Math.Clamp(top, 1, 500);

        var filters = new List<string>
        {
            "(content_type eq 'nodeset_summary' or content_type eq 'cloudlib_summary')",
            "spec_part ne 'AllSpecs'",
        };

        if (!string.IsNullOrWhiteSpace(source))
            filters.Add($"source eq '{source.ToLowerInvariant()}'");

        if (unique_to_source && string.Equals(source, "cloudlib", StringComparison.OrdinalIgnoreCase))
            filters.Add("in_opcfoundation_index eq false");

        var mode = (version_mode ?? "latest").Trim().ToLowerInvariant();
        if (mode == "latest" || string.IsNullOrEmpty(mode))
            filters.Add("is_latest eq true");

        var filter = string.Join(" and ", filters);

        var orderBy = (order_by ?? "popularity").Trim().ToLowerInvariant();
        var orderByClause = orderBy switch
        {
            "name" => "spec_part asc",
            "date" => "publication_date desc",
            _ => "popularity desc",
        };

        var options = new SearchOptions
        {
            Filter = filter,
            Size = top,
            IncludeTotalCount = true,
            OrderBy = { orderByClause },
        };
        foreach (var f in new[] { "spec_part", "spec_version", "source", "title", "description", "publication_date", "namespace_uri", "is_latest", "version_rank", "popularity" })
            options.Select.Add(f);

        var response = await search.Client.SearchAsync<SearchDocument>("*", options);
        var totalCount = response.Value.TotalCount ?? 0;

        var rows = new List<(string Source, string SpecPart, string Version, string? Title, string? Desc, DateTimeOffset? Pub, string? Ns, long Popularity)>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var d = r.Document;
            var src = d.GetString("source") ?? "";
            var specPart = d.GetString("spec_part") ?? "";
            var ver = d.GetString("spec_version") ?? "";
            var title = d.GetString("title");
            var desc = d.GetString("description");
            var ns = d.GetString("namespace_uri");
            DateTimeOffset? pub = d.TryGetValue("publication_date", out var pv) && pv is DateTimeOffset dto ? dto : null;
            long pop = 0;
            if (d.TryGetValue("popularity", out var popV) && popV != null)
            {
                if (popV is long l) pop = l;
                else if (popV is int i) pop = i;
                else if (long.TryParse(popV.ToString(), out var parsed)) pop = parsed;
            }

            rows.Add((src, specPart, ver, title, desc, pub, ns, pop));
        }

        if (rows.Count == 0)
            return $"No specs found matching source='{source ?? "any"}', version_mode='{mode}'.";

        var sb = new StringBuilder();
        var opcfCount = rows.Count(r => r.Source == "opcfoundation");
        var cloudCount = rows.Count(r => r.Source == "cloudlib");
        var header = $"Indexed specifications — returned: {rows.Count} of {totalCount} matching";
        if (unique_to_source)
            header += " (cloudlib entries NOT already in opcfoundation index)";
        sb.AppendLine(header);
        sb.AppendLine($"  opcfoundation: {opcfCount}, cloudlib: {cloudCount}; sorted by {orderBy}");
        sb.AppendLine();

        foreach (var r in rows)
        {
            var name = !string.IsNullOrEmpty(r.Title) ? r.Title : r.SpecPart;
            var head = $"• [{r.Source}] {name}";
            if (!string.IsNullOrEmpty(r.Version)) head += $" v{r.Version}";
            if (r.Pub.HasValue) head += $" ({r.Pub.Value:yyyy-MM-dd})";
            // Only show download count for cloudlib (opcfoundation uses synthetic max popularity)
            if (r.Source == "cloudlib" && r.Popularity > 0) head += $" — {r.Popularity:N0} downloads";
            sb.AppendLine(head);

            if (name != r.SpecPart)
                sb.AppendLine($"    spec_part: {r.SpecPart}");
            if (!string.IsNullOrEmpty(r.Ns))
                sb.AppendLine($"    namespace: {r.Ns}");
            if (!string.IsNullOrEmpty(r.Desc))
            {
                var d = r.Desc.Length > 240 ? r.Desc[..240] + "…" : r.Desc;
                sb.AppendLine($"    {d}");
            }
        }

        return sb.ToString();
    }
}

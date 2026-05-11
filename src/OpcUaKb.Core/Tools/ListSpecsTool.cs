using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ModelContextProtocol.Server;

[McpServerToolType]
static partial class ListSpecsTool
{
    [McpServerTool(Name = "list_specs"),
     Description("List all indexed OPC UA companion specifications and CloudLibrary NodeSets. " +
        "Returns a ranked catalog with name, source (opcfoundation vs cloudlib), version, node count, " +
        "publication date, namespace URI, popularity (download count), and short description. " +
        "Use this to answer questions like 'what specs are indexed?', 'what CloudLib NodeSets are in the KB?', " +
        "or 'which CloudLib NodeSets aren't already covered by the official companion specs?' " +
        "Filter by source='cloudlib' with unique_to_source=true to see only CloudLib entries whose namespace " +
        "is NOT already in the opcfoundation index OR whose version differs from the opcfoundation version. " +
        "Results default to popularity-sorted (downloads desc). By default returns only latest versions.")]
    public static async Task<string> ListSpecs(
        SearchService search,
        [Description("Filter by source: 'opcfoundation' (official specs) or 'cloudlib' (UA-CloudLibrary). Omit for all.")] string? source = null,
        [Description("When true AND source='cloudlib', only return cloudlib entries whose namespace is NOT already " +
            "in the opcfoundation index OR whose version differs from the opcfoundation version. " +
            "Answers 'which CloudLib nodesets aren't already in our companion spec coverage?'")] bool unique_to_source = false,
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

        // unique_to_source filtering is done client-side for version-aware comparison

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

        // Fetch all matching summaries (small corpus), apply top after client-side filtering
        var options = new SearchOptions
        {
            Filter = filter,
            Size = 500,
            IncludeTotalCount = true,
            OrderBy = { orderByClause },
        };
        foreach (var f in new[] { "spec_part", "spec_version", "source", "title", "description",
            "publication_date", "namespace_uri", "is_latest", "version_rank", "popularity", "page_chunk" })
            options.Select.Add(f);

        var response = await search.Client.SearchAsync<SearchDocument>("*", options);

        var rows = new List<SpecRow>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var d = r.Document;
            var chunk = d.GetString("page_chunk") ?? "";
            var nodeCountMatch = NodeCountRegex().Match(chunk);

            rows.Add(new SpecRow(
                Source: d.GetString("source") ?? "",
                SpecPart: d.GetString("spec_part") ?? "",
                Version: d.GetString("spec_version") ?? "",
                Title: d.GetString("title"),
                Desc: d.GetString("description"),
                Pub: d.TryGetValue("publication_date", out var pv) && pv is DateTimeOffset dto ? dto : null,
                Ns: d.GetString("namespace_uri"),
                Popularity: ReadLong(d, "popularity"),
                NodeCount: nodeCountMatch.Success ? int.Parse(nodeCountMatch.Groups[1].Value) : 0
            ));
        }

        if (rows.Count == 0)
            return $"No specs found matching source='{source ?? "any"}', version_mode='{mode}'.";

        // Cross-query opcfoundation summaries when showing cloudlib — enables version comparison
        Dictionary<string, List<string>>? opcfVersionsByNs = null;
        bool isCloudLib = string.Equals(source, "cloudlib", StringComparison.OrdinalIgnoreCase);
        if (isCloudLib || unique_to_source)
        {
            opcfVersionsByNs = new(StringComparer.OrdinalIgnoreCase);
            // If we already queried both sources (source=null), extract from existing rows
            if (string.IsNullOrWhiteSpace(source))
            {
                foreach (var r in rows.Where(r => r.Source == "opcfoundation" && !string.IsNullOrEmpty(r.Ns)))
                    AddVersion(opcfVersionsByNs, r.Ns!, r.Version);
            }
            else
            {
                // Query opcfoundation summaries separately
                var opcfOptions = new SearchOptions
                {
                    Filter = "content_type eq 'nodeset_summary' and spec_part ne 'AllSpecs' and is_latest eq true",
                    Size = 500,
                };
                foreach (var f in new[] { "namespace_uri", "spec_version" })
                    opcfOptions.Select.Add(f);
                var opcfResp = await search.Client.SearchAsync<SearchDocument>("*", opcfOptions);
                await foreach (var r in opcfResp.Value.GetResultsAsync())
                {
                    var ns = r.Document.GetString("namespace_uri") ?? "";
                    var ver = r.Document.GetString("spec_version") ?? "";
                    if (!string.IsNullOrEmpty(ns)) AddVersion(opcfVersionsByNs, ns, ver);
                }
            }
        }

        // Client-side filtering for unique_to_source (version-aware)
        if (unique_to_source && opcfVersionsByNs != null)
        {
            rows = rows.Where(r =>
            {
                if (r.Source != "cloudlib") return false;
                if (string.IsNullOrEmpty(r.Ns)) return true; // no namespace → keep as unique
                var nsNorm = r.Ns.TrimEnd('/');
                if (!opcfVersionsByNs.TryGetValue(nsNorm, out var opcfVersions))
                    return true; // namespace not in opcfoundation → unique
                if (string.IsNullOrEmpty(r.Version))
                    return false; // can't compare versions → treat as duplicate
                return !opcfVersions.Contains(r.Version); // version not in opcfoundation → unique
            }).ToList();
        }

        // Apply top limit after client-side filtering
        var totalFiltered = rows.Count;
        if (rows.Count > top)
            rows = rows.Take(top).ToList();

        var sb = new StringBuilder();
        var opcfCount = rows.Count(r => r.Source == "opcfoundation");
        var cloudCount = rows.Count(r => r.Source == "cloudlib");
        var header = $"Indexed specifications — returned: {rows.Count} of {totalFiltered} matching";
        if (unique_to_source)
            header += " (cloudlib entries NOT in opcfoundation or with different version)";
        sb.AppendLine(header);
        sb.AppendLine($"  opcfoundation: {opcfCount}, cloudlib: {cloudCount}; sorted by {orderBy}");
        sb.AppendLine();

        int rank = 0;
        foreach (var r in rows)
        {
            rank++;
            var name = !string.IsNullOrEmpty(r.Title) ? r.Title : r.SpecPart;
            var head = $"{rank}. [{r.Source}] {name}";
            if (!string.IsNullOrEmpty(r.Version)) head += $" v{r.Version}";
            if (r.Pub.HasValue) head += $" ({r.Pub.Value:yyyy-MM-dd})";
            if (r.Source == "cloudlib" && r.Popularity > 0) head += $" — {r.Popularity:N0} downloads";
            sb.AppendLine(head);

            if (name != r.SpecPart)
                sb.AppendLine($"    spec_part: {r.SpecPart}");
            if (!string.IsNullOrEmpty(r.Ns))
                sb.AppendLine($"    namespace: {r.Ns}");
            if (r.NodeCount > 0)
                sb.AppendLine($"    nodes: {r.NodeCount:N0}");

            // Version comparison annotation for cloudlib entries
            if (opcfVersionsByNs != null && r.Source == "cloudlib" && !string.IsNullOrEmpty(r.Ns))
            {
                var nsNorm = r.Ns.TrimEnd('/');
                if (opcfVersionsByNs.TryGetValue(nsNorm, out var opcfVers) && opcfVers.Count > 0)
                {
                    var opcfVer = opcfVers[0]; // latest/first
                    if (!string.IsNullOrEmpty(r.Version) && !string.IsNullOrEmpty(opcfVer))
                    {
                        if (r.Version == opcfVer)
                            sb.AppendLine($"    opcfoundation: same version (v{opcfVer})");
                        else
                            sb.AppendLine($"    opcfoundation: v{opcfVer} — version differs");
                    }
                    else
                        sb.AppendLine($"    opcfoundation: also indexed");
                }
                else
                    sb.AppendLine($"    opcfoundation: not indexed — unique to CloudLibrary");
            }

            if (!string.IsNullOrEmpty(r.Desc))
            {
                var d = r.Desc.Length > 200 ? r.Desc[..200] + "…" : r.Desc;
                sb.AppendLine($"    {d}");
            }
        }

        return sb.ToString();
    }

    static void AddVersion(Dictionary<string, List<string>> map, string ns, string version)
    {
        var key = ns.TrimEnd('/');
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        if (!string.IsNullOrEmpty(version) && !list.Contains(version))
            list.Add(version);
    }

    static long ReadLong(SearchDocument d, string field)
    {
        if (!d.TryGetValue(field, out var v) || v == null) return 0;
        if (v is long l) return l;
        if (v is int i) return i;
        return long.TryParse(v.ToString(), out var p) ? p : 0;
    }

    record SpecRow(string Source, string SpecPart, string Version, string? Title, string? Desc,
        DateTimeOffset? Pub, string? Ns, long Popularity, int NodeCount);

    [GeneratedRegex(@"Total nodes:\s*(\d[\d,]*)")]
    private static partial Regex NodeCountRegex();
}

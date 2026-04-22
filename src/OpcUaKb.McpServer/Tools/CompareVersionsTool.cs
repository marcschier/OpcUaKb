using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CompareVersionsTool
{
    [McpServerTool(Name = "compare_versions"),
     Description("Compare two versions of an OPC UA companion specification's NodeSet to identify " +
        "added, removed, and changed nodes. Classifies changes as backward-compatible or breaking " +
        "per OPC 11030 §3 (Rules for backward compatibility). " +
        "Use this to assess migration impact between spec versions.")]
    public static async Task<string> CompareVersions(
        SearchService search,
        [Description("Companion spec name (e.g., DI, Pumps, PlasticsRubber)")] string spec,
        [Description("Older version (e.g., v104)")] string old_version,
        [Description("Newer version (e.g., v105)")] string new_version,
        [Description("Node class to compare (optional, e.g., ObjectType, Variable). Default: all.")] string? node_class = null)
    {
        // Fetch nodes from old version — try opcfoundation first, then cloudlib
        string contentType = "nodeset";
        var oldNodes = await FetchVersionNodes(search, spec, old_version, contentType, node_class);
        var newNodes = await FetchVersionNodes(search, spec, new_version, contentType, node_class);

        // Fallback to cloudlib if opcfoundation has no data
        if (oldNodes.Count == 0 && newNodes.Count == 0)
        {
            contentType = "cloudlib_nodeset";
            oldNodes = await FetchVersionNodes(search, spec, old_version, contentType, node_class);
            newNodes = await FetchVersionNodes(search, spec, new_version, contentType, node_class);
        }

        // If still no version-specific data, try version_rank for cloudlib
        if (oldNodes.Count == 0 && newNodes.Count == 0)
        {
            var prevFilter = $"content_type eq 'cloudlib_nodeset' and spec_part eq '{spec}' and version_rank eq 2";
            var latestFilter = $"content_type eq 'cloudlib_nodeset' and spec_part eq '{spec}' and version_rank eq 1";
            if (!string.IsNullOrWhiteSpace(node_class))
            {
                prevFilter += $" and node_class eq '{node_class}'";
                latestFilter += $" and node_class eq '{node_class}'";
            }
            var select = new[] { "browse_name", "node_class", "parent_type", "modelling_rule", "data_type" };
            oldNodes = await search.SearchAsync("*", prevFilter, select, 1000);
            newNodes = await search.SearchAsync("*", latestFilter, select, 1000);
        }

        if (oldNodes.Count == 0 && newNodes.Count == 0)
            return $"No nodes found for spec '{spec}' in either {old_version} or {new_version}.";

        // Build lookup by browse_name + node_class + parent_type
        var oldSet = oldNodes.ToDictionary(
            n => MakeKey(n.Document),
            n => n.Document);
        var newSet = newNodes.ToDictionary(
            n => MakeKey(n.Document),
            n => n.Document);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();
        int breakingCount = 0;

        // Find added nodes (in new but not old)
        foreach (var (key, doc) in newSet)
        {
            if (!oldSet.ContainsKey(key))
            {
                var mr = doc.GetString("modelling_rule");
                var nc = doc.GetString("node_class");
                var isBreaking = mr == "Mandatory" && nc is not "ObjectType" and not "VariableType" and not "DataType";
                if (isBreaking) breakingCount++;
                added.Add($"  + {FormatNode(doc)}{(isBreaking ? " ⚠️ BREAKING (new mandatory member)" : "")}");
            }
        }

        // Find removed nodes (in old but not new)
        foreach (var (key, doc) in oldSet)
        {
            if (!newSet.ContainsKey(key))
            {
                breakingCount++;
                removed.Add($"  - {FormatNode(doc)} ⚠️ BREAKING (removed)");
            }
        }

        // Find changed nodes (same key, different attributes)
        foreach (var (key, oldDoc) in oldSet)
        {
            if (newSet.TryGetValue(key, out var newDoc))
            {
                var diffs = new List<string>();
                CompareField(oldDoc, newDoc, "modelling_rule", "ModellingRule", diffs);
                CompareField(oldDoc, newDoc, "data_type", "DataType", diffs);
                if (diffs.Count > 0)
                {
                    var name = oldDoc.GetString("browse_name");
                    breakingCount++;
                    changed.Add($"  ~ {name}: {string.Join(", ", diffs)} ⚠️ BREAKING");
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Version Comparison: {spec} {old_version} → {new_version}");
        sb.AppendLine();
        sb.AppendLine($"Old version nodes: {oldNodes.Count}");
        sb.AppendLine($"New version nodes: {newNodes.Count}");
        sb.AppendLine($"Added: {added.Count} | Removed: {removed.Count} | Changed: {changed.Count}");
        sb.AppendLine($"**Breaking changes: {breakingCount}**");
        sb.AppendLine();

        if (breakingCount == 0 && added.Count == 0 && removed.Count == 0 && changed.Count == 0)
        {
            sb.AppendLine("✅ No differences found between versions.");
        }
        else
        {
            if (removed.Count > 0)
            {
                sb.AppendLine("### Removed Nodes (always breaking — OPC 11030 §3.2.16)");
                foreach (var r in removed) sb.AppendLine(r);
                sb.AppendLine();
            }
            if (changed.Count > 0)
            {
                sb.AppendLine("### Changed Nodes");
                foreach (var c in changed) sb.AppendLine(c);
                sb.AppendLine();
            }
            if (added.Count > 0)
            {
                sb.AppendLine("### Added Nodes");
                sb.AppendLine("Adding mandatory members to existing types is breaking (OPC 11030 §3.2.1).");
                sb.AppendLine("Adding optional members is non-breaking (OPC 11030 §3.2.2).");
                foreach (var a in added) sb.AppendLine(a);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    static string MakeKey(Azure.Search.Documents.Models.SearchDocument doc)
    {
        var name = doc.GetString("browse_name") ?? "";
        var nc = doc.GetString("node_class") ?? "";
        var parent = doc.GetString("parent_type") ?? "";
        return $"{nc}|{name}|{parent}";
    }

    static string FormatNode(Azure.Search.Documents.Models.SearchDocument doc)
    {
        var name = doc.GetString("browse_name");
        var nc = doc.GetString("node_class");
        var mr = doc.GetString("modelling_rule");
        var parent = doc.GetString("parent_type");
        var parts = new List<string> { $"{name} [{nc}]" };
        if (!string.IsNullOrEmpty(parent)) parts.Add($"in {parent}");
        if (!string.IsNullOrEmpty(mr)) parts.Add($"({mr})");
        return string.Join(" ", parts);
    }

    static void CompareField(Azure.Search.Documents.Models.SearchDocument oldDoc,
        Azure.Search.Documents.Models.SearchDocument newDoc,
        string field, string label, List<string> diffs)
    {
        var oldVal = oldDoc.GetString(field) ?? "";
        var newVal = newDoc.GetString(field) ?? "";
        if (!oldVal.Equals(newVal, StringComparison.Ordinal))
            diffs.Add($"{label}: '{oldVal}' → '{newVal}'");
    }

    static async Task<List<SearchService.SearchResult>> FetchVersionNodes(
        SearchService search, string spec, string version, string contentType, string? nodeClass)
    {
        var filters = new List<string>
        {
            $"content_type eq '{contentType}'",
            $"spec_part eq '{spec}'",
            $"spec_version eq '{version}'"
        };
        if (!string.IsNullOrWhiteSpace(nodeClass))
            filters.Add($"node_class eq '{nodeClass}'");
        var select = new[] { "browse_name", "node_class", "parent_type", "modelling_rule", "data_type" };
        return await search.SearchAsync("*", string.Join(" and ", filters), select, 1000);
    }
}

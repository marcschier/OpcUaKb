using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class SearchNodesTool
{
    [McpServerTool(Name = "search_nodes"),
     Description("Search OPC UA NodeSet nodes with structured filters. " +
        "Use this to find specific ObjectTypes, Variables, Methods, or DataTypes " +
        "by name, parent type, companion spec, or modelling rule. " +
        "By default searches only the latest spec version; use version_mode to control. " +
        "Returns a deduplicated list of matching nodes with browse name, node class, spec, " +
        "version, parent type, modelling rule, and data type.")]
    public static async Task<string> SearchNodes(
        SearchService search,
        [Description("Text query to search node names and descriptions")] string? query = null,
        [Description("Filter by node class.")] NodeClass? node_class = null,
        [Description("Filter by companion spec name (e.g., DI, PlasticsRubber, Pumps)")] string? spec = null,
        [Description("Filter by parent type browse name")] string? parent_type = null,
        [Description("Filter by modelling rule.")] ModellingRule? modelling_rule = null,
        [Description("Filter by source (official specs vs UA-CloudLibrary submissions).")] SpecSource? source = null,
        [Description(VersionFilter.ModeDescription)] string? version_mode = null,
        [Description("Max results (1-50, default 20)")] int top = 20)
    {
        top = Math.Clamp(top, 1, 50);
        var filters = new List<string> { "(content_type eq 'nodeset' or content_type eq 'cloudlib_nodeset')" };

        if (node_class is { } nodeClassValue)
            filters.Add($"node_class eq '{nodeClassValue}'");
        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add(SpecFilter.Match(spec));
        if (!string.IsNullOrWhiteSpace(parent_type))
            filters.Add($"parent_type eq '{parent_type}'");
        if (modelling_rule is { } modellingRuleValue)
            filters.Add($"modelling_rule eq '{modellingRuleValue}'");
        if (source is { } sourceValue)
            filters.Add($"source eq '{sourceValue.Wire()}'");

        var select = new[] { "browse_name", "node_class", "spec_part", "spec_id", "spec_version", "parent_type", "modelling_rule", "data_type", "page_chunk", "is_latest", "version_rank", "source", "namespace_uri" };
        var (results, usedFallback) = await VersionFilter.SearchWithFallbackAsync(
            search, query, filters, select, top, version_mode);

        if (results.Count == 0)
            return "No nodes found matching the criteria.";

        // Deduplicate: when same browse_name + node_class + parent_type exists in both sources, prefer opcfoundation
        var deduped = results
            .GroupBy(r =>
            {
                var d = r.Document;
                return $"{d.GetString("browse_name")}|{d.GetString("node_class")}|{d.GetString("parent_type")}";
            })
            .Select(g => g.OrderBy(r => r.Document.GetString("source") == "opcfoundation" ? 0 : 1).First())
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Found {deduped.Count} node(s):");
        VersionFilter.AppendVersionNote(sb, version_mode, usedFallback);
        sb.AppendLine();

        foreach (var r in deduped)
        {
            var d = r.Document;
            var name = d.GetString("browse_name");
            var nc = d.GetString("node_class");
            var sp = d.GetString("spec_part");
            var sid = d.GetString("spec_id");
            var specLabel = !string.IsNullOrEmpty(sid) ? sid : sp;
            var sv = d.GetString("spec_version");
            var pt = d.GetString("parent_type");
            var mr = d.GetString("modelling_rule");
            var dt = d.GetString("data_type");
            var src = d.GetString("source");
            var chunk = d.GetString("page_chunk");

            var srcTag = string.IsNullOrEmpty(src) ? "" : $" [src:{src}]";
            sb.AppendLine($"• {name} [{nc}] — Spec: {specLabel} ({sv}){srcTag}");
            if (!string.IsNullOrEmpty(pt)) sb.AppendLine($"  Parent: {pt}");
            if (!string.IsNullOrEmpty(mr)) sb.AppendLine($"  ModellingRule: {mr}");
            if (!string.IsNullOrEmpty(dt)) sb.AppendLine($"  DataType: {dt}");
            if (!string.IsNullOrEmpty(chunk)) sb.AppendLine($"  {chunk}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

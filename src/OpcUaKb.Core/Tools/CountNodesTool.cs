using System.ComponentModel;
using System.Text;
using Azure.Search.Documents.Models;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CountNodesTool
{
    [McpServerTool(Name = "count_nodes"),
     Description("Count and aggregate OPC UA NodeSet nodes by facets. " +
        "Returns counts grouped by node class, companion spec, modelling rule, or data type. " +
        "Use this for questions like 'how many Variables per spec?' or " +
        "'what data types are most common?'. By default counts only latest version nodes.")]
    public static async Task<string> CountNodes(
        SearchService search,
        [Description("Facet to group by.")] NodeCountFacet facet,
        [Description("Optional filter by node class.")] NodeClass? node_class = null,
        [Description("Optional filter by companion spec name")] string? spec = null,
        [Description("Optional filter by modelling rule")] ModellingRule? modelling_rule = null,
        [Description("Optional filter by source.")] SpecSource? source = null,
        [Description(VersionFilter.ModeDescription)] string? version_mode = null,
        [Description("Max facet values to return (default 50)")] int top = 50)
    {
        var facetWire = facet.Wire();
        top = Math.Clamp(top, 1, 100);

        var filters = new List<string> { "(content_type eq 'nodeset' or content_type eq 'cloudlib_nodeset')" };
        if (node_class is { } nc)
            filters.Add($"node_class eq '{nc}'");
        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add(SpecFilter.Match(spec));
        if (modelling_rule is { } mr)
            filters.Add($"modelling_rule eq '{mr}'");
        if (source is { } src)
            filters.Add($"source eq '{src.Wire()}'");

        // Apply version filter
        var versionFilter = VersionFilter.BuildVersionFilter(version_mode);
        if (versionFilter != null)
            filters.Add(versionFilter);

        var filter = string.Join(" and ", filters);

        // For the 'spec_part' facet: prefer the new spec_id field; fall back to legacy spec_part
        // if spec_id is unset on the underlying docs. NodeSet content predates the v2 schema,
        // so spec_id may be null on legacy index docs.
        string facetField = facetWire;
        IList<FacetResult>? facetResults = null;
        if (facet == NodeCountFacet.SpecPart)
        {
            var trySpecId = await search.FacetSearchAsync(filter, [$"spec_id,count:{top}"]);
            if (trySpecId.TryGetValue("spec_id", out var idResults) && idResults.Count > 0)
            {
                facetField = "spec_id";
                facetResults = idResults;
            }
        }

        if (facetResults == null)
        {
            var facets = await search.FacetSearchAsync(filter, [$"{facetWire},count:{top}"]);
            facets.TryGetValue(facetWire, out facetResults);
        }

        if (facetResults == null || facetResults.Count == 0)
            return $"No facet results for '{facet}' with the given filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Node counts by {facetField}:");
        VersionFilter.AppendVersionNote(sb, version_mode, false);
        sb.AppendLine();

        var total = facetResults.Sum(f => f.Count ?? 0);
        foreach (var f in facetResults.OrderByDescending(f => f.Count))
        {
            var pct = total > 0 ? (f.Count ?? 0) * 100.0 / total : 0;
            sb.AppendLine($"  {f.Value}: {f.Count:N0} ({pct:F1}%)");
        }

        sb.AppendLine();
        sb.AppendLine($"Total: {total:N0}");

        return sb.ToString();
    }
}

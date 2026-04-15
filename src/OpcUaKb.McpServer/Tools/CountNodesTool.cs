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
        "'what data types are most common?'. Supports combining a filter with a facet.")]
    public static async Task<string> CountNodes(
        SearchService search,
        [Description("Facet to group by: node_class, spec_part, modelling_rule, data_type")] string facet,
        [Description("Optional filter by node class: ObjectType, Variable, Method, DataType")] string? node_class = null,
        [Description("Optional filter by companion spec name")] string? spec = null,
        [Description("Optional filter by modelling rule")] string? modelling_rule = null,
        [Description("Max facet values to return (default 50)")] int top = 50)
    {
        var validFacets = new HashSet<string> { "node_class", "spec_part", "modelling_rule", "data_type" };
        if (!validFacets.Contains(facet))
            return $"Invalid facet '{facet}'. Must be one of: {string.Join(", ", validFacets)}";

        top = Math.Clamp(top, 1, 100);

        var filters = new List<string> { "content_type eq 'nodeset'" };
        if (!string.IsNullOrWhiteSpace(node_class))
            filters.Add($"node_class eq '{node_class}'");
        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add($"spec_part eq '{spec}'");
        if (!string.IsNullOrWhiteSpace(modelling_rule))
            filters.Add($"modelling_rule eq '{modelling_rule}'");

        var filter = string.Join(" and ", filters);
        var facets = await search.FacetSearchAsync(filter, [$"{facet},count:{top}"]);

        if (!facets.TryGetValue(facet, out var facetResults) || facetResults.Count == 0)
            return $"No facet results for '{facet}' with the given filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Node counts by {facet}:");
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

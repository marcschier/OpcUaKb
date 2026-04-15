using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class SearchNodesTool
{
    [McpServerTool(Name = "search_nodes"),
     Description("Search OPC UA NodeSet nodes with structured filters. " +
        "Use this to find specific ObjectTypes, Variables, Methods, or DataTypes " +
        "by name, parent type, companion spec, or modelling rule. " +
        "Returns browse name, node class, parent type, spec, and description.")]
    public static async Task<string> SearchNodes(
        SearchService search,
        [Description("Text query to search node names and descriptions")] string? query = null,
        [Description("Filter by node class: ObjectType, Variable, Method, DataType, Object, VariableType, ReferenceType")] string? node_class = null,
        [Description("Filter by companion spec name (e.g., DI, PlasticsRubber, Pumps)")] string? spec = null,
        [Description("Filter by parent type browse name")] string? parent_type = null,
        [Description("Filter by modelling rule: Mandatory, Optional, MandatoryPlaceholder, OptionalPlaceholder")] string? modelling_rule = null,
        [Description("Max results (1-50, default 20)")] int top = 20)
    {
        top = Math.Clamp(top, 1, 50);
        var filters = new List<string> { "content_type eq 'nodeset'" };

        if (!string.IsNullOrWhiteSpace(node_class))
            filters.Add($"node_class eq '{node_class}'");
        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add($"spec_part eq '{spec}'");
        if (!string.IsNullOrWhiteSpace(parent_type))
            filters.Add($"parent_type eq '{parent_type}'");
        if (!string.IsNullOrWhiteSpace(modelling_rule))
            filters.Add($"modelling_rule eq '{modelling_rule}'");

        var filter = string.Join(" and ", filters);
        var results = await search.SearchAsync(
            string.IsNullOrWhiteSpace(query) ? "*" : query,
            filter,
            ["browse_name", "node_class", "spec_part", "parent_type", "modelling_rule", "data_type", "page_chunk"],
            top);

        if (results.Count == 0)
            return "No nodes found matching the criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} node(s):");
        sb.AppendLine();

        foreach (var r in results)
        {
            var d = r.Document;
            var name = d.GetString("browse_name");
            var nc = d.GetString("node_class");
            var sp = d.GetString("spec_part");
            var pt = d.GetString("parent_type");
            var mr = d.GetString("modelling_rule");
            var dt = d.GetString("data_type");
            var chunk = d.GetString("page_chunk");

            sb.AppendLine($"• {name} [{nc}] — Spec: {sp}");
            if (!string.IsNullOrEmpty(pt)) sb.AppendLine($"  Parent: {pt}");
            if (!string.IsNullOrEmpty(mr)) sb.AppendLine($"  ModellingRule: {mr}");
            if (!string.IsNullOrEmpty(dt)) sb.AppendLine($"  DataType: {dt}");
            if (!string.IsNullOrEmpty(chunk)) sb.AppendLine($"  {chunk}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

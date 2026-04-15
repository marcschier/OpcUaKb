using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class SpecSummaryTool
{
    [McpServerTool(Name = "get_spec_summary"),
     Description("Get NodeSet statistics for a specific OPC UA companion specification " +
        "or all specs combined. Returns ObjectType/Variable/Method/DataType counts, " +
        "modelling rule breakdown, and top ObjectTypes by member count. " +
        "Use this for aggregation questions like 'how many ObjectTypes in DI?' or " +
        "'which spec has the most Variables?'.")]
    public static async Task<string> GetSpecSummary(
        SearchService search,
        [Description("Companion spec name (e.g., DI, Pumps, PlasticsRubber). Omit for cross-spec master summary.")] string? spec = null)
    {
        string filter;
        string query;

        if (string.IsNullOrWhiteSpace(spec))
        {
            // Cross-spec master summary
            filter = "content_type eq 'nodeset_summary' and spec_part eq 'AllSpecs'";
            query = "*";
        }
        else
        {
            filter = $"content_type eq 'nodeset_summary' and spec_part eq '{spec}'";
            query = "*";
        }

        var results = await search.SearchAsync(query, filter,
            ["section_title", "spec_part", "page_chunk"], 3);

        if (results.Count == 0)
        {
            // Try fuzzy search on spec name
            var fuzzyResults = await search.SearchAsync(
                spec,
                "content_type eq 'nodeset_summary'",
                ["section_title", "spec_part", "page_chunk"],
                5);

            if (fuzzyResults.Count == 0)
                return $"No summary data found for spec '{spec ?? "all"}'. Available specs can be found with count_nodes.";

            var sb2 = new StringBuilder();
            sb2.AppendLine($"No exact match for '{spec}'. Did you mean one of these?");
            sb2.AppendLine();
            foreach (var r in fuzzyResults)
            {
                var chunk = r.Document.GetString("page_chunk");
                if (!string.IsNullOrEmpty(chunk))
                {
                    sb2.AppendLine(chunk);
                    sb2.AppendLine("---");
                }
            }
            return sb2.ToString();
        }

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var chunk = r.Document.GetString("page_chunk");
            if (!string.IsNullOrEmpty(chunk))
            {
                sb.AppendLine(chunk);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}

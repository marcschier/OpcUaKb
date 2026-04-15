using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class SearchDocsTool
{
    [McpServerTool(Name = "search_docs"),
     Description("Search OPC UA specification documentation (HTML pages, tables, diagrams). " +
        "Use this for questions about OPC UA concepts, protocol details, services, " +
        "security models, or any non-NodeSet specification content. " +
        "Returns page title, source URL, and content snippet.")]
    public static async Task<string> SearchDocs(
        SearchService search,
        [Description("Search query about OPC UA specifications")] string query,
        [Description("Optional spec part filter (e.g., Part4, Part5, DI, PackML)")] string? spec = null,
        [Description("Max results (1-20, default 10)")] int top = 10)
    {
        top = Math.Clamp(top, 1, 20);

        var filters = new List<string>
        {
            "content_type ne 'nodeset'",
            "content_type ne 'nodeset_summary'",
            "content_type ne 'nodeset_hierarchy'",
        };

        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add($"spec_part eq '{spec}'");

        var filter = string.Join(" and ", filters);
        var results = await search.SearchAsync(query, filter,
            ["section_title", "source_url", "spec_part", "content_type", "page_chunk"],
            top);

        if (results.Count == 0)
            return "No documentation found matching the query.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s):");
        sb.AppendLine();

        foreach (var r in results)
        {
            var d = r.Document;
            var title = d.GetString("section_title");
            var url = d.GetString("source_url");
            var sp = d.GetString("spec_part");
            var ct = d.GetString("content_type");
            var chunk = d.GetString("page_chunk") ?? "";

            sb.AppendLine($"### {title}");
            sb.AppendLine($"Spec: {sp} | Type: {ct} | URL: {url}");
            // Truncate long chunks
            if (chunk.Length > 500)
                chunk = chunk[..500] + "...";
            sb.AppendLine(chunk);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

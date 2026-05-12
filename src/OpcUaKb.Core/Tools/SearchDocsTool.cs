using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

// ─────────────────────────────────────────────────────────────────────────
// Back-compat note (rollout to opcua-content-index-v2):
// Mixes new spec_section docs with legacy text/table/diagram chunks via a
// single OR-form content_type filter. Example final filter for spec="Part4":
//
//   (content_type eq 'spec_section' or content_type eq 'text'
//      or content_type eq 'table' or content_type eq 'diagram')
//   and (spec_part eq 'Part4' or spec_id eq 'Part4')
//   and is_latest eq true
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
static class SearchDocsTool
{
    [McpServerTool(Name = "search_docs"),
     Description("Search OPC UA specification documentation (HTML pages, tables, diagrams). " +
        "Use this for questions about OPC UA concepts, protocol details, services, " +
        "security models, or any non-NodeSet specification content. " +
        "By default searches the latest spec version; use version_mode to control.")]
    public static async Task<string> SearchDocs(
        SearchService search,
        [Description("Search query about OPC UA specifications")] string query,
        [Description("Optional spec part filter (e.g., Part4, Part5, DI, PackML)")] string? spec = null,
        [Description(VersionFilter.ModeDescription)] string? version_mode = null,
        [Description("Filter by specific spec version (e.g., v104, v105). Overrides version_mode.")] string? spec_version = null,
        [Description("Max results (1-20, default 10)")] int top = 10)
    {
        top = Math.Clamp(top, 1, 20);

        var filters = new List<string>
        {
            "(content_type eq 'spec_section' or content_type eq 'text' or content_type eq 'table' or content_type eq 'diagram')",
        };

        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add(SpecFilter.Match(spec));

        var select = new[]
        {
            "section_title", "source_url", "spec_part", "spec_version", "content_type", "page_chunk",
            "is_latest", "version_rank",
            // New spec_section schema fields (null on legacy text/table/diagram docs)
            "spec_id", "spec_title", "section_id", "section_number", "section_path", "breadcrumb", "figures",
        };
        var (results, usedFallback) = await VersionFilter.SearchWithFallbackAsync(
            search, query, filters, select, top, version_mode, spec_version);

        if (results.Count == 0)
            return "No documentation found matching the query.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s):");
        VersionFilter.AppendVersionNote(sb, version_mode, spec_version, usedFallback);
        sb.AppendLine();

        foreach (var r in results)
        {
            var d = r.Document;
            var title = d.GetString("section_title");
            var url = d.GetString("source_url");
            var sp = d.GetString("spec_part");
            var sv = d.GetString("spec_version");
            var ct = d.GetString("content_type");
            var chunk = d.GetString("page_chunk") ?? "";

            // spec_section-only fields
            var sid = d.GetString("spec_id");
            var stitle = d.GetString("spec_title");
            var secNum = d.GetString("section_number");
            var secPath = d.GetString("section_path");
            var breadcrumb = SpecFilter.ReadStringCollection(d, "breadcrumb");
            var figures = SpecFilter.ReadStringCollection(d, "figures");

            var snippet = chunk.Length > 500 ? chunk[..500] + "..." : chunk;

            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(secNum))
            {
                // New rich spec_section format
                sb.AppendLine($"### {sid} §{secNum} ({title})");
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(stitle)) meta.Add(stitle);
                if (!string.IsNullOrEmpty(sv)) meta.Add($"v{sv}");
                meta.Add($"Type: {ct}");
                sb.AppendLine(string.Join(" | ", meta));
                if (breadcrumb.Count > 0)
                    sb.AppendLine($"Path: {string.Join(" › ", breadcrumb)}");
                else if (!string.IsNullOrEmpty(secPath))
                    sb.AppendLine($"Path: {secPath}");
                sb.AppendLine($"URL: {url}");
                sb.AppendLine(snippet);
                if (figures.Count > 0)
                    sb.AppendLine($"Figures: {string.Join(", ", figures)}");
            }
            else
            {
                // Legacy text/table/diagram chunk
                var specLabel = !string.IsNullOrEmpty(sid) ? sid : sp;
                sb.AppendLine($"### {title}");
                sb.AppendLine($"Spec: {specLabel} ({sv}) | Type: {ct} | URL: {url}");
                sb.AppendLine(snippet);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

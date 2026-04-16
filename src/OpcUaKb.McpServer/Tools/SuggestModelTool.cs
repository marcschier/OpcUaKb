using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class SuggestModelTool
{
    [McpServerTool(Name = "suggest_model"),
     Description("Suggest an OPC UA information model design based on a description of the domain " +
        "or device to model. Recommends ObjectTypes, Variables, Methods, DataTypes, and which " +
        "existing companion specs and base types to reuse (DI, Machinery, IA, etc.). " +
        "Follows OPC 11030 Modelling Best Practices. " +
        "Use this when starting a new companion specification or extending an existing one.")]
    public static async Task<string> SuggestModel(
        SearchService search,
        [Description("Description of the domain, device, or system to model (e.g., 'a CNC milling machine with spindle speed, tool changer, and job management')")] string description,
        [Description("Specific aspect to focus on (optional, e.g., 'state machine', 'alarms', 'data types')")] string? focus = null)
    {
        // Search for relevant existing companion specs and base types
        var specResults = await search.SearchAsync(
            description,
            "content_type eq 'nodeset_summary'",
            ["section_title", "spec_part", "page_chunk"],
            5);

        // Search for relevant ObjectTypes in existing specs
        var typeResults = await search.SearchAsync(
            description,
            "node_class eq 'ObjectType' and content_type eq 'nodeset'",
            ["browse_name", "spec_part", "parent_type", "page_chunk"],
            10);

        // Search best practices documentation
        var bpQuery = !string.IsNullOrWhiteSpace(focus)
            ? $"OPC UA modelling best practices {focus}"
            : "OPC UA modelling best practices ObjectType design composition inheritance";
        var bestPractices = await search.SearchAsync(
            bpQuery,
            "content_type ne 'nodeset' and content_type ne 'nodeset_summary' and content_type ne 'nodeset_hierarchy'",
            ["section_title", "source_url", "page_chunk"],
            5);

        // Search for relevant base types from common specs (DI, Machinery, IA)
        var baseTypeResults = await search.SearchAsync(
            $"{description} device machine",
            "node_class eq 'ObjectType' and (spec_part eq 'DI' or spec_part eq 'Machinery' or spec_part eq 'IA') and content_type eq 'nodeset'",
            ["browse_name", "spec_part", "parent_type", "page_chunk"],
            10);

        var sb = new StringBuilder();
        sb.AppendLine($"## OPC UA Model Suggestions for: {description}");
        sb.AppendLine();

        // Relevant base types to extend
        if (baseTypeResults.Count > 0)
        {
            sb.AppendLine("### Recommended Base Types to Extend");
            sb.AppendLine("These existing types from foundation companion specs should be considered as supertypes:");
            sb.AppendLine();
            foreach (var r in baseTypeResults)
            {
                var d = r.Document;
                var name = d.GetString("browse_name");
                var sp = d.GetString("spec_part");
                var parent = d.GetString("parent_type");
                var chunk = d.GetString("page_chunk") ?? "";
                sb.AppendLine($"- **{name}** ({sp}) — extends {parent}");
                if (chunk.Length > 200) chunk = chunk[..200] + "...";
                sb.AppendLine($"  {chunk}");
            }
            sb.AppendLine();
        }

        // Similar existing ObjectTypes
        if (typeResults.Count > 0)
        {
            sb.AppendLine("### Similar Existing ObjectTypes (for reference/reuse)");
            sb.AppendLine("These existing types model similar concepts and can serve as patterns:");
            sb.AppendLine();
            foreach (var r in typeResults)
            {
                var d = r.Document;
                var name = d.GetString("browse_name");
                var sp = d.GetString("spec_part");
                var parent = d.GetString("parent_type");
                sb.AppendLine($"- **{name}** ({sp}) — extends {parent}");
            }
            sb.AppendLine();
        }

        // Relevant companion specs
        if (specResults.Count > 0)
        {
            sb.AppendLine("### Related Companion Specifications");
            sb.AppendLine("These specs cover similar domains and should be reviewed for reuse:");
            sb.AppendLine();
            foreach (var r in specResults)
            {
                var d = r.Document;
                var title = d.GetString("section_title");
                var sp = d.GetString("spec_part");
                var chunk = d.GetString("page_chunk") ?? "";
                // Extract first 2 lines of the summary
                var lines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var summary = string.Join(" | ", lines.Take(3));
                sb.AppendLine($"- **{title}** ({sp}): {summary}");
            }
            sb.AppendLine();
        }

        // Best practices guidance
        sb.AppendLine("### Modelling Best Practices (OPC 11030)");
        sb.AppendLine();
        sb.AppendLine("Key design decisions to make:");
        sb.AppendLine();
        sb.AppendLine("1. **Type hierarchy** (§7.2.2): Derive from the most specific existing type.");
        sb.AppendLine("   If modelling a device → extend `DeviceType` (DI spec).");
        sb.AppendLine("   If modelling a machine component → extend `ComponentType` (Machinery spec).");
        sb.AppendLine("   If nothing fits → extend `BaseObjectType`.");
        sb.AppendLine();
        sb.AppendLine("2. **Composition vs Inheritance** (§7.2.4): Prefer composition (HasComponent)");
        sb.AppendLine("   over deep inheritance. Use FunctionalGroups to organize related Variables.");
        sb.AppendLine();
        sb.AppendLine("3. **Mandatory vs Optional** (§7.2.7): Make core identity/status Variables");
        sb.AppendLine("   Mandatory. Make configuration and diagnostic Variables Optional.");
        sb.AppendLine();
        sb.AppendLine("4. **Interfaces** (§7.2.6): Use Interfaces (AddIns) for cross-cutting concerns");
        sb.AppendLine("   like IMachineTagNameplateType, IVendorNameplateType.");
        sb.AppendLine();
        sb.AppendLine("5. **Naming** (§2.1): Use PascalCase, suffix types with 'Type',");
        sb.AppendLine("   no underscores, namespace URI ending with '/'.");
        sb.AppendLine();
        sb.AppendLine("6. **Data granularity** (§7.5): Prefer individual Variables for data that");
        sb.AppendLine("   changes independently. Use Structured DataTypes for atomic groups.");
        sb.AppendLine();

        if (bestPractices.Count > 0)
        {
            sb.AppendLine("### Relevant Best Practice Sections");
            foreach (var r in bestPractices)
            {
                var d = r.Document;
                var title = d.GetString("section_title");
                var url = d.GetString("source_url");
                sb.AppendLine($"- [{title}]({url})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### Next Steps");
        sb.AppendLine("1. Define your ObjectType hierarchy based on the base types above");
        sb.AppendLine("2. Add Variables (with DataTypes and ModellingRules) for your domain data");
        sb.AppendLine("3. Add Methods for operations/commands");
        sb.AppendLine("4. Define custom DataTypes (Structures/Enums) as needed");
        sb.AppendLine("5. Use `validate_nodeset` to check your NodeSet against best practices");
        sb.AppendLine("6. Use `check_compliance` to verify implementations match your spec");

        return sb.ToString();
    }
}

using System.ComponentModel;
using System.Text;
using System.Xml.Linq;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CheckComplianceTool
{
    static readonly XNamespace Ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    [McpServerTool(Name = "check_compliance"),
     Description("Check whether a NodeSet XML implementation is compliant with a companion specification's " +
        "type definitions. Compares the implementation against the indexed spec to find: missing mandatory nodes, " +
        "missing optional nodes (info), data type mismatches, incorrect modelling rules, and extra nodes not in the spec. " +
        "Use this to verify that an OPC UA server correctly implements a companion specification.")]
    public static async Task<string> CheckCompliance(
        SearchService search,
        [Description("The NodeSet XML content of the implementation to check")] string nodeset_xml,
        [Description("Companion spec name to check against (e.g., DI, Pumps, PlasticsRubber, Machinery)")] string spec,
        [Description("Specific ObjectType to check compliance for (optional — checks all types if omitted)")] string? object_type = null)
    {
        // Parse the implementation NodeSet
        XDocument xdoc;
        try { xdoc = XDocument.Parse(nodeset_xml); }
        catch (Exception ex) { return $"❌ Failed to parse XML: {ex.Message}"; }

        var root = xdoc.Root;
        if (root == null) return "❌ Invalid XML: no root element";

        // Extract implemented nodes from the provided NodeSet
        var implNodes = new Dictionary<string, ImplNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in root.Elements())
        {
            var localName = el.Name.LocalName;
            if (!IsNodeElement(localName)) continue;

            var browseName = StripNsPrefix(el.Attribute("BrowseName")?.Value ?? "");
            var nodeClass = MapNodeClass(localName);
            var dataType = el.Attribute("DataType")?.Value ?? "";
            var parentNodeId = el.Attribute("ParentNodeId")?.Value;

            // Resolve parent BrowseName
            string parentType = "";
            var refs = el.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [];
            foreach (var r in refs)
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                if (rt is "HasComponent" or "HasProperty" or "HasOrderedComponent"
                    or "i=47" or "i=46" or "i=49"
                    && r.Attribute("IsForward")?.Value != "true")
                {
                    // Try to resolve from nodeIndex
                    parentType = ResolveParentBrowseName(root, r.Value.Trim());
                    break;
                }
            }

            var key = $"{nodeClass}|{browseName}|{parentType}";
            implNodes.TryAdd(key, new ImplNode
            {
                BrowseName = browseName,
                NodeClass = nodeClass,
                ParentType = parentType,
                DataType = dataType,
            });
        }

        // Fetch spec definitions from the index
        var filters = new List<string>
        {
            "content_type eq 'nodeset'",
            $"spec_part eq '{spec}'",
        };
        if (!string.IsNullOrWhiteSpace(object_type))
            filters.Add($"parent_type eq '{object_type}'");

        var select = new[] { "browse_name", "node_class", "parent_type", "modelling_rule", "data_type" };
        var specNodes = await search.SearchAsync("*", string.Join(" and ", filters), select, 1000);

        if (specNodes.Count == 0)
            return $"No nodes found in spec '{spec}'" +
                   (object_type != null ? $" for type '{object_type}'" : "") +
                   ". Check the spec name is correct.";

        // Compare
        var findings = new List<(string severity, string message)>();
        int mandatoryMissing = 0, optionalMissing = 0, matched = 0, extra = 0;
        var specKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var specNode in specNodes)
        {
            var d = specNode.Document;
            var name = d.GetString("browse_name") ?? "";
            var nc = d.GetString("node_class") ?? "";
            var parent = d.GetString("parent_type") ?? "";
            var mr = d.GetString("modelling_rule") ?? "";
            var dt = d.GetString("data_type") ?? "";

            var key = $"{nc}|{name}|{parent}";
            specKeys.Add(key);

            if (implNodes.TryGetValue(key, out var impl))
            {
                matched++;

                // Check data type mismatch
                if (!string.IsNullOrEmpty(dt) && !string.IsNullOrEmpty(impl.DataType)
                    && !dt.Equals(impl.DataType, StringComparison.OrdinalIgnoreCase)
                    && !impl.DataType.Contains(dt))
                {
                    findings.Add(("WARNING",
                        $"DataType mismatch: {name} [{nc}] in {parent} — spec expects '{dt}', implementation has '{impl.DataType}'"));
                }
            }
            else
            {
                // Missing from implementation
                if (mr == "Mandatory")
                {
                    mandatoryMissing++;
                    findings.Add(("ERROR",
                        $"Missing mandatory {nc}: {name} in {parent} (ModellingRule: Mandatory)"));
                }
                else if (mr == "Optional")
                {
                    optionalMissing++;
                    findings.Add(("INFO",
                        $"Optional {nc} not implemented: {name} in {parent}"));
                }
                else if (mr is "MandatoryPlaceholder" or "OptionalPlaceholder")
                {
                    findings.Add(("INFO",
                        $"Placeholder {nc} not implemented: {name} in {parent} ({mr})"));
                }
            }
        }

        // Check for extra nodes not in spec (only for the specific types we're checking)
        if (!string.IsNullOrWhiteSpace(object_type))
        {
            foreach (var (key, impl) in implNodes)
            {
                if (impl.ParentType.Equals(object_type, StringComparison.OrdinalIgnoreCase)
                    && !specKeys.Contains(key))
                {
                    extra++;
                    findings.Add(("INFO",
                        $"Extra {impl.NodeClass} not in spec: {impl.BrowseName} in {impl.ParentType}"));
                }
            }
        }

        // Build report
        var sb = new StringBuilder();
        sb.AppendLine($"## Compliance Report: {spec}");
        if (!string.IsNullOrWhiteSpace(object_type))
            sb.AppendLine($"ObjectType: {object_type}");
        sb.AppendLine();
        sb.AppendLine($"Spec nodes checked: {specNodes.Count}");
        sb.AppendLine($"Implementation nodes parsed: {implNodes.Count}");
        sb.AppendLine($"Matched: {matched} | Missing mandatory: {mandatoryMissing} | Missing optional: {optionalMissing} | Extra: {extra}");
        sb.AppendLine();

        if (mandatoryMissing == 0 && findings.All(f => f.severity != "WARNING"))
        {
            sb.AppendLine($"✅ Implementation is **compliant** with {spec}" +
                (object_type != null ? $" ({object_type})" : "") + ".");
            if (optionalMissing > 0)
                sb.AppendLine($"   ({optionalMissing} optional nodes not implemented — this is acceptable)");
        }
        else
        {
            sb.AppendLine($"❌ Implementation has **{mandatoryMissing} mandatory nodes missing**.");
        }
        sb.AppendLine();

        // Group findings
        foreach (var sev in new[] { "ERROR", "WARNING", "INFO" })
        {
            var group = findings.Where(f => f.severity == sev).ToList();
            if (group.Count == 0) continue;

            var label = sev switch { "ERROR" => "Errors", "WARNING" => "Warnings", _ => "Info" };
            sb.AppendLine($"### {label} ({group.Count})");
            foreach (var (_, msg) in group.Take(50))
                sb.AppendLine($"- {msg}");
            if (group.Count > 50)
                sb.AppendLine($"  ... and {group.Count - 50} more");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    static string ResolveParentBrowseName(XElement root, string targetNodeId)
    {
        foreach (var el in root.Elements())
        {
            var nodeId = el.Attribute("NodeId")?.Value;
            if (nodeId == targetNodeId)
                return StripNsPrefix(el.Attribute("BrowseName")?.Value ?? "");
        }
        return "";
    }

    static bool IsNodeElement(string localName) => localName is
        "UAObjectType" or "UAVariableType" or "UAObject" or "UAVariable"
        or "UAMethod" or "UADataType" or "UAReferenceType" or "UAView";

    static string MapNodeClass(string localName) => localName switch
    {
        "UAObjectType" => "ObjectType",
        "UAVariableType" => "VariableType",
        "UAObject" => "Object",
        "UAVariable" => "Variable",
        "UAMethod" => "Method",
        "UADataType" => "DataType",
        "UAReferenceType" => "ReferenceType",
        "UAView" => "View",
        _ => localName,
    };

    static string StripNsPrefix(string browseName)
    {
        var idx = browseName.IndexOf(':');
        return idx >= 0 && idx < 4 && int.TryParse(browseName[..idx], out _)
            ? browseName[(idx + 1)..] : browseName;
    }

    sealed class ImplNode
    {
        public required string BrowseName { get; init; }
        public required string NodeClass { get; init; }
        public required string ParentType { get; init; }
        public required string DataType { get; init; }
    }
}

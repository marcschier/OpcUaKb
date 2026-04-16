using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ModelContextProtocol.Server;

[McpServerToolType]
static class ValidateNodeSetTool
{
    static readonly XNamespace Ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    static readonly HashSet<string> ValidNodeElements = new(StringComparer.Ordinal)
    {
        "UAObjectType", "UAVariableType", "UAObject", "UAVariable",
        "UAMethod", "UADataType", "UAReferenceType", "UAView",
    };

    // OPC 11030 §2.1.2: BrowseNames should be PascalCase, no underscores
    static readonly Regex PascalCasePattern = new(@"^[A-Z][a-zA-Z0-9]*$", RegexOptions.Compiled);
    static readonly Regex ContainsUnderscore = new(@"_", RegexOptions.Compiled);

    [McpServerTool(Name = "validate_nodeset"),
     Description("Validate an OPC UA NodeSet XML against the OPC UA standard and OPC 11030 " +
        "Modelling Best Practices. Checks naming conventions, modelling rules, type hierarchy, " +
        "reference types, and structural correctness. Returns a report with errors, warnings, " +
        "and informational findings with spec references.")]
    public static string ValidateNodeSet(
        [Description("The NodeSet XML content to validate")] string nodeset_xml)
    {
        var report = new List<Finding>();

        // Parse XML
        XDocument xdoc;
        try
        {
            xdoc = XDocument.Parse(nodeset_xml);
        }
        catch (Exception ex)
        {
            return $"❌ Failed to parse XML: {ex.Message}";
        }

        var root = xdoc.Root;
        if (root == null || root.Name.LocalName != "UANodeSet")
        {
            return "❌ Root element must be <UANodeSet> with namespace http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
        }

        // Collect namespaces
        var namespaceUris = root.Element(Ns + "NamespaceUris")?
            .Elements(Ns + "Uri").Select(e => e.Value).ToList() ?? [];

        // Collect aliases
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in root.Element(Ns + "Aliases")?.Elements(Ns + "Alias") ?? [])
        {
            var name = alias.Attribute("Alias")?.Value;
            var value = alias.Value.Trim();
            if (name != null) aliases[name] = value;
        }

        // §2.5 NamespaceUri conventions
        ValidateNamespaceUris(namespaceUris, report);

        // Collect all nodes
        var nodes = new List<(XElement el, string nodeClass, string nodeId, string browseName)>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var browseNamesByClass = new Dictionary<string, List<string>>();

        foreach (var el in root.Elements())
        {
            var localName = el.Name.LocalName;
            if (!ValidNodeElements.Contains(localName)) continue;

            var nodeId = el.Attribute("NodeId")?.Value ?? "";
            var browseName = el.Attribute("BrowseName")?.Value ?? "";

            // Check for missing NodeId
            if (string.IsNullOrEmpty(nodeId))
            {
                report.Add(Finding.Error($"Node with BrowseName='{browseName}' has no NodeId attribute",
                    "Part 3, §5.2.1"));
                continue;
            }

            // Check for duplicate NodeIds
            if (!nodeIds.Add(nodeId))
            {
                report.Add(Finding.Error($"Duplicate NodeId: {nodeId} (BrowseName='{browseName}')",
                    "Part 3, §5.2.1"));
            }

            // Check for missing BrowseName
            if (string.IsNullOrEmpty(browseName))
            {
                report.Add(Finding.Error($"Node {nodeId} has no BrowseName attribute",
                    "Part 3, §5.2"));
            }

            nodes.Add((el, localName, nodeId, StripNsPrefix(browseName)));

            if (!browseNamesByClass.TryGetValue(localName, out var list))
            {
                list = [];
                browseNamesByClass[localName] = list;
            }
            list.Add(StripNsPrefix(browseName));
        }

        // Validate each node
        foreach (var (el, nodeClass, nodeId, browseName) in nodes)
        {
            ValidateNaming(browseName, nodeClass, nodeId, report);
            ValidateReferences(el, nodeClass, nodeId, browseName, aliases, report);
            ValidateModellingRules(el, nodeClass, nodeId, browseName, report);
            ValidateDataTypeUsage(el, nodeClass, nodeId, browseName, report);
        }

        // Summary stats
        var sb = new StringBuilder();
        var errors = report.Count(f => f.Severity == "ERROR");
        var warnings = report.Count(f => f.Severity == "WARNING");
        var infos = report.Count(f => f.Severity == "INFO");

        sb.AppendLine($"## NodeSet Validation Report");
        sb.AppendLine();
        sb.AppendLine($"Nodes analyzed: {nodes.Count}");
        sb.AppendLine($"Namespace URIs: {namespaceUris.Count}");
        sb.AppendLine($"Aliases: {aliases.Count}");
        sb.AppendLine();
        sb.AppendLine($"**{errors} errors, {warnings} warnings, {infos} info**");
        sb.AppendLine();

        if (errors == 0 && warnings == 0)
        {
            sb.AppendLine("✅ No issues found. The NodeSet appears well-formed and follows best practices.");
        }
        else
        {
            foreach (var group in report.GroupBy(f => f.Severity).OrderBy(g => g.Key))
            {
                sb.AppendLine($"### {group.Key}s ({group.Count()})");
                sb.AppendLine();
                foreach (var f in group)
                {
                    sb.AppendLine($"- {f.Message}");
                    if (!string.IsNullOrEmpty(f.SpecRef))
                        sb.AppendLine($"  📖 {f.SpecRef}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    static void ValidateNamespaceUris(List<string> uris, List<Finding> report)
    {
        foreach (var uri in uris)
        {
            // §2.5: NamespaceUri should end with /
            if (!uri.EndsWith('/'))
            {
                report.Add(Finding.Warning(
                    $"NamespaceUri '{uri}' does not end with '/'. Convention is to end with trailing slash.",
                    "OPC 11030, §2.5"));
            }

            // §2.5: Should use http://opcfoundation.org/UA/ prefix for OPC Foundation specs
            if (uri.Contains("opcfoundation.org") && !uri.StartsWith("http://opcfoundation.org/UA/"))
            {
                report.Add(Finding.Warning(
                    $"NamespaceUri '{uri}' uses opcfoundation.org but doesn't follow 'http://opcfoundation.org/UA/...' convention.",
                    "OPC 11030, §2.5"));
            }
        }
    }

    static void ValidateNaming(string browseName, string nodeClass, string nodeId, List<Finding> report)
    {
        if (string.IsNullOrEmpty(browseName)) return;

        // §2.1.2: BrowseNames should use PascalCase
        if (browseName.Length > 1 && char.IsLower(browseName[0]))
        {
            report.Add(Finding.Warning(
                $"BrowseName '{browseName}' ({nodeId}) starts with lowercase. OPC 11030 recommends PascalCase.",
                "OPC 11030, §2.1.2"));
        }

        // §2.1.2: Avoid underscores in BrowseNames
        if (ContainsUnderscore.IsMatch(browseName))
        {
            report.Add(Finding.Warning(
                $"BrowseName '{browseName}' ({nodeId}) contains underscore(s). OPC 11030 recommends avoiding underscores.",
                "OPC 11030, §2.1.2"));
        }

        // §2.1.3: ObjectTypes and VariableTypes should end with "Type"
        if (nodeClass is "UAObjectType" or "UAVariableType")
        {
            if (!browseName.EndsWith("Type"))
            {
                report.Add(Finding.Warning(
                    $"{nodeClass} BrowseName '{browseName}' ({nodeId}) does not end with 'Type'.",
                    "OPC 11030, §2.1.3"));
            }
        }

        // §2.1.3: DataTypes that are structures/enums: naming conventions
        if (nodeClass == "UADataType" && browseName.EndsWith("Type") &&
            !browseName.EndsWith("DataType"))
        {
            report.Add(Finding.Info(
                $"DataType BrowseName '{browseName}' ({nodeId}) ends with 'Type'. Consider if 'DataType' suffix is more appropriate.",
                "OPC 11030, §2.1.3"));
        }
    }

    static void ValidateReferences(XElement el, string nodeClass, string nodeId,
        string browseName, Dictionary<string, string> aliases, List<Finding> report)
    {
        var refs = el.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [];
        bool hasSubtype = false;
        bool hasModellingRule = false;
        int componentCount = 0, propertyCount = 0;

        foreach (var r in refs)
        {
            var refType = r.Attribute("ReferenceType")?.Value ?? "";
            var isForward = r.Attribute("IsForward")?.Value;

            // Resolve alias
            if (aliases.TryGetValue(refType, out var resolved))
                refType = resolved;

            // Track reference types
            if (refType is "HasSubtype" or "i=45" or "ns=0;i=45" && isForward != "true")
                hasSubtype = true;
            if (refType is "HasModellingRule" or "i=37" or "ns=0;i=37")
                hasModellingRule = true;
            if (refType is "HasComponent" or "i=47" or "ns=0;i=47" && isForward == "true")
                componentCount++;
            if (refType is "HasProperty" or "i=46" or "ns=0;i=46" && isForward == "true")
                propertyCount++;
        }

        // Types should have a HasSubtype reference (except root types)
        if (nodeClass is "UAObjectType" or "UAVariableType" or "UADataType" or "UAReferenceType"
            && !hasSubtype)
        {
            report.Add(Finding.Warning(
                $"{nodeClass} '{browseName}' ({nodeId}) has no HasSubtype inverse reference. It should derive from a base type.",
                "Part 3, §5.8"));
        }

        // Instance declarations should have ModellingRule
        if (nodeClass is "UAObject" or "UAVariable" or "UAMethod")
        {
            var parentNodeId = el.Attribute("ParentNodeId")?.Value;
            if (parentNodeId != null && !hasModellingRule)
            {
                report.Add(Finding.Warning(
                    $"Instance '{browseName}' ({nodeId}) has ParentNodeId but no ModellingRule reference.",
                    "Part 3, §6.2.6"));
            }
        }
    }

    static void ValidateModellingRules(XElement el, string nodeClass, string nodeId,
        string browseName, List<Finding> report)
    {
        // Check that ObjectTypes have at least one member
        if (nodeClass == "UAObjectType")
        {
            var refs = el.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [];
            var forwardMembers = refs.Count(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                var isForward = r.Attribute("IsForward")?.Value;
                return isForward == "true" &&
                    rt is "HasComponent" or "HasProperty" or "HasOrderedComponent"
                        or "i=47" or "i=46" or "i=49";
            });

            if (forwardMembers == 0)
            {
                report.Add(Finding.Info(
                    $"ObjectType '{browseName}' ({nodeId}) has no declared components/properties. Consider if this is intentional.",
                    "OPC 11030, §7.2"));
            }
        }
    }

    static void ValidateDataTypeUsage(XElement el, string nodeClass, string nodeId,
        string browseName, List<Finding> report)
    {
        if (nodeClass != "UAVariable") return;

        var dataType = el.Attribute("DataType")?.Value ?? "";

        // Warn about using generic Structure/BaseDataType
        if (dataType is "i=22" or "ns=0;i=22")
        {
            report.Add(Finding.Warning(
                $"Variable '{browseName}' ({nodeId}) uses generic 'Structure' DataType. Consider using a more specific concrete type.",
                "OPC 11030, §7.5"));
        }

        if (dataType is "i=24" or "ns=0;i=24")
        {
            report.Add(Finding.Warning(
                $"Variable '{browseName}' ({nodeId}) uses 'BaseDataType'. Consider using a more specific type.",
                "Part 3, §5.8.3"));
        }
    }

    static string StripNsPrefix(string browseName)
    {
        var idx = browseName.IndexOf(':');
        return idx >= 0 && idx < 4 && int.TryParse(browseName[..idx], out _)
            ? browseName[(idx + 1)..] : browseName;
    }

    sealed class Finding
    {
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public string? SpecRef { get; init; }

        public static Finding Error(string msg, string? specRef = null) =>
            new() { Severity = "ERROR", Message = msg, SpecRef = specRef };
        public static Finding Warning(string msg, string? specRef = null) =>
            new() { Severity = "WARNING", Message = msg, SpecRef = specRef };
        public static Finding Info(string msg, string? specRef = null) =>
            new() { Severity = "INFO", Message = msg, SpecRef = specRef };
    }
}

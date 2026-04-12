using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA NodeSet XML Parser — extracts type definitions from NodeSet files
// stored in Azure Blob Storage and produces search index documents.
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaNodeSetParser
{
    const string ContainerName = "opcua-content";

    static readonly XNamespace Ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    // Well-known ModellingRule NodeIds
    static readonly Dictionary<string, string> ModellingRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i=78"]          = "Mandatory",
        ["ns=0;i=78"]     = "Mandatory",
        ["i=80"]          = "Optional",
        ["ns=0;i=80"]     = "Optional",
        ["i=11508"]       = "MandatoryPlaceholder",
        ["ns=0;i=11508"]  = "MandatoryPlaceholder",
        ["i=11510"]       = "OptionalPlaceholder",
        ["ns=0;i=11510"]  = "OptionalPlaceholder",
        ["i=83"]          = "ExposesItsArray",
        ["ns=0;i=83"]     = "ExposesItsArray",
    };

    // Node element local names → NodeClass label
    static readonly Dictionary<string, string> NodeClassMap = new(StringComparer.Ordinal)
    {
        ["UAObjectType"]   = "ObjectType",
        ["UAVariableType"] = "VariableType",
        ["UAObject"]       = "Object",
        ["UAVariable"]     = "Variable",
        ["UAMethod"]       = "Method",
        ["UADataType"]     = "DataType",
        ["UAReferenceType"]= "ReferenceType",
        ["UAView"]         = "View",
    };

    readonly BlobContainerClient _container;
    readonly ILogger _log;

    public OpcUaNodeSetParser(string storageConnectionString, ILogger logger)
    {
        _container = new BlobContainerClient(storageConnectionString, ContainerName);
        _log = logger;
    }

    public async Task<List<SearchDocument>> ParseAllAsync()
    {
        // Phase 1: discover nodeset XML blobs
        var blobNames = new List<string>();
        await foreach (var item in _container.GetBlobsAsync())
        {
            var name = item.Name;
            // Match: *.xml files with "nodeset" in path, OR api/nodesets/ blobs (XML content, may lack extension)
            if (name.StartsWith("api/nodesets/", StringComparison.OrdinalIgnoreCase)
                || (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("nodeset", StringComparison.OrdinalIgnoreCase)))
            {
                blobNames.Add(name);
            }
        }

        _log.LogInformation("[NODESET] Found {Count} nodeset XML blobs", blobNames.Count);
        if (blobNames.Count == 0) return [];

        // Phase 2: download + parse each blob
        var allDocs = new List<SearchDocument>();
        int processed = 0;

        foreach (var blobName in blobNames)
        {
            processed++;
            _log.LogInformation("Parsing nodeset {N}/{Total}: {BlobName}",
                processed, blobNames.Count, blobName);

            try
            {
                var dl = await _container.GetBlobClient(blobName).DownloadContentAsync();
                var xml = dl.Value.Content.ToString();
                var docs = ParseNodeSetXml(xml, blobName);
                allDocs.AddRange(docs);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[NODESET] Skipping malformed XML Blob={Blob} Error={Error}",
                    blobName, ex.Message);
            }
        }

        _log.LogInformation("[NODESET] Completed: {Docs} documents from {Files} files",
            allDocs.Count, blobNames.Count);
        return allDocs;
    }

    /// <summary>
    /// Generates per-spec and cross-spec summary documents from parsed NodeSet docs.
    /// These summaries enable the KB to answer aggregation questions like
    /// "how many ObjectTypes per companion spec?" without needing SQL-style queries.
    /// </summary>
    public static List<SearchDocument> GenerateSummaries(List<SearchDocument> nodesetDocs)
    {
        var summaries = new List<SearchDocument>();

        // Group by spec_part
        var bySpec = nodesetDocs
            .Where(d => d.TryGetValue("content_type", out var ct) && ct?.ToString() == "nodeset")
            .GroupBy(d => d["spec_part"]?.ToString() ?? "Unknown")
            .OrderBy(g => g.Key)
            .ToList();

        var specStats = new List<(string spec, int objectTypes, int variableTypes, int variables,
            int methods, int dataTypes, int mandatory, int optional, int total)>();

        foreach (var group in bySpec)
        {
            var spec = group.Key;
            var nodes = group.ToList();

            int objectTypes = 0, variableTypes = 0, variables = 0, methods = 0, dataTypes = 0;
            int mandatory = 0, optional = 0;

            foreach (var node in nodes)
            {
                var nc = node.TryGetValue("node_class", out var ncVal) ? ncVal?.ToString() : "";
                var mr = node.TryGetValue("modelling_rule", out var mrVal) ? mrVal?.ToString() : "";

                switch (nc)
                {
                    case "ObjectType": objectTypes++; break;
                    case "VariableType": variableTypes++; break;
                    case "Variable": variables++; break;
                    case "Method": methods++; break;
                    case "DataType": dataTypes++; break;
                }

                if (mr == "Mandatory") mandatory++;
                else if (mr == "Optional") optional++;
            }

            specStats.Add((spec, objectTypes, variableTypes, variables, methods, dataTypes,
                mandatory, optional, nodes.Count));

            // Per-spec summary
            var sb = new StringBuilder();
            sb.AppendLine($"NodeSet Summary for companion specification: {spec}");
            sb.AppendLine($"Total nodes: {nodes.Count}");
            sb.AppendLine($"ObjectTypes: {objectTypes}");
            sb.AppendLine($"VariableTypes: {variableTypes}");
            sb.AppendLine($"Variables: {variables} (Mandatory: {mandatory}, Optional: {optional})");
            sb.AppendLine($"Methods: {methods}");
            sb.AppendLine($"DataTypes: {dataTypes}");

            var specId = $"summary-{spec.ToLowerInvariant().Replace(' ', '-')}";
            summaries.Add(new SearchDocument(new Dictionary<string, object>
            {
                ["id"] = MakeId("summary", spec, specId),
                ["page_chunk"] = sb.ToString(),
                ["source_url"] = $"https://reference.opcfoundation.org/{spec}/",
                ["spec_part"] = spec,
                ["spec_version"] = "",
                ["section_title"] = $"NodeSet Summary: {spec}",
                ["content_type"] = "nodeset_summary",
                ["chunk_index"] = 0,
                ["node_class"] = "",
                ["modelling_rule"] = "",
            }));
        }

        // Cross-spec master summary
        var master = new StringBuilder();
        master.AppendLine("OPC UA Companion Specifications — NodeSet Statistics (all specs combined)");
        master.AppendLine();
        master.AppendLine($"Total companion specifications with NodeSet data: {specStats.Count}");
        master.AppendLine($"Total nodes across all specs: {specStats.Sum(s => s.total)}");
        master.AppendLine($"Total ObjectTypes: {specStats.Sum(s => s.objectTypes)}");
        master.AppendLine($"Total VariableTypes: {specStats.Sum(s => s.variableTypes)}");
        master.AppendLine($"Total Variables: {specStats.Sum(s => s.variables)} (Mandatory: {specStats.Sum(s => s.mandatory)}, Optional: {specStats.Sum(s => s.optional)})");
        master.AppendLine($"Total Methods: {specStats.Sum(s => s.methods)}");
        master.AppendLine($"Total DataTypes: {specStats.Sum(s => s.dataTypes)}");
        master.AppendLine();
        master.AppendLine("Top 20 companion specifications by ObjectType count:");
        foreach (var (spec, ot, _, _, _, _, _, _, total) in specStats.OrderByDescending(s => s.objectTypes).Take(20))
        {
            master.AppendLine($"  {spec}: {ot} ObjectTypes ({total} total nodes)");
        }
        master.AppendLine();
        master.AppendLine("Top 20 companion specifications by total Variable count:");
        foreach (var (spec, _, _, vars, _, _, mand, opt, _) in specStats.OrderByDescending(s => s.variables).Take(20))
        {
            master.AppendLine($"  {spec}: {vars} Variables (Mandatory: {mand}, Optional: {opt})");
        }

        summaries.Add(new SearchDocument(new Dictionary<string, object>
        {
            ["id"] = MakeId("summary", "all-specs", "master"),
            ["page_chunk"] = master.ToString(),
            ["source_url"] = "https://reference.opcfoundation.org/",
            ["spec_part"] = "AllSpecs",
            ["spec_version"] = "",
            ["section_title"] = "OPC UA NodeSet Statistics — All Companion Specifications",
            ["content_type"] = "nodeset_summary",
            ["chunk_index"] = 0,
            ["node_class"] = "",
            ["modelling_rule"] = "",
        }));

        return summaries;
    }

    List<SearchDocument> ParseNodeSetXml(string xml, string blobName)
    {
        var xdoc = XDocument.Parse(xml);
        var root = xdoc.Root!;

        // Resolve namespace URIs declared in the file
        var namespaceUris = root.Element(Ns + "NamespaceUris")?
            .Elements(Ns + "Uri")
            .Select(e => e.Value)
            .ToList() ?? [];

        var primaryNsUri = namespaceUris.Count > 0 ? namespaceUris[0] : "";
        var specName = ExtractSpecName(primaryNsUri, blobName);
        var specVersion = ExtractSpecVersion(blobName);

        // Build NodeId → BrowseName lookup for parent resolution
        var nodeIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in root.Elements())
        {
            var nodeId = el.Attribute("NodeId")?.Value;
            var browse = el.Attribute("BrowseName")?.Value;
            if (nodeId != null && browse != null)
                nodeIndex[nodeId] = StripNamespacePrefix(browse);
        }

        var docs = new List<SearchDocument>();
        int chunkIdx = 0;

        foreach (var el in root.Elements())
        {
            var localName = el.Name.LocalName;
            if (!NodeClassMap.TryGetValue(localName, out var nodeClass))
                continue;

            var nodeId = el.Attribute("NodeId")?.Value ?? "";
            var browseName = StripNamespacePrefix(el.Attribute("BrowseName")?.Value ?? "");
            var dataType = el.Attribute("DataType")?.Value ?? "";
            var parentNodeId = el.Attribute("ParentNodeId")?.Value;
            var description = el.Element(Ns + "Description")?.Value?.Trim() ?? "";

            // Resolve references
            var refs = el.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [];
            string modellingRule = "";
            string parentType = "";

            foreach (var r in refs)
            {
                var refType = r.Attribute("ReferenceType")?.Value ?? "";
                var target = r.Value.Trim();

                if (refType == "HasModellingRule")
                {
                    ModellingRules.TryGetValue(target, out modellingRule!);
                    modellingRule ??= "";
                }
                else if (refType is "HasComponent" or "HasProperty"
                         && r.Attribute("IsForward")?.Value != "true")
                {
                    // Inverse reference — target is the parent
                    if (nodeIndex.TryGetValue(target, out var pName))
                        parentType = pName;
                }
            }

            // Fall back to ParentNodeId attribute for parent resolution
            if (string.IsNullOrEmpty(parentType) && parentNodeId != null)
            {
                if (nodeIndex.TryGetValue(parentNodeId, out var pName))
                    parentType = pName;
            }

            // Build descriptive chunk text
            var chunk = FormatChunkText(nodeClass, browseName, dataType,
                modellingRule, parentType, description);

            var sourceUrl = BuildSourceUrl(primaryNsUri, blobName);
            var id = MakeId(primaryNsUri, browseName, nodeId);

            var sectionTitle = !string.IsNullOrEmpty(parentType) ? parentType : browseName;

            docs.Add(new SearchDocument(new Dictionary<string, object>
            {
                ["id"] = id,
                ["page_chunk"] = chunk,
                ["source_url"] = sourceUrl,
                ["spec_part"] = specName,
                ["spec_version"] = specVersion,
                ["section_title"] = sectionTitle,
                ["content_type"] = "nodeset",
                ["chunk_index"] = chunkIdx++,
                ["node_class"] = nodeClass,
                ["modelling_rule"] = modellingRule,
            }));
        }

        return docs;
    }

    static string FormatChunkText(string nodeClass, string browseName,
        string dataType, string modellingRule, string parentType, string description)
    {
        var sb = new StringBuilder();
        sb.Append($"{nodeClass}: {browseName}");

        var details = new List<string>();
        if (!string.IsNullOrEmpty(dataType)) details.Add($"DataType: {dataType}");
        if (!string.IsNullOrEmpty(modellingRule)) details.Add($"ModellingRule: {modellingRule}");
        if (details.Count > 0) sb.Append($" ({string.Join(", ", details)})");

        if (!string.IsNullOrEmpty(parentType)) sb.Append($" in {parentType}");
        if (!string.IsNullOrEmpty(description)) sb.Append($". {description}");

        return sb.ToString();
    }

    static string MakeId(string namespaceUri, string browseName, string nodeId)
    {
        var raw = $"{namespaceUri}:{browseName}:{nodeId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32]
            .ToLowerInvariant();
    }

    static string BuildSourceUrl(string namespaceUri, string blobName)
    {
        // If we can derive a reference.opcfoundation.org URL, prefer it
        var match = Regex.Match(namespaceUri, @"opcfoundation\.org/UA/([^/]+)");
        if (match.Success)
            return $"https://reference.opcfoundation.org/{match.Groups[1].Value}/";

        return $"blob://{ContainerName}/{blobName}";
    }

    static string ExtractSpecName(string namespaceUri, string blobName)
    {
        // Try namespace URI first: http://opcfoundation.org/UA/DI/ → "DI"
        var m = Regex.Match(namespaceUri, @"opcfoundation\.org/UA/([^/]+)");
        if (m.Success) return m.Groups[1].Value;

        // Fall back to blob path segments
        var segments = blobName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg.Contains("nodeset", StringComparison.OrdinalIgnoreCase)) continue;
            if (seg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(seg) && seg.Length > 1) return seg;
        }

        return "Unknown";
    }

    static string ExtractSpecVersion(string blobName)
    {
        var m = Regex.Match(blobName, @"(v\d+[\.\d]*)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Try version patterns like 1.04, 1.05.03
        m = Regex.Match(blobName, @"(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "Unknown";
    }

    static string StripNamespacePrefix(string browseName)
    {
        // BrowseNames may be prefixed with "1:", "2:", etc.
        var idx = browseName.IndexOf(':');
        return idx >= 0 && idx < 4 && int.TryParse(browseName[..idx], out _)
            ? browseName[(idx + 1)..] : browseName;
    }
}

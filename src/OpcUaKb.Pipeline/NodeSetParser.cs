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
// Includes full type hierarchy resolution for inherited member counting.
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaNodeSetParser
{
    const string ContainerName = "opcua-content";
    const string BaseUaNamespace = "http://opcfoundation.org/UA/";

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

    // Containment reference types (HasComponent, HasProperty, HasOrderedComponent)
    static readonly HashSet<string> ContainmentRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "HasComponent", "HasProperty", "HasOrderedComponent",
        "i=47", "ns=0;i=47",   // HasComponent
        "i=46", "ns=0;i=46",   // HasProperty
        "i=49", "ns=0;i=49",   // HasOrderedComponent
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

    // ── Type hierarchy data (populated during ParseAllAsync) ────────────
    readonly Dictionary<string, TypeInfo> _typeRegistry = new(StringComparer.Ordinal);

    /// <summary>Type hierarchy information for a single ObjectType node.</summary>
    internal sealed class TypeInfo
    {
        public required string GlobalNodeId { get; init; }
        public required string BrowseName { get; init; }
        public required string Spec { get; init; }
        public string? SupertypeGlobalId { get; set; }
        public bool HierarchyComplete { get; set; } = true;

        // Members declared directly in this type's subtree (all depths)
        public int DeclaredVariables { get; set; }
        public int DeclaredMethods { get; set; }
        public int DeclaredObjects { get; set; }

        // Including inherited from supertype chain (computed after all files parsed)
        public int TotalVariables { get; set; }
        public int TotalMethods { get; set; }
        public int TotalObjects { get; set; }
        public bool TotalComputed { get; set; }
    }

    /// <summary>Per-file metadata collected during parsing for hierarchy resolution.</summary>
    sealed class FileNodeInfo
    {
        public required string LocalNodeId { get; init; }
        public required string GlobalNodeId { get; init; }
        public required string NodeClass { get; init; }
        public string? ParentLocalNodeId { get; set; }
    }

    public OpcUaNodeSetParser(string storageConnectionString, ILogger logger)
    {
        _container = new BlobContainerClient(storageConnectionString, ContainerName);
        _log = logger;
    }

    /// <summary>Exposes the computed type hierarchy for use by GenerateSummaries.</summary>
    internal IReadOnlyDictionary<string, TypeInfo> TypeRegistry => _typeRegistry;

    public async Task<List<SearchDocument>> ParseAllAsync()
    {
        // Phase 1: discover nodeset XML blobs
        var blobNames = new List<string>();
        await foreach (var item in _container.GetBlobsAsync())
        {
            var name = item.Name;
            if (name.StartsWith("api/nodesets/", StringComparison.OrdinalIgnoreCase)
                || (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("nodeset", StringComparison.OrdinalIgnoreCase)))
            {
                blobNames.Add(name);
            }
        }

        _log.LogInformation("[NODESET] Found {Count} nodeset XML blobs", blobNames.Count);
        if (blobNames.Count == 0) return [];

        // Phase 2: download + parse each blob, collecting hierarchy metadata
        var allDocs = new List<SearchDocument>();
        var allFileNodes = new List<(string spec, List<FileNodeInfo> nodes,
            Dictionary<string, string> localToGlobal)>();
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
                var (docs, fileNodes, localToGlobal) = ParseNodeSetXmlWithHierarchy(xml, blobName);
                allDocs.AddRange(docs);
                var spec = docs.FirstOrDefault()?["spec_part"]?.ToString() ?? "Unknown";
                allFileNodes.Add((spec, fileNodes, localToGlobal));
            }
            catch (Exception ex)
            {
                _log.LogWarning("[NODESET] Skipping malformed XML Blob={Blob} Error={Error}",
                    blobName, ex.Message);
            }
        }

        // Phase 3: compute declared member counts per ObjectType
        foreach (var (spec, fileNodes, localToGlobal) in allFileNodes)
        {
            ComputeDeclaredCounts(fileNodes, localToGlobal);
        }

        // Phase 4: compute inherited totals across all types
        ComputeInheritedCounts();

        _log.LogInformation(
            "[NODESET] Completed: {Docs} documents from {Files} files, {Types} ObjectTypes with hierarchy",
            allDocs.Count, blobNames.Count, _typeRegistry.Count);
        return allDocs;
    }

    /// <summary>
    /// Generates per-spec and cross-spec summary documents from parsed NodeSet docs.
    /// Uses the type hierarchy (if available) to include per-ObjectType member counts
    /// with inherited members from the supertype chain.
    /// </summary>
    public List<SearchDocument> GenerateSummaries(List<SearchDocument> nodesetDocs)
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

            // Per-spec summary with top ObjectTypes by total members
            var sb = new StringBuilder();
            sb.AppendLine($"NodeSet Summary for companion specification: {spec}");
            sb.AppendLine($"Total nodes: {nodes.Count}");
            sb.AppendLine($"ObjectTypes: {objectTypes}");
            sb.AppendLine($"VariableTypes: {variableTypes}");
            sb.AppendLine($"Variables: {variables} (Mandatory: {mandatory}, Optional: {optional})");
            sb.AppendLine($"Methods: {methods}");
            sb.AppendLine($"DataTypes: {dataTypes}");

            // Add top ObjectTypes by total members (including inherited) for this spec
            var specTypes = _typeRegistry.Values
                .Where(t => t.Spec == spec && t.TotalComputed)
                .OrderByDescending(t => t.TotalVariables + t.TotalMethods + t.TotalObjects)
                .Take(15)
                .ToList();

            if (specTypes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top ObjectTypes by total members (including inherited from supertypes):");
                foreach (var t in specTypes)
                {
                    var total = t.TotalVariables + t.TotalMethods + t.TotalObjects;
                    var declared = t.DeclaredVariables + t.DeclaredMethods + t.DeclaredObjects;
                    var inherited = total - declared;
                    var completeness = t.HierarchyComplete ? "" : " [partial — missing supertype data]";
                    sb.AppendLine($"  {t.BrowseName}: {total} total members ({t.TotalVariables} Variables, {t.TotalMethods} Methods, {t.TotalObjects} Objects) — {declared} declared, {inherited} inherited{completeness}");
                }
            }

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
                ["browse_name"] = "",
                ["parent_type"] = "",
                ["data_type"] = "",
            ["is_latest"] = true,
            ["version_rank"] = 1,
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

        // Top ObjectTypes across ALL specs by total members (including inherited)
        var topTypes = _typeRegistry.Values
            .Where(t => t.TotalComputed)
            .OrderByDescending(t => t.TotalVariables + t.TotalMethods + t.TotalObjects)
            .Take(30)
            .ToList();

        if (topTypes.Count > 0)
        {
            master.AppendLine();
            master.AppendLine("Top 30 ObjectTypes across all specs by total members (including inherited from supertypes):");
            foreach (var t in topTypes)
            {
                var total = t.TotalVariables + t.TotalMethods + t.TotalObjects;
                var declared = t.DeclaredVariables + t.DeclaredMethods + t.DeclaredObjects;
                var inherited = total - declared;
                var completeness = t.HierarchyComplete ? "" : " [partial]";
                master.AppendLine($"  {t.BrowseName} ({t.Spec}): {total} total ({t.TotalVariables} Vars, {t.TotalMethods} Methods, {t.TotalObjects} Objs) — {declared} declared, {inherited} inherited{completeness}");
            }
        }

        var completeCount = _typeRegistry.Values.Count(t => t.HierarchyComplete);
        var partialCount = _typeRegistry.Values.Count(t => !t.HierarchyComplete);
        master.AppendLine();
        master.AppendLine($"Type hierarchy: {_typeRegistry.Count} ObjectTypes resolved, {completeCount} with complete hierarchy, {partialCount} with partial (missing supertype data)");

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
            ["browse_name"] = "",
            ["parent_type"] = "",
            ["data_type"] = "",
        ["is_latest"] = true,
        ["version_rank"] = 1,
        }));

        // Per-ObjectType hierarchy documents for structured lookup
        foreach (var t in _typeRegistry.Values.Where(t => t.TotalComputed))
        {
            var total = t.TotalVariables + t.TotalMethods + t.TotalObjects;
            var declared = t.DeclaredVariables + t.DeclaredMethods + t.DeclaredObjects;
            var inherited = total - declared;
            var completeness = t.HierarchyComplete ? "complete" : "partial (missing supertype data)";

            // Build supertype chain text
            var chain = new List<string>();
            var current = t;
            var visited = new HashSet<string>();
            while (current?.SupertypeGlobalId != null && visited.Add(current.SupertypeGlobalId))
            {
                if (_typeRegistry.TryGetValue(current.SupertypeGlobalId, out var super))
                {
                    chain.Add($"{super.BrowseName} ({super.Spec})");
                    current = super;
                }
                else
                {
                    chain.Add("[unknown supertype]");
                    break;
                }
            }

            var hb = new StringBuilder();
            hb.AppendLine($"ObjectType: {t.BrowseName} (Companion Spec: {t.Spec})");
            if (chain.Count > 0)
                hb.AppendLine($"Supertype chain: {string.Join(" → ", chain)}");
            else
                hb.AppendLine("Supertype chain: (none / root type)");
            hb.AppendLine($"Hierarchy status: {completeness}");
            hb.AppendLine($"Declared members: {declared} ({t.DeclaredVariables} Variables, {t.DeclaredMethods} Methods, {t.DeclaredObjects} Objects)");
            hb.AppendLine($"Inherited members: {inherited}");
            hb.AppendLine($"Total members (declared + inherited): {total} ({t.TotalVariables} Variables, {t.TotalMethods} Methods, {t.TotalObjects} Objects)");

            summaries.Add(new SearchDocument(new Dictionary<string, object>
            {
                ["id"] = MakeId("hierarchy", t.Spec, t.BrowseName),
                ["page_chunk"] = hb.ToString(),
                ["source_url"] = $"https://reference.opcfoundation.org/{t.Spec}/",
                ["spec_part"] = t.Spec,
                ["spec_version"] = "",
                ["section_title"] = t.BrowseName,
                ["content_type"] = "nodeset_hierarchy",
                ["chunk_index"] = 0,
                ["node_class"] = "ObjectType",
                ["modelling_rule"] = "",
                ["browse_name"] = t.BrowseName,
                ["parent_type"] = chain.Count > 0 ? chain[0] : "",
                ["data_type"] = "",
            ["is_latest"] = true,
            ["version_rank"] = 1,
            }));
        }

        return summaries;
    }

    // ── Core parsing with hierarchy metadata collection ─────────────────

    (List<SearchDocument> docs, List<FileNodeInfo> nodes, Dictionary<string, string> localToGlobal)
        ParseNodeSetXmlWithHierarchy(string xml, string blobName)
    {
        var xdoc = XDocument.Parse(xml);
        var root = xdoc.Root!;

        // Resolve namespace URIs declared in the file
        var namespaceUris = root.Element(Ns + "NamespaceUris")?
            .Elements(Ns + "Uri")
            .Select(e => e.Value)
            .ToList() ?? [];

        // Build alias table: alias → NodeId
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in root.Element(Ns + "Aliases")?.Elements(Ns + "Alias") ?? [])
        {
            var name = alias.Attribute("Alias")?.Value;
            var value = alias.Value.Trim();
            if (name != null) aliases[name] = value;
        }

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

        // Build localNodeId → globalNodeId mapping for this file
        var localToGlobal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in root.Elements())
        {
            var nodeId = el.Attribute("NodeId")?.Value;
            if (nodeId != null)
                localToGlobal[nodeId] = ResolveGlobalNodeId(nodeId, namespaceUris, aliases);
        }

        var docs = new List<SearchDocument>();
        var fileNodes = new List<FileNodeInfo>();
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

            var refs = el.Element(Ns + "References")?.Elements(Ns + "Reference") ?? [];
            string modellingRule = "";
            string parentType = "";
            string? resolvedParentLocalId = null;
            string? supertypeLocalId = null;

            foreach (var r in refs)
            {
                var refType = r.Attribute("ReferenceType")?.Value ?? "";
                var target = r.Value.Trim();

                // Resolve alias for reference type
                if (aliases.TryGetValue(refType, out var resolvedRef))
                    refType = resolvedRef;

                if (refType == "HasModellingRule" || refType == "i=37" || refType == "ns=0;i=37")
                {
                    var resolvedTarget = aliases.TryGetValue(target, out var t) ? t : target;
                    ModellingRules.TryGetValue(resolvedTarget, out modellingRule!);
                    modellingRule ??= "";
                }
                else if (IsContainmentRef(refType) && r.Attribute("IsForward")?.Value != "true")
                {
                    var resolvedTarget = aliases.TryGetValue(target, out var t) ? t : target;
                    resolvedParentLocalId = resolvedTarget;
                    if (nodeIndex.TryGetValue(resolvedTarget, out var pName))
                        parentType = pName;
                }
                else if ((refType == "HasSubtype" || refType == "i=45" || refType == "ns=0;i=45")
                         && r.Attribute("IsForward")?.Value != "true")
                {
                    // Inverse HasSubtype: this type IS a subtype of target
                    supertypeLocalId = aliases.TryGetValue(target, out var t) ? t : target;
                }
            }

            // Fall back to ParentNodeId attribute for parent resolution
            if (resolvedParentLocalId == null && parentNodeId != null)
            {
                resolvedParentLocalId = parentNodeId;
                if (string.IsNullOrEmpty(parentType) && nodeIndex.TryGetValue(parentNodeId, out var pName))
                    parentType = pName;
            }

            // Register ObjectType in hierarchy
            if (nodeClass == "ObjectType" && !string.IsNullOrEmpty(nodeId))
            {
                var globalId = localToGlobal.GetValueOrDefault(nodeId)
                    ?? ResolveGlobalNodeId(nodeId, namespaceUris, aliases);
                var supertypeGlobalId = supertypeLocalId != null
                    ? ResolveGlobalNodeId(supertypeLocalId, namespaceUris, aliases)
                    : null;

                _typeRegistry.TryAdd(globalId, new TypeInfo
                {
                    GlobalNodeId = globalId,
                    BrowseName = browseName,
                    Spec = specName,
                    SupertypeGlobalId = supertypeGlobalId,
                });
            }

            // Collect hierarchy metadata for member counting
            fileNodes.Add(new FileNodeInfo
            {
                LocalNodeId = nodeId,
                GlobalNodeId = localToGlobal.GetValueOrDefault(nodeId)
                    ?? ResolveGlobalNodeId(nodeId, namespaceUris, aliases),
                NodeClass = nodeClass,
                ParentLocalNodeId = resolvedParentLocalId,
            });

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
                ["browse_name"] = browseName,
                ["parent_type"] = parentType,
                ["data_type"] = StripNamespacePrefix(dataType),
            ["is_latest"] = true,
            ["version_rank"] = 1,
            }));
        }

        return (docs, fileNodes, localToGlobal);
    }

    // ── Hierarchy computation ───────────────────────────────────────────

    /// <summary>
    /// Counts declared members (Variables, Methods, Objects) for each ObjectType
    /// by walking the node tree within each file. Counts ALL descendants, not just direct children.
    /// </summary>
    void ComputeDeclaredCounts(List<FileNodeInfo> fileNodes, Dictionary<string, string> localToGlobal)
    {
        // Build parent→children tree using local NodeIds
        var childMap = new Dictionary<string, List<FileNodeInfo>>(StringComparer.Ordinal);
        foreach (var node in fileNodes)
        {
            if (node.ParentLocalNodeId == null) continue;
            if (!childMap.TryGetValue(node.ParentLocalNodeId, out var list))
            {
                list = [];
                childMap[node.ParentLocalNodeId] = list;
            }
            list.Add(node);
        }

        // For each ObjectType in this file, count all descendants
        foreach (var node in fileNodes)
        {
            if (node.NodeClass != "ObjectType") continue;
            if (!_typeRegistry.TryGetValue(node.GlobalNodeId, out var typeInfo)) continue;

            var (vars, methods, objects) = CountDescendants(node.LocalNodeId, childMap, []);
            typeInfo.DeclaredVariables = vars;
            typeInfo.DeclaredMethods = methods;
            typeInfo.DeclaredObjects = objects;
        }
    }

    static (int vars, int methods, int objects) CountDescendants(
        string nodeId, Dictionary<string, List<FileNodeInfo>> childMap, HashSet<string> visited)
    {
        if (!visited.Add(nodeId)) return (0, 0, 0); // cycle protection
        if (!childMap.TryGetValue(nodeId, out var children)) return (0, 0, 0);

        int vars = 0, methods = 0, objects = 0;
        foreach (var child in children)
        {
            switch (child.NodeClass)
            {
                case "Variable": vars++; break;
                case "Method": methods++; break;
                case "Object": objects++; break;
            }
            // Recurse into child's subtree (nested components)
            var (cv, cm, co) = CountDescendants(child.LocalNodeId, childMap, visited);
            vars += cv;
            methods += cm;
            objects += co;
        }
        return (vars, methods, objects);
    }

    /// <summary>
    /// Computes total member counts (declared + inherited) for all ObjectTypes
    /// by walking supertype chains with memoization.
    /// </summary>
    void ComputeInheritedCounts()
    {
        foreach (var type in _typeRegistry.Values)
        {
            ComputeTotalForType(type, []);
        }

        var complete = _typeRegistry.Values.Count(t => t.HierarchyComplete);
        _log.LogInformation(
            "[HIERARCHY] {Total} ObjectTypes, {Complete} with complete hierarchy, {Partial} partial",
            _typeRegistry.Count, complete, _typeRegistry.Count - complete);
    }

    void ComputeTotalForType(TypeInfo type, HashSet<string> visiting)
    {
        if (type.TotalComputed) return;

        // Cycle detection
        if (!visiting.Add(type.GlobalNodeId))
        {
            _log.LogWarning("[HIERARCHY] Cycle detected at {Type}", type.BrowseName);
            type.TotalVariables = type.DeclaredVariables;
            type.TotalMethods = type.DeclaredMethods;
            type.TotalObjects = type.DeclaredObjects;
            type.TotalComputed = true;
            type.HierarchyComplete = false;
            return;
        }

        int inheritedVars = 0, inheritedMethods = 0, inheritedObjects = 0;

        if (type.SupertypeGlobalId != null)
        {
            if (_typeRegistry.TryGetValue(type.SupertypeGlobalId, out var supertype))
            {
                ComputeTotalForType(supertype, visiting);
                inheritedVars = supertype.TotalVariables;
                inheritedMethods = supertype.TotalMethods;
                inheritedObjects = supertype.TotalObjects;
                if (!supertype.HierarchyComplete)
                    type.HierarchyComplete = false;
            }
            else
            {
                // Supertype not in registry (e.g., base OPC UA types not downloaded)
                type.HierarchyComplete = false;
            }
        }

        type.TotalVariables = type.DeclaredVariables + inheritedVars;
        type.TotalMethods = type.DeclaredMethods + inheritedMethods;
        type.TotalObjects = type.DeclaredObjects + inheritedObjects;
        type.TotalComputed = true;
        visiting.Remove(type.GlobalNodeId);
    }

    // ── NodeId resolution ───────────────────────────────────────────────

    static bool IsContainmentRef(string refType)
    {
        return ContainmentRefs.Contains(refType);
    }

    /// <summary>
    /// Converts a file-local NodeId to a globally unique identifier using namespace URIs.
    /// Handles ns=N prefixes, aliases, and nsu= expanded NodeIds.
    /// </summary>
    static string ResolveGlobalNodeId(string localNodeId, List<string> namespaceUris,
        Dictionary<string, string> aliases)
    {
        // Resolve alias first
        if (aliases.TryGetValue(localNodeId, out var resolved))
            localNodeId = resolved;

        // Handle nsu= (ExpandedNodeId) format: nsu=http://example.org/;i=123
        if (localNodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase))
        {
            var semiIdx = localNodeId.IndexOf(';');
            if (semiIdx > 4)
            {
                var nsUri = localNodeId[4..semiIdx];
                var identifier = localNodeId[(semiIdx + 1)..];
                return $"{nsUri}|{identifier}";
            }
        }

        // Handle ns=N prefix
        var match = Regex.Match(localNodeId, @"^ns=(\d+);(.+)$");
        if (match.Success)
        {
            var nsIndex = int.Parse(match.Groups[1].Value);
            var identifier = match.Groups[2].Value;

            string nsUri;
            if (nsIndex == 0)
                nsUri = BaseUaNamespace;
            else if (nsIndex - 1 < namespaceUris.Count)
                nsUri = namespaceUris[nsIndex - 1];
            else
                nsUri = $"ns={nsIndex}"; // Unknown namespace — best-effort

            return $"{nsUri}|{identifier}";
        }

        // No namespace prefix → namespace 0 (base OPC UA)
        return $"{BaseUaNamespace}|{localNodeId}";
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

    // Known blob path prefixes that produce unhelpful spec names
    static readonly Dictionary<string, string> SpecNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api"] = "CoreNodeSets",
        ["files_opcfoundation_org"] = "LegacyNodeSets",
        ["files.opcfoundation.org"] = "LegacyNodeSets",
    };

    static string ExtractSpecName(string namespaceUri, string blobName)
    {
        // Try namespace URI first: http://opcfoundation.org/UA/DI/ → "DI"
        var m = Regex.Match(namespaceUri, @"opcfoundation\.org/UA/([^/]+)");
        if (m.Success) return m.Groups[1].Value;

        // Fall back to blob path segments with override normalization
        var segments = blobName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg.Contains("nodeset", StringComparison.OrdinalIgnoreCase)) continue;
            if (seg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(seg) && seg.Length > 1)
            {
                // Apply override if this is a known unhelpful name
                return SpecNameOverrides.GetValueOrDefault(seg, seg);
            }
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

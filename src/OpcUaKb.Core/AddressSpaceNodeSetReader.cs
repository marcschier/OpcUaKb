using System.Globalization;
using System.Xml;

public sealed record AddressSpaceModel
{
    public required string ModelUri { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? PublicationDate { get; init; }
    public IReadOnlyList<AddressSpaceRequiredModel> RequiredModels { get; init; } = [];
}

public sealed record AddressSpaceRequiredModel
{
    public required string ModelUri { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? PublicationDate { get; init; }
}

public sealed record AddressSpaceQualifiedName
{
    public required string NamespaceUri { get; init; }
    public required string Name { get; init; }
    public override string ToString() =>
        $"nsu={AddressSpaceExpandedNodeId.EscapeNamespaceUri(NamespaceUri)};{Name}";
}

public static class AddressSpaceExpandedNodeId
{
    public static string EscapeNamespaceUri(string namespaceUri)
    {
        ArgumentNullException.ThrowIfNull(namespaceUri);
        if (Uri.TryCreate(namespaceUri, UriKind.Absolute, out var uri))
        {
            return uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped)
                .Replace(";", "%3B", StringComparison.Ordinal);
        }

        return namespaceUri.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal);
    }
}

public sealed record AddressSpaceReference
{
    public required string ReferenceType { get; init; }
    public required string TargetNodeId { get; init; }
    public required bool IsForward { get; init; }
}

public sealed class AddressSpaceNode
{
    public required string NodeId { get; init; }
    public required string LocalNodeId { get; init; }
    public required AddressSpaceQualifiedName BrowseName { get; init; }
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public required string NodeClass { get; init; }
    public string? ParentNodeId { get; set; }
    public string? TypeDefinition { get; set; }
    public string? DataType { get; set; }
    public int? ValueRank { get; init; }
    public IReadOnlyList<uint> ArrayDimensions { get; init; } = [];
    public byte? AccessLevel { get; init; }
    public byte? UserAccessLevel { get; init; }
    public bool? Executable { get; init; }
    public bool? UserExecutable { get; init; }
    public string? ValueType { get; set; }
    public string? ValueText { get; set; }
    public string? ModellingRule { get; set; }
    public IReadOnlyList<AddressSpaceReference> References { get; set; } = [];
    public string BrowsePath { get; set; } = "";
    public List<string> Children { get; } = [];
}

public sealed class AddressSpaceGraph
{
    public required IReadOnlyList<string> NamespaceUris { get; init; }
    public required IReadOnlyList<AddressSpaceModel> Models { get; init; }
    public required IReadOnlyDictionary<string, string> Aliases { get; init; }
    public required IReadOnlyDictionary<string, AddressSpaceNode> Nodes { get; init; }

    public IEnumerable<AddressSpaceNode> Roots =>
        Nodes.Values.Where(n => n.ParentNodeId is null || !Nodes.ContainsKey(n.ParentNodeId));
}

public sealed class AddressSpaceNodeSetReader
{
    public const int MaximumValueTextLength = 4096;
    public const string UaNamespace = "http://opcfoundation.org/UA/";
    public const string NodeSetNamespace = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    static readonly HashSet<string> NodeElements =
    [
        "UAObject", "UAVariable", "UAMethod", "UAObjectType", "UAVariableType",
        "UADataType", "UAReferenceType", "UAView",
    ];

    static readonly Dictionary<string, string> StandardAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HasTypeDefinition"] = "i=40",
        ["HasModellingRule"] = "i=37",
        ["HasSubtype"] = "i=45",
        ["HasProperty"] = "i=46",
        ["HasComponent"] = "i=47",
        ["HasNotifier"] = "i=48",
        ["HasOrderedComponent"] = "i=49",
        ["Organizes"] = "i=35",
        ["Mandatory"] = "i=78",
        ["Optional"] = "i=80",
        ["OptionalPlaceholder"] = "i=11508",
        ["MandatoryPlaceholder"] = "i=11510",
    };

    static readonly HashSet<string> HierarchicalReferenceIds =
    [
        "i=33", "i=34", "i=35", "i=36", "i=44", "i=46", "i=47", "i=48", "i=49",
    ];

    static XmlReaderSettings Settings => new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    public async Task<AddressSpaceGraph> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var namespaceUris = new List<string> { UaNamespace };
        var aliases = new Dictionary<string, string>(StandardAliases, StringComparer.Ordinal);
        var models = new List<AddressSpaceModel>();
        var nodes = new Dictionary<string, AddressSpaceNode>(StringComparer.Ordinal);

        using var reader = XmlReader.Create(stream, Settings);
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != NodeSetNamespace)
                continue;

            switch (reader.LocalName)
            {
                case "NamespaceUris":
                    await ReadNamespaceUrisAsync(reader, namespaceUris, cancellationToken);
                    break;
                case "Models":
                    await ReadModelsAsync(reader, models, cancellationToken);
                    break;
                case "Aliases":
                    await ReadAliasesAsync(reader, aliases, cancellationToken);
                    break;
                default:
                    if (NodeElements.Contains(reader.LocalName))
                    {
                        var node = await ReadNodeAsync(reader, namespaceUris, aliases, cancellationToken);
                        if (!nodes.TryAdd(node.NodeId, node))
                            throw new XmlException($"Duplicate NodeId '{node.NodeId}'.");
                    }
                    break;
            }
        }

        NormalizeGraph(nodes, aliases, namespaceUris);
        return new AddressSpaceGraph
        {
            NamespaceUris = namespaceUris,
            Models = models,
            Aliases = aliases.ToDictionary(
                p => p.Key,
                p => CanonicalizeNodeId(p.Value, namespaceUris, aliases, allowAlias: false),
                StringComparer.Ordinal),
            Nodes = nodes,
        };
    }

    static async Task ReadNamespaceUrisAsync(
        XmlReader reader,
        List<string> namespaceUris,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement) return;
        using var subtree = reader.ReadSubtree();
        await subtree.ReadAsync();
        var hasNode = await subtree.ReadAsync();
        while (hasNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "Uri")
            {
                var uri = (await subtree.ReadElementContentAsStringAsync()).Trim();
                if (uri.Length > 0 && !namespaceUris.Contains(uri, StringComparer.Ordinal))
                    namespaceUris.Add(uri);
                hasNode = !subtree.EOF;
                continue;
            }
            hasNode = await subtree.ReadAsync();
        }
    }

    static async Task ReadModelsAsync(
        XmlReader reader,
        List<AddressSpaceModel> models,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement) return;
        using var subtree = reader.ReadSubtree();
        await subtree.ReadAsync();
        while (await subtree.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "Model")
                continue;

            var required = new List<AddressSpaceRequiredModel>();
            var model = new AddressSpaceModel
            {
                ModelUri = RequiredAttribute(subtree, "ModelUri"),
                Version = subtree.GetAttribute("Version"),
                PublicationDate = ParseDate(subtree.GetAttribute("PublicationDate")),
                RequiredModels = required,
            };
            if (!subtree.IsEmptyElement)
            {
                using var modelSubtree = subtree.ReadSubtree();
                await modelSubtree.ReadAsync();
                while (await modelSubtree.ReadAsync())
                {
                    if (modelSubtree.NodeType == XmlNodeType.Element &&
                        modelSubtree.LocalName == "RequiredModel")
                    {
                        required.Add(new AddressSpaceRequiredModel
                        {
                            ModelUri = RequiredAttribute(modelSubtree, "ModelUri"),
                            Version = modelSubtree.GetAttribute("Version"),
                            PublicationDate = ParseDate(modelSubtree.GetAttribute("PublicationDate")),
                        });
                    }
                }
            }
            models.Add(model);
        }
    }

    static async Task ReadAliasesAsync(
        XmlReader reader,
        Dictionary<string, string> aliases,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement) return;
        using var subtree = reader.ReadSubtree();
        await subtree.ReadAsync();
        var hasNode = await subtree.ReadAsync();
        while (hasNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "Alias")
            {
                hasNode = await subtree.ReadAsync();
                continue;
            }
            var name = RequiredAttribute(subtree, "Alias");
            var value = (await subtree.ReadElementContentAsStringAsync()).Trim();
            if (value.Length == 0)
                throw new XmlException($"Alias '{name}' has no value.");
            aliases[name] = value;
            hasNode = !subtree.EOF;
        }
    }

    static async Task<AddressSpaceNode> ReadNodeAsync(
        XmlReader reader,
        IReadOnlyList<string> namespaceUris,
        IReadOnlyDictionary<string, string> aliases,
        CancellationToken cancellationToken)
    {
        var localNodeId = RequiredAttribute(reader, "NodeId");
        var browseName = ParseBrowseName(RequiredAttribute(reader, "BrowseName"), namespaceUris);
        var nodeClass = reader.LocalName[2..];
        var parent = reader.GetAttribute("ParentNodeId");
        var dataType = reader.GetAttribute("DataType");
        var valueRank = ParseNullableInt(reader.GetAttribute("ValueRank"));
        var arrayDimensions = ParseArrayDimensions(reader.GetAttribute("ArrayDimensions"));
        var accessLevel = ParseNullableByte(reader.GetAttribute("AccessLevel"));
        var userAccessLevel = ParseNullableByte(reader.GetAttribute("UserAccessLevel"));
        var executable = ParseNullableBool(reader.GetAttribute("Executable"));
        var userExecutable = ParseNullableBool(reader.GetAttribute("UserExecutable"));
        var references = new List<AddressSpaceReference>();
        var displayName = "";
        string? description = null;
        string? valueType = null;
        string? valueText = null;

        if (!reader.IsEmptyElement)
        {
            using var subtree = reader.ReadSubtree();
            await subtree.ReadAsync();
            var hasNode = await subtree.ReadAsync();
            while (hasNode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (subtree.NodeType != XmlNodeType.Element)
                {
                    hasNode = await subtree.ReadAsync();
                    continue;
                }
                switch (subtree.LocalName)
                {
                    case "DisplayName":
                        displayName = (await subtree.ReadElementContentAsStringAsync()).Trim();
                        hasNode = !subtree.EOF;
                        continue;
                    case "Description":
                        description = (await subtree.ReadElementContentAsStringAsync()).Trim();
                        hasNode = !subtree.EOF;
                        continue;
                    case "Value":
                        (valueType, valueText) = await ReadValueAsync(subtree, cancellationToken);
                        hasNode = await subtree.ReadAsync();
                        continue;
                    case "Reference":
                        var referenceType = CanonicalizeNodeId(
                            RequiredAttribute(subtree, "ReferenceType"), namespaceUris, aliases);
                        var isForward = !bool.TryParse(
                            subtree.GetAttribute("IsForward"), out var forward) || forward;
                        var target = CanonicalizeNodeId(
                            (await subtree.ReadElementContentAsStringAsync()).Trim(), namespaceUris, aliases);
                        references.Add(new AddressSpaceReference
                        {
                            ReferenceType = referenceType,
                            TargetNodeId = target,
                            IsForward = isForward,
                        });
                        hasNode = !subtree.EOF;
                        continue;
                }
                hasNode = await subtree.ReadAsync();
            }
        }

        return new AddressSpaceNode
        {
            NodeId = CanonicalizeNodeId(localNodeId, namespaceUris, aliases),
            LocalNodeId = localNodeId,
            BrowseName = browseName,
            DisplayName = displayName.Length == 0 ? browseName.Name : displayName,
            Description = description,
            NodeClass = nodeClass,
            ParentNodeId = string.IsNullOrWhiteSpace(parent)
                ? null
                : CanonicalizeNodeId(parent, namespaceUris, aliases),
            DataType = string.IsNullOrWhiteSpace(dataType)
                ? null
                : CanonicalizeNodeId(dataType, namespaceUris, aliases),
            ValueRank = valueRank,
            ArrayDimensions = arrayDimensions,
            AccessLevel = accessLevel,
            UserAccessLevel = userAccessLevel,
            Executable = executable,
            UserExecutable = userExecutable,
            ValueType = valueType,
            ValueText = valueText,
            References = references,
        };
    }

    static async Task<(string? Type, string? Text)> ReadValueAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement) return (null, "");
        using var subtree = reader.ReadSubtree();
        await subtree.ReadAsync();
        string? type = null;
        var text = new System.Text.StringBuilder();
        var truncated = false;
        while (await subtree.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType == XmlNodeType.Element && subtree.Depth == 1)
                type ??= subtree.LocalName;
            if (subtree.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace))
                continue;
            var remaining = MaximumValueTextLength - text.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }
            var value = subtree.Value;
            if (value.Length > remaining)
            {
                text.Append(value.AsSpan(0, remaining));
                truncated = true;
            }
            else
            {
                text.Append(value);
            }
        }
        if (truncated) text.Append('…');
        return (type, text.ToString());
    }

    static void NormalizeGraph(
        Dictionary<string, AddressSpaceNode> nodes,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyList<string> namespaceUris)
    {
        var hasTypeDefinition = CanonicalizeNodeId("HasTypeDefinition", namespaceUris, aliases);
        var hasModellingRule = CanonicalizeNodeId("HasModellingRule", namespaceUris, aliases);
        var hierarchical = HierarchicalReferenceIds
            .Select(id => CanonicalizeNodeId(id, namespaceUris, aliases))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in nodes.Values)
        {
            node.TypeDefinition = node.References.FirstOrDefault(
                r => r.IsForward && r.ReferenceType == hasTypeDefinition)?.TargetNodeId;
            node.ModellingRule = node.References.FirstOrDefault(
                r => r.IsForward && r.ReferenceType == hasModellingRule)?.TargetNodeId;

            if (node.ParentNodeId is null)
            {
                node.ParentNodeId = node.References
                    .Where(r => !r.IsForward && hierarchical.Contains(r.ReferenceType))
                    .Select(r => r.TargetNodeId)
                    .FirstOrDefault(nodes.ContainsKey);
            }
        }

        foreach (var parent in nodes.Values)
        {
            foreach (var reference in parent.References.Where(
                         r => r.IsForward && hierarchical.Contains(r.ReferenceType) && nodes.ContainsKey(r.TargetNodeId)))
            {
                var child = nodes[reference.TargetNodeId];
                child.ParentNodeId ??= parent.NodeId;
            }
        }

        foreach (var node in nodes.Values)
        {
            if (node.ParentNodeId is not null && nodes.TryGetValue(node.ParentNodeId, out var parent))
                parent.Children.Add(node.NodeId);
        }
        foreach (var node in nodes.Values)
            node.Children.Sort((a, b) => CompareNodes(nodes[a], nodes[b]));

        var pathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
            node.BrowsePath = BuildBrowsePath(node.NodeId, nodes, pathCache, []);
    }

    static string BuildBrowsePath(
        string nodeId,
        IReadOnlyDictionary<string, AddressSpaceNode> nodes,
        Dictionary<string, string> cache,
        HashSet<string> active)
    {
        if (cache.TryGetValue(nodeId, out var cached)) return cached;
        var node = nodes[nodeId];
        if (!active.Add(nodeId))
            return "/" + node.BrowseName.Name;
        var parentPath = node.ParentNodeId is not null && nodes.ContainsKey(node.ParentNodeId)
            ? BuildBrowsePath(node.ParentNodeId, nodes, cache, active)
            : "";
        active.Remove(nodeId);
        var path = parentPath + "/" + node.BrowseName.Name;
        cache[nodeId] = path;
        return path;
    }

    public static string CanonicalizeNodeId(
        string value,
        IReadOnlyList<string> namespaceUris,
        IReadOnlyDictionary<string, string>? aliases = null,
        bool allowAlias = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var nodeId = value.Trim();
        if (allowAlias && aliases is not null)
        {
            var visitedAliases = new HashSet<string>(StringComparer.Ordinal);
            while (aliases.TryGetValue(nodeId, out var alias))
            {
                if (!visitedAliases.Add(nodeId))
                    throw new XmlException($"Alias cycle detected at '{nodeId}'.");
                nodeId = alias;
            }
        }

        if (nodeId.StartsWith("svr=", StringComparison.Ordinal) ||
            nodeId.StartsWith("nsu=", StringComparison.Ordinal))
        {
            return NormalizeExpandedNodeId(nodeId);
        }

        var namespaceIndex = 0;
        if (nodeId.StartsWith("ns=", StringComparison.Ordinal))
        {
            var separator = nodeId.IndexOf(';');
            if (separator < 4 ||
                !int.TryParse(nodeId.AsSpan(3, separator - 3), NumberStyles.None, CultureInfo.InvariantCulture, out namespaceIndex))
                throw new XmlException($"Invalid NodeId namespace index '{value}'.");
            nodeId = nodeId[(separator + 1)..];
        }

        if ((uint)namespaceIndex >= (uint)namespaceUris.Count)
            throw new XmlException($"NodeId '{value}' references undefined namespace index {namespaceIndex}.");
        if (!IsIdentifier(nodeId))
            throw new XmlException($"NodeId '{value}' has an unsupported identifier.");
        return $"nsu={AddressSpaceExpandedNodeId.EscapeNamespaceUri(namespaceUris[namespaceIndex])};{nodeId}";
    }

    static string NormalizeExpandedNodeId(string nodeId)
    {
        var nsuIndex = nodeId.IndexOf("nsu=", StringComparison.Ordinal);
        if (nsuIndex < 0) return nodeId;
        var idSeparator = FindIdentifierSeparator(nodeId, nsuIndex + 4);
        if (idSeparator < 0)
            throw new XmlException($"ExpandedNodeId '{nodeId}' has no identifier.");
        var uri = Uri.UnescapeDataString(nodeId[(nsuIndex + 4)..idSeparator]);
        var prefix = nodeId[..nsuIndex];
        var identifier = nodeId[(idSeparator + 1)..];
        return $"{prefix}nsu={AddressSpaceExpandedNodeId.EscapeNamespaceUri(uri)};{identifier}";
    }

    static int FindIdentifierSeparator(string value, int start)
    {
        for (var i = start; i < value.Length - 2; i++)
        {
            if (value[i] == ';' && value[i + 2] == '=' && "isgb".Contains(value[i + 1]))
                return i;
        }
        return -1;
    }

    static AddressSpaceQualifiedName ParseBrowseName(string browseName, IReadOnlyList<string> namespaceUris)
    {
        var separator = browseName.IndexOf(':');
        var namespaceIndex = 0;
        var name = browseName;
        if (separator > 0 &&
            int.TryParse(browseName.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            namespaceIndex = parsed;
            name = browseName[(separator + 1)..];
        }
        if ((uint)namespaceIndex >= (uint)namespaceUris.Count)
            throw new XmlException($"BrowseName '{browseName}' references undefined namespace index {namespaceIndex}.");
        return new AddressSpaceQualifiedName { NamespaceUri = namespaceUris[namespaceIndex], Name = name };
    }

    static bool IsIdentifier(string value) =>
        value.Length >= 3 && value[1] == '=' && "isgb".Contains(value[0]);

    static string RequiredAttribute(XmlReader reader, string name) =>
        reader.GetAttribute(name) is { Length: > 0 } value
            ? value
            : throw new XmlException($"Element '{reader.LocalName}' requires attribute '{name}'.");

    static int CompareNodes(AddressSpaceNode left, AddressSpaceNode right)
    {
        var result = StringComparer.Ordinal.Compare(left.BrowseName.NamespaceUri, right.BrowseName.NamespaceUri);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.BrowseName.Name, right.BrowseName.Name);
    }

    static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    static byte? ParseNullableByte(string? value) =>
        byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    static bool? ParseNullableBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : null;

    static IReadOnlyList<uint> ParseArrayDimensions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<uint>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var dimension))
                throw new XmlException($"Invalid ArrayDimensions value '{value}'.");
            result.Add(dimension);
        }
        return result;
    }
}

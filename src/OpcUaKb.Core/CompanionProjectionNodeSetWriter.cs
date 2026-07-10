using System.Globalization;
using System.Xml;

public sealed class CompanionProjectionNodeSetWriter
{
    const string NodeSetNamespace = AddressSpaceNodeSetReader.NodeSetNamespace;
    const string TypesNamespace = "http://opcfoundation.org/UA/2011/03/UANodeSetTypes.xsd";
    const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    public async Task WriteAsync(
        CompanionMappingDocument document,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var nodes = BuildNodes(document);
        var namespaces = BuildNamespaceTable(document, nodes);
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = false,
        };
        await using var writer = XmlWriter.Create(output, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "UANodeSet", NodeSetNamespace);
        await writer.WriteAttributeStringAsync("xmlns", "xsi", null, XsiNamespace);
        await writer.WriteAttributeStringAsync("xmlns", "uax", null, TypesNamespace);

        await writer.WriteStartElementAsync(null, "NamespaceUris", NodeSetNamespace);
        foreach (var uri in namespaces.Skip(1))
            await writer.WriteElementStringAsync(null, "Uri", NodeSetNamespace, uri);
        await writer.WriteEndElementAsync();
        await WriteModelsAsync(writer, document.Model);

        foreach (var node in nodes.OrderBy(n => n.SortKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteNodeAsync(writer, node, namespaces);
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    public async Task<byte[]> WriteBytesAsync(
        CompanionMappingDocument document,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream();
        await WriteAsync(document, stream, cancellationToken);
        return stream.ToArray();
    }

    static List<OutputNode> BuildNodes(CompanionMappingDocument document)
    {
        var nodes = new List<OutputNode>();
        var rootId = StableInfrastructureId(document.Model.OutputModelUri, "root");
        nodes.Add(new OutputNode
        {
            NodeClass = "Object",
            NodeId = rootId,
            BrowseName = "CompanionProjections",
            BrowseNameNamespaceUri = document.Model.OutputModelUri,
            DisplayName = "Companion Projections",
            ParentNodeId = CanonicalUa("i=85"),
            ReferenceTypeToParent = CanonicalUa("i=35"),
            TypeDefinition = CanonicalUa("i=61"),
            SortKey = "0000",
        });

        var accepted = document.Projections
            .Where(p => p.Status == CompanionDecisionStatus.Accepted &&
                        p.Mappings.Any(m => m.Kind != CompanionMappingKind.PassThrough) &&
                        p.TargetType is not null)
            .ToArray();
        foreach (var specGroup in accepted.GroupBy(
                     p => p.TargetType!.ModelUri ?? "Unspecified", StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var specId = StableInfrastructureId(document.Model.OutputModelUri, $"spec:{specGroup.Key}");
            nodes.Add(new OutputNode
            {
                NodeClass = "Object",
                NodeId = specId,
                BrowseName = SafeBrowseName(specGroup.Key),
                BrowseNameNamespaceUri = document.Model.OutputModelUri,
                DisplayName = specGroup.Key,
                ParentNodeId = rootId,
                ReferenceTypeToParent = CanonicalUa("i=35"),
                TypeDefinition = CanonicalUa("i=61"),
                SortKey = $"1000/{specGroup.Key}",
            });

            foreach (var projection in specGroup.OrderBy(p => p.Ordinal))
            {
                var rootMapping = projection.Mappings.Single(m => m.DeclarationPath == "$");
                var projectionId = rootMapping.Target!.ExpandedNodeId;
                nodes.Add(new OutputNode
                {
                    NodeClass = "Object",
                    NodeId = projectionId,
                    BrowseName = projection.SourceRoot.BrowseName,
                    BrowseNameNamespaceUri = document.Model.OutputModelUri,
                    DisplayName = projection.SourceRoot.BrowseName,
                    ParentNodeId = specId,
                    ReferenceTypeToParent = CanonicalUa("i=35"),
                    TypeDefinition = projection.TargetType!.ExpandedNodeId,
                    SortKey = $"2000/{projection.Ordinal:D8}",
                });

                var byPath = new Dictionary<string, string>(StringComparer.Ordinal) { ["$"] = projectionId };
                foreach (var mapping in projection.Mappings
                             .Where(m => m.DeclarationPath is not null and not "$")
                             .OrderBy(m => PathDepth(m.DeclarationPath!))
                             .ThenBy(m => m.DeclarationPath, StringComparer.Ordinal))
                {
                    if (mapping.Target is null) continue;
                    var parentPath = ParentPath(mapping.DeclarationPath!);
                    if (!byPath.TryGetValue(parentPath, out var parentId))
                        throw new InvalidOperationException(
                            $"Mapped declaration '{mapping.DeclarationPath}' has no generated parent '{parentPath}'.");
                    var declarationClass = mapping.TargetNodeClass ?? mapping.Kind switch
                    {
                        CompanionMappingKind.Variable => "Variable",
                        CompanionMappingKind.Method => "Method",
                        CompanionMappingKind.Event => "Object",
                        CompanionMappingKind.UnboundRequired when
                            mapping.Direction == CompanionMappingDirection.MethodForward => "Method",
                        CompanionMappingKind.UnboundRequired => "Variable",
                        _ => "Object",
                    };
                    nodes.Add(new OutputNode
                    {
                        NodeClass = declarationClass,
                        NodeId = mapping.Target.ExpandedNodeId,
                        BrowseName = mapping.Target.BrowseName,
                        BrowseNameNamespaceUri = mapping.TargetBrowseNameNamespaceUri ??
                            document.Model.OutputModelUri,
                        DisplayName = mapping.Target.BrowseName,
                        ParentNodeId = parentId,
                        ReferenceTypeToParent = mapping.TargetReferenceType ??
                            CanonicalUa(declarationClass == "Variable" ? "i=46" : "i=47"),
                        TypeDefinition = mapping.TargetTypeDefinition ?? declarationClass switch
                        {
                            "Variable" => CanonicalUa("i=63"),
                            "Object" => CanonicalUa("i=58"),
                            _ => null,
                        },
                        DataType = declarationClass == "Variable"
                            ? mapping.TargetDataType ?? CanonicalUa("i=24")
                            : null,
                        AccessLevel = declarationClass == "Variable"
                            ? mapping.Direction == CompanionMappingDirection.ReadWrite ? (byte)3 : (byte)1
                            : null,
                        Executable = declarationClass == "Method" && mapping.Kind != CompanionMappingKind.UnboundRequired,
                        UserExecutable = declarationClass == "Method" && mapping.Kind != CompanionMappingKind.UnboundRequired,
                        SortKey = $"3000/{projection.Ordinal:D8}/{mapping.DeclarationPath}",
                    });
                    byPath[mapping.DeclarationPath!] = mapping.Target.ExpandedNodeId;
                }
            }
        }

        AddForwardChildren(nodes);
        return nodes;
    }

    static void AddForwardChildren(List<OutputNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.ParentNodeId is null || !byId.TryGetValue(node.ParentNodeId, out var parent))
                continue;
            parent.Children.Add((node.ReferenceTypeToParent, node.NodeId));
        }
    }

    static IReadOnlyList<string> BuildNamespaceTable(
        CompanionMappingDocument document,
        IReadOnlyList<OutputNode> nodes)
    {
        var namespaces = new List<string>
        {
            AddressSpaceNodeSetReader.UaNamespace,
            document.Model.OutputModelUri,
        };
        foreach (var uri in EffectiveRequiredModels(document.Model).Select(model => model.ModelUri))
        {
            if (!namespaces.Contains(uri, StringComparer.Ordinal)) namespaces.Add(uri);
        }
        foreach (var uri in nodes.Select(node => node.BrowseNameNamespaceUri)
                     .Where(uri => !string.IsNullOrWhiteSpace(uri))
                     .Cast<string>())
        {
            if (!namespaces.Contains(uri, StringComparer.Ordinal)) namespaces.Add(uri);
        }
        foreach (var id in nodes.SelectMany(n => new[]
                 {
                     n.NodeId, n.ParentNodeId, n.TypeDefinition, n.DataType,
                     n.ReferenceTypeToParent,
                 }).Where(id => id is not null).Cast<string>())
        {
            var uri = NamespaceOf(id);
            if (uri is not null && !namespaces.Contains(uri, StringComparer.Ordinal))
                namespaces.Add(uri);
        }
        return namespaces;
    }

    static async Task WriteModelsAsync(XmlWriter writer, CompanionProjectionModelMetadata model)
    {
        await writer.WriteStartElementAsync(null, "Models", NodeSetNamespace);
        await writer.WriteStartElementAsync(null, "Model", NodeSetNamespace);
        await writer.WriteAttributeStringAsync(null, "ModelUri", null, model.OutputModelUri);
        if (!string.IsNullOrWhiteSpace(model.OutputVersion))
            await writer.WriteAttributeStringAsync(null, "Version", null, model.OutputVersion);
        await writer.WriteAttributeStringAsync(
            null, "PublicationDate", null, model.GeneratedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        foreach (var required in EffectiveRequiredModels(model)
                     .OrderBy(required => required.ModelUri, StringComparer.Ordinal))
        {
            await writer.WriteStartElementAsync(null, "RequiredModel", NodeSetNamespace);
            await writer.WriteAttributeStringAsync(null, "ModelUri", null, required.ModelUri);
            if (!string.IsNullOrWhiteSpace(required.Version))
                await writer.WriteAttributeStringAsync(null, "Version", null, required.Version);
            if (required.PublicationDate is { } publicationDate)
            {
                await writer.WriteAttributeStringAsync(
                    null,
                    "PublicationDate",
                    null,
                    publicationDate.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }
            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
    }

    static IReadOnlyList<AddressSpaceRequiredModel> EffectiveRequiredModels(
        CompanionProjectionModelMetadata model)
    {
        if (model.RequiredModels.Count > 0) return model.RequiredModels;
        return model.RequiredModelUris
            .Select(uri => new AddressSpaceRequiredModel { ModelUri = uri })
            .ToArray();
    }

    static async Task WriteNodeAsync(
        XmlWriter writer,
        OutputNode node,
        IReadOnlyList<string> namespaces)
    {
        await writer.WriteStartElementAsync(null, "UA" + node.NodeClass, NodeSetNamespace);
        await writer.WriteAttributeStringAsync(null, "NodeId", null, ToNodeId(node.NodeId, namespaces));
        await writer.WriteAttributeStringAsync(
            null,
            "BrowseName",
            null,
            ToBrowseName(node.BrowseName, node.BrowseNameNamespaceUri, namespaces));
        if (node.ParentNodeId is not null)
            await writer.WriteAttributeStringAsync(
                null, "ParentNodeId", null, ToNodeId(node.ParentNodeId, namespaces));
        if (node.NodeClass == "Variable")
        {
            await writer.WriteAttributeStringAsync(
                null, "DataType", null, ToNodeId(node.DataType ?? CanonicalUa("i=24"), namespaces));
            await writer.WriteAttributeStringAsync(
                null, "AccessLevel", null, (node.AccessLevel ?? 1).ToString(CultureInfo.InvariantCulture));
            await writer.WriteAttributeStringAsync(
                null, "UserAccessLevel", null, (node.AccessLevel ?? 1).ToString(CultureInfo.InvariantCulture));
        }
        if (node.NodeClass == "Method")
        {
            await writer.WriteAttributeStringAsync(
                null, "Executable", null, (node.Executable ?? false).ToString().ToLowerInvariant());
            await writer.WriteAttributeStringAsync(
                null, "UserExecutable", null, (node.UserExecutable ?? false).ToString().ToLowerInvariant());
        }
        await writer.WriteElementStringAsync(null, "DisplayName", NodeSetNamespace, node.DisplayName);
        await writer.WriteStartElementAsync(null, "References", NodeSetNamespace);
        if (node.ParentNodeId is not null)
        {
            await WriteReferenceAsync(
                writer, node.ReferenceTypeToParent, node.ParentNodeId, false, namespaces);
        }
        if (node.TypeDefinition is not null)
            await WriteReferenceAsync(
                writer, CanonicalUa("i=40"), node.TypeDefinition, true, namespaces);
        foreach (var child in node.Children.OrderBy(c => c.Target, StringComparer.Ordinal))
            await WriteReferenceAsync(writer, child.ReferenceType, child.Target, true, namespaces);
        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
    }

    static async Task WriteReferenceAsync(
        XmlWriter writer,
        string referenceType,
        string target,
        bool isForward,
        IReadOnlyList<string> namespaces)
    {
        await writer.WriteStartElementAsync(null, "Reference", NodeSetNamespace);
        await writer.WriteAttributeStringAsync(
            null, "ReferenceType", null, ToNodeId(referenceType, namespaces));
        if (!isForward)
            await writer.WriteAttributeStringAsync(null, "IsForward", null, "false");
        await writer.WriteStringAsync(ToNodeId(target, namespaces));
        await writer.WriteEndElementAsync();
    }

    static string StableInfrastructureId(string modelUri, string key) =>
        CompanionTargetNodeId.Create(modelUri, $"infrastructure:{key}", CanonicalUa("i=61"), key, 0);

    static string CanonicalUa(string identifier) =>
        $"nsu={AddressSpaceExpandedNodeId.EscapeNamespaceUri(AddressSpaceNodeSetReader.UaNamespace)};{identifier}";

    static string ToNodeId(string expandedNodeId, IReadOnlyList<string> namespaces)
    {
        var namespaceUri = NamespaceOf(expandedNodeId)
            ?? throw new InvalidOperationException($"NodeId '{expandedNodeId}' has no namespace URI.");
        var namespaceIndex = IndexOf(namespaces, namespaceUri);
        var separator = expandedNodeId.IndexOf(';', expandedNodeId.IndexOf("nsu=", StringComparison.Ordinal) + 4);
        var identifier = expandedNodeId[(separator + 1)..];
        return namespaceIndex == 0 ? identifier : $"ns={namespaceIndex};{identifier}";
    }

    static string ToBrowseName(string name, string? namespaceUri, IReadOnlyList<string> namespaces)
    {
        var index = namespaceUri is null ? 1 : IndexOf(namespaces, namespaceUri);
        return index == 0 ? name : $"{index}:{name}";
    }

    static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
        throw new InvalidOperationException($"Namespace URI '{value}' is absent from NamespaceUris.");
    }

    static string? NamespaceOf(string expandedNodeId)
    {
        var start = expandedNodeId.IndexOf("nsu=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 4;
        var end = expandedNodeId.IndexOf(';', start);
        return end < 0 ? null : Uri.UnescapeDataString(expandedNodeId[start..end]);
    }

    static int PathDepth(string path) => path.Count(c => c == '/');

    static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "$" : path[..separator];
    }

    static string SafeBrowseName(string value)
    {
        var candidate = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Segments.Length > 0)
            {
                candidate = uri.Segments
                    .Select(segment => segment.Trim('/'))
                    .LastOrDefault(segment => segment.Length > 0)
                    ?? uri.Host;
            }
            if (uri.Scheme.Equals("urn", StringComparison.OrdinalIgnoreCase))
                candidate = value.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
        }

        var result = new string(candidate.Where(char.IsLetterOrDigit).ToArray());
        if (result.Length > 0 && char.IsLower(result[0]))
            result = char.ToUpperInvariant(result[0]) + result[1..];
        return result.Length == 0 ? "CompanionModel" : result;
    }

    sealed class OutputNode
    {
        public required string NodeClass { get; init; }
        public required string NodeId { get; init; }
        public required string BrowseName { get; init; }
        public required string BrowseNameNamespaceUri { get; init; }
        public required string DisplayName { get; init; }
        public string? ParentNodeId { get; init; }
        public required string ReferenceTypeToParent { get; init; }
        public string? TypeDefinition { get; init; }
        public string? DataType { get; init; }
        public byte? AccessLevel { get; init; }
        public bool? Executable { get; init; }
        public bool? UserExecutable { get; init; }
        public required string SortKey { get; init; }
        public List<(string ReferenceType, string Target)> Children { get; } = [];
    }
}

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;

public sealed record CompanionModelCatalogDocument
{
    [JsonPropertyName("schema_version")]
    public string? SchemaVersion { get; init; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset? GeneratedAt { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<CompanionModelCatalogEntry> Models { get; init; } = [];
}

public sealed record CompanionModelCatalogEntry
{
    [JsonPropertyName("model_uri")]
    public required string ModelUri { get; init; }

    [JsonPropertyName("namespace_uri")]
    public string? NamespaceUri { get; init; }

    [JsonPropertyName("namespace_uris")]
    public IReadOnlyList<string> NamespaceUris { get; init; } = [];

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("publication_date")]
    public DateTimeOffset? PublicationDate { get; init; }

    [JsonPropertyName("source_blob")]
    public required string SourceBlob { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("spec_id")]
    public string? SpecId { get; init; }

    [JsonPropertyName("is_latest")]
    public bool IsLatest { get; init; }

    [JsonPropertyName("is_official")]
    public bool IsOfficial { get; init; } = true;

    [JsonPropertyName("required_models")]
    public IReadOnlyList<AddressSpaceRequiredModel> RequiredModels { get; init; } = [];
}

public sealed record CompanionModelGraph
{
    public required CompanionModelCatalogEntry CatalogEntry { get; init; }
    public required AddressSpaceGraph AddressSpace { get; init; }
    public required IReadOnlyDictionary<string, string> Supertypes { get; init; }
}

public sealed record CompanionDeclaration
{
    public required string DeclarationPath { get; init; }
    public required AddressSpaceNode Node { get; init; }
    public required string DeclaringTypeNodeId { get; init; }
    public string? ReferenceType { get; init; }
    public bool IsMandatory { get; init; }
    public bool IsPlaceholder { get; init; }
}

public interface ICompanionModelRepository : ICompanionModelCatalog
{
    Task<CompanionModelCatalogEntry?> ResolveAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default);

    Task<CompanionModelGraph> LoadModelAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanionDeclaration>> ExpandDeclarationsAsync(
        string modelUri,
        string targetTypeExpandedNodeId,
        string? version = null,
        CancellationToken cancellationToken = default);
}

public sealed class CompanionModelRepository : ICompanionModelRepository
{
    public const string DefaultCatalogBlobName = "nodesets/model-catalog.json.gz";

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly BlobContainerClient _container;
    readonly string _catalogBlobName;
    readonly AddressSpaceNodeSetReader _reader;
    readonly SemaphoreSlim _catalogLock = new(1, 1);
    readonly ConcurrentDictionary<string, Lazy<Task<CompanionModelGraph>>> _models =
        new(StringComparer.Ordinal);
    IReadOnlyList<CompanionModelCatalogEntry>? _catalog;

    public CompanionModelRepository(
        BlobContainerClient container,
        string catalogBlobName = DefaultCatalogBlobName,
        AddressSpaceNodeSetReader? reader = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _catalogBlobName = string.IsNullOrWhiteSpace(catalogBlobName)
            ? throw new ArgumentException("Catalog blob name is required.", nameof(catalogBlobName))
            : catalogBlobName;
        _reader = reader ?? new AddressSpaceNodeSetReader();
    }

    public async Task<IReadOnlyList<CompanionModelCatalogEntry>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        if (_catalog is not null) return _catalog;
        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null) return _catalog;
            await using var source = await _container.GetBlobClient(_catalogBlobName)
                .OpenReadAsync(cancellationToken: cancellationToken);
            await using var decoded = await OpenPossiblyGzipAsync(source, cancellationToken);
            _catalog = await ReadCatalogAsync(decoded, cancellationToken);
            return _catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async Task<CompanionModelCatalogEntry?> ResolveAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelUri);
        var matches = (await GetCatalogAsync(cancellationToken))
            .Where(m => string.Equals(m.ModelUri, modelUri, StringComparison.Ordinal) ||
                        string.Equals(m.NamespaceUri, modelUri, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(version))
            return matches.FirstOrDefault(m => string.Equals(m.Version, version, StringComparison.Ordinal));

        return matches
            .OrderByDescending(m => m.IsLatest)
            .ThenByDescending(m => m.PublicationDate)
            .ThenByDescending(m => m.Version, SemanticVersionComparer.Instance)
            .FirstOrDefault();
    }

    public async Task<CompanionModelGraph> LoadModelAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await ResolveAsync(modelUri, version, cancellationToken)
            ?? throw new KeyNotFoundException($"Companion model '{modelUri}' version '{version ?? "latest"}' was not found.");
        var key = $"{entry.ModelUri}\n{entry.Version}\n{entry.SourceBlob}";
        var lazy = _models.GetOrAdd(
            key,
            _ => new Lazy<Task<CompanionModelGraph>>(
                () => LoadModelCoreAsync(entry),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return await lazy.Value.WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CompanionDeclaration>> ExpandDeclarationsAsync(
        string modelUri,
        string targetTypeExpandedNodeId,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var model = await LoadModelAsync(modelUri, version, cancellationToken);
        if (!model.AddressSpace.Nodes.ContainsKey(targetTypeExpandedNodeId))
            throw new KeyNotFoundException($"Type '{targetTypeExpandedNodeId}' was not found in model '{modelUri}'.");

        var declarations = new Dictionary<string, CompanionDeclaration>(StringComparer.Ordinal);
        ExpandType(
            model,
            targetTypeExpandedNodeId,
            "",
            declarations,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        return declarations.Values
            .OrderBy(d => d.DeclarationPath, StringComparer.Ordinal)
            .ToArray();
    }

    async Task<CompanionModelGraph> LoadModelCoreAsync(CompanionModelCatalogEntry entry)
    {
        await using var source = await _container.GetBlobClient(entry.SourceBlob).OpenReadAsync();
        await using var decoded = await OpenPossiblyGzipAsync(source, CancellationToken.None);
        var graph = await _reader.ReadAsync(decoded);
        var hasSubtype = AddressSpaceNodeSetReader.CanonicalizeNodeId(
            "HasSubtype", graph.NamespaceUris, graph.Aliases);
        var supertypes = graph.Nodes.Values
            .Select(node => (
                node.NodeId,
                Supertype: node.References.FirstOrDefault(
                    r => !r.IsForward && r.ReferenceType == hasSubtype)?.TargetNodeId))
            .Where(pair => pair.Supertype is not null)
            .ToDictionary(pair => pair.NodeId, pair => pair.Supertype!, StringComparer.Ordinal);
        return new CompanionModelGraph
        {
            CatalogEntry = entry,
            AddressSpace = graph,
            Supertypes = supertypes,
        };
    }

    static void ExpandType(
        CompanionModelGraph model,
        string typeNodeId,
        string pathPrefix,
        Dictionary<string, CompanionDeclaration> declarations,
        HashSet<string> activeTypes,
        HashSet<string> activeNodes)
    {
        if (!activeTypes.Add(typeNodeId)) return;
        if (model.Supertypes.TryGetValue(typeNodeId, out var supertype) &&
            model.AddressSpace.Nodes.ContainsKey(supertype))
        {
            ExpandType(model, supertype, pathPrefix, declarations, activeTypes, activeNodes);
        }

        if (model.AddressSpace.Nodes.TryGetValue(typeNodeId, out var type))
        {
            foreach (var childId in type.Children)
                ExpandDeclarationNode(
                    model, childId, typeNodeId, typeNodeId, pathPrefix,
                    declarations, activeTypes, activeNodes);
        }
        activeTypes.Remove(typeNodeId);
    }

    static void ExpandDeclarationNode(
        CompanionModelGraph model,
        string nodeId,
        string declaringType,
        string parentNodeId,
        string pathPrefix,
        Dictionary<string, CompanionDeclaration> declarations,
        HashSet<string> activeTypes,
        HashSet<string> activeNodes)
    {
        if (!activeNodes.Add(nodeId)) return;
        var node = model.AddressSpace.Nodes[nodeId];
        var path = pathPrefix.Length == 0
            ? node.BrowseName.Name
            : $"{pathPrefix}/{node.BrowseName.Name}";
        declarations[path] = new CompanionDeclaration
        {
            DeclarationPath = path,
            Node = node,
            DeclaringTypeNodeId = declaringType,
            ReferenceType = ResolveDeclarationReferenceType(model.AddressSpace, parentNodeId, node),
            IsMandatory = IsMandatory(node.ModellingRule),
            IsPlaceholder = IsPlaceholder(node.ModellingRule),
        };

        if (node.TypeDefinition is { } nestedType &&
            model.AddressSpace.Nodes.ContainsKey(nestedType) &&
            nestedType != declaringType)
        {
            ExpandType(model, nestedType, path, declarations, activeTypes, activeNodes);
        }
        foreach (var childId in node.Children)
            ExpandDeclarationNode(
                model, childId, declaringType, nodeId, path,
                declarations, activeTypes, activeNodes);
        activeNodes.Remove(nodeId);
    }

    static string? ResolveDeclarationReferenceType(
        AddressSpaceGraph graph,
        string parentNodeId,
        AddressSpaceNode child)
    {
        if (graph.Nodes.TryGetValue(parentNodeId, out var parent))
        {
            var forward = parent.References.FirstOrDefault(reference =>
                reference.IsForward &&
                reference.TargetNodeId == child.NodeId &&
                IsDeclarationReference(reference.ReferenceType));
            if (forward is not null) return forward.ReferenceType;
        }

        return child.References.FirstOrDefault(reference =>
            !reference.IsForward &&
            reference.TargetNodeId == parentNodeId &&
            IsDeclarationReference(reference.ReferenceType))?.ReferenceType;
    }

    static bool IsDeclarationReference(string referenceType) =>
        !referenceType.EndsWith(";i=40", StringComparison.Ordinal) &&
        !referenceType.EndsWith(";i=37", StringComparison.Ordinal) &&
        !referenceType.EndsWith(";i=45", StringComparison.Ordinal);

    static bool IsMandatory(string? modellingRule) =>
        modellingRule is not null &&
        (modellingRule.EndsWith(";i=78", StringComparison.Ordinal) ||
         modellingRule.EndsWith(";i=11510", StringComparison.Ordinal));

    static bool IsPlaceholder(string? modellingRule) =>
        modellingRule is not null &&
        (modellingRule.EndsWith(";i=11508", StringComparison.Ordinal) ||
         modellingRule.EndsWith(";i=11510", StringComparison.Ordinal));

    static async Task<IReadOnlyList<CompanionModelCatalogEntry>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var modelsElement = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement,
            JsonValueKind.Object when TryProperty(document.RootElement, "models", out var models) => models,
            JsonValueKind.Object when TryProperty(document.RootElement, "items", out var items) => items,
            _ => throw new JsonException("Model catalog must be an array or contain a 'models' array."),
        };
        if (modelsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Model catalog models value must be an array.");

        var result = new List<CompanionModelCatalogEntry>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            var modelUri = StringProperty(item, "model_uri", "modelUri", "namespace_uri", "namespaceUri");
            var blob = StringProperty(item, "source_blob", "sourceBlob", "blob_name", "blobName", "blob");
            if (string.IsNullOrWhiteSpace(modelUri) || string.IsNullOrWhiteSpace(blob))
                throw new JsonException("Each catalog model requires model_uri and source_blob.");
            result.Add(new CompanionModelCatalogEntry
            {
                ModelUri = modelUri,
                NamespaceUri = StringProperty(item, "namespace_uri", "namespaceUri") ?? modelUri,
                NamespaceUris = ReadStrings(item, "namespace_uris", "namespaceUris"),
                Version = StringProperty(item, "version", "model_version", "modelVersion"),
                PublicationDate = DateProperty(item, "publication_date", "publicationDate"),
                SourceBlob = blob,
                Source = StringProperty(item, "source"),
                SpecId = StringProperty(item, "spec_id", "specId"),
                IsLatest = BoolProperty(item, "is_latest", "isLatest") ?? false,
                IsOfficial = BoolProperty(item, "is_official", "isOfficial") ??
                    !string.Equals(StringProperty(item, "source"), "cloudlib", StringComparison.OrdinalIgnoreCase),
                RequiredModels = ReadRequiredModels(item),
            });
        }
        return result;
    }

    static IReadOnlyList<AddressSpaceRequiredModel> ReadRequiredModels(JsonElement item)
    {
        if (!TryProperty(item, "required_models", out var required) &&
            !TryProperty(item, "requiredModels", out required))
            return [];
        if (required.ValueKind != JsonValueKind.Array) return [];
        return required.EnumerateArray().Select(model => new AddressSpaceRequiredModel
        {
            ModelUri = StringProperty(model, "model_uri", "modelUri", "namespace_uri", "namespaceUri")
                ?? throw new JsonException("Required model has no model_uri."),
            Version = StringProperty(model, "version"),
            PublicationDate = DateProperty(model, "publication_date", "publicationDate"),
        }).ToArray();
    }

    static IReadOnlyList<string> ReadStrings(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(item, name, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            return value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .ToArray();
        }
        return [];
    }

    static async Task<Stream> OpenPossiblyGzipAsync(Stream source, CancellationToken cancellationToken)
    {
        var prefix = new byte[2];
        var read = await source.ReadAsync(prefix, cancellationToken);
        Stream combined;
        if (source.CanSeek)
        {
            source.Position -= read;
            combined = source;
        }
        else
        {
            combined = new PrefixReadStream(prefix.AsMemory(0, read).ToArray(), source);
        }
        return read == 2 && prefix[0] == 0x1f && prefix[1] == 0x8b
            ? new GZipStream(combined, CompressionMode.Decompress)
            : combined;
    }

    static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    static string? StringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    static bool? BoolProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }

    static DateTimeOffset? DateProperty(JsonElement element, params string[] names)
    {
        var value = StringProperty(element, names);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    sealed class SemanticVersionComparer : IComparer<string?>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftParts = left.Split(['.', '-', '+']);
            var rightParts = right.Split(['.', '-', '+']);
            for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
            {
                if (i >= leftParts.Length) return -1;
                if (i >= rightParts.Length) return 1;
                var lNumber = int.TryParse(leftParts[i], out var ln);
                var rNumber = int.TryParse(rightParts[i], out var rn);
                var comparison = lNumber && rNumber
                    ? ln.CompareTo(rn)
                    : StringComparer.OrdinalIgnoreCase.Compare(leftParts[i], rightParts[i]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }

    sealed class PrefixReadStream(byte[] prefix, Stream remainder) : Stream
        {
            readonly MemoryStream _prefix = new(prefix, writable: false);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _prefix.Read(buffer, offset, count);
                return read > 0 ? read : remainder.Read(buffer, offset, count);
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                var read = await _prefix.ReadAsync(buffer, cancellationToken);
                return read > 0 ? read : await remainder.ReadAsync(buffer, cancellationToken);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _prefix.Dispose();
                    remainder.Dispose();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _prefix.DisposeAsync();
                await remainder.DisposeAsync();
                GC.SuppressFinalize(this);
        }
    }
}

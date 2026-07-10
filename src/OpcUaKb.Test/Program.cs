using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;

// ── Test selector ──────────────────────────────────────────────────────
// Usage:
//   dotnet run --project src/OpcUaKb.Test           → run all tests (KB test skipped if no SEARCH_API_KEY)
//   dotnet run --project src/OpcUaKb.Test -- url    → run only the URL absolutizer test
//   dotnet run --project src/OpcUaKb.Test -- sts    → run only the STS metadata parser test
//   dotnet run --project src/OpcUaKb.Test -- html   → run only the spec HTML parser test
//   dotnet run --project src/OpcUaKb.Test -- mapping → run local companion-projection parser/filter tests
//   dotnet run --project src/OpcUaKb.Test -- kb     → run only the live Knowledge Base test
var selector = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

if (selector is "url" or "all")
{
    RunUrlHelperTest();
    if (selector == "url") return;
}

if (selector is "sts" or "all")
{
    RunStsMetadataParserTest();
    if (selector == "sts") return;
}

if (selector is "html" or "all")
{
    RunSpecHtmlParserTest();
    if (selector == "html") return;
}

if (selector is "mapping" or "all")
{
    await RunMappingParserTestAsync();
    await RunMappingEngineTestAsync();
    await RunMappingArtifactTestAsync();
    if (selector == "mapping") return;
}

// ── Configuration ──────────────────────────────────────────────────────
const string SearchEndpoint    = "https://opcua-kb-search.search.windows.net";
const string KnowledgeBaseName = "opcua-kb";
const string ApiVersion        = "2025-11-01-preview";

var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY");
if (string.IsNullOrEmpty(searchApiKey))
{
    Console.WriteLine("SEARCH_API_KEY not set — skipping Knowledge Base test.");
    return;
}

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("api-key", searchApiKey);

var testQueries = new[]
{
    "What are the main OPC UA service sets defined in Part 4?",
    "Explain the OPC UA security model and certificate handling from Part 2",
    "What is the OPC UA Pub/Sub transport protocol? Which Part covers it?",
    "How does the OPC UA Browse service work? Show a C# code example using the .NET Standard SDK",
    "What node classes are defined in Part 3 Address Space Model?",
    "Describe the OPC UA Alarm and Conditions model from Part 9",
    "What is the difference between OPC UA DataAccess and HistoricalAccess?",
};

Console.WriteLine($"Testing Knowledge Base '{KnowledgeBaseName}' with {testQueries.Length} queries...");
Console.WriteLine(new string('═', 80));

int passed = 0;
foreach (var (query, index) in testQueries.Select((q, i) => (q, i)))
{
    Console.WriteLine($"\n[{index + 1}/{testQueries.Length}] {query}");
    Console.WriteLine(new string('─', 80));

    try
    {
        var body = new
        {
            messages = new[]
            {
                new { role = "user", content = new[] { new { type = "text", text = query } } }
            },
            retrievalReasoningEffort = new { kind = "low" }
        };

        var response = await http.PostAsync(
            $"{SearchEndpoint}/knowledgebases/{KnowledgeBaseName}/retrieve?api-version={ApiVersion}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"  ✗ HTTP {(int)response.StatusCode}: {errorBody}");
            continue;
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonNode.Parse(json)!;

        var answerText = doc["response"]?[0]?["content"]?[0]?["text"]?.GetValue<string>();
        var refCount = doc["references"]?.AsArray().Count ?? 0;
        var activities = doc["activity"]?.AsArray();

        // Print truncated answer
        var preview = answerText?.Length > 300 ? answerText[..300] + "..." : answerText;
        Console.WriteLine($"  Answer: {preview}");
        Console.WriteLine($"  References: {refCount}");

        if (activities != null)
        {
            foreach (var act in activities)
            {
                var type = act?["type"]?.GetValue<string>();
                var elapsed = act?["elapsedMs"]?.GetValue<int>();
                if (type != null)
                    Console.WriteLine($"  Activity: {type} ({elapsed}ms)");
            }
        }

        if (!string.IsNullOrWhiteSpace(answerText))
        {
            Console.WriteLine("  ✓ PASSED");
            passed++;
        }
        else
        {
            Console.WriteLine("  ✗ FAILED — empty response");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ ERROR: {ex.Message}");
    }
}

Console.WriteLine($"\n{new string('═', 80)}");
Console.WriteLine($"Results: {passed}/{testQueries.Length} passed");
Console.WriteLine(passed == testQueries.Length ? "All tests passed! ✓" : "Some tests failed ✗");

// ═══════════════════════════════════════════════════════════════════════
// URL absolutizer test — guards against the Linux Uri.TryCreate quirk that
// previously caused SpecCatalog to emit relative `/specs/.../sts-xml`
// hrefs which then made HttpClient throw "An invalid request URI was
// provided" (production: opcua-kb-sc-pipeline-job-ix4nmeq, ~200 errors).
//
// Naive `Uri.TryCreate(href, UriKind.Absolute, out _)` returns TRUE on
// Linux for paths starting with `/` (parsed as `file://` URIs), but FALSE
// on Windows. The test below pins the behaviour we actually want: any
// root-relative href must be combined with the base URL on every OS.
// ═══════════════════════════════════════════════════════════════════════
static void RunUrlHelperTest()
{
    Console.WriteLine("URL absolutizer test");
    Console.WriteLine(new string('═', 80));
    Console.WriteLine($"  Platform: {Environment.OSVersion.Platform}  RID: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");

    const string Base = "https://reference.opcfoundation.org";

    var cases = new (string Href, string Expected, string Description)[]
    {
        ("/specs/OPC-10000-2/v1.04/t63914193972/download/sts-xml",
         "https://reference.opcfoundation.org/specs/OPC-10000-2/v1.04/t63914193972/download/sts-xml",
         "root-relative STS XML href is combined with the base"),

        ("/specs/OPC-10000-3/v1.05.06/t63914194059/download/markdown",
         "https://reference.opcfoundation.org/specs/OPC-10000-3/v1.05.06/t63914194059/download/markdown",
         "root-relative Markdown href is combined with the base"),

        ("/foo/bar",
         "https://reference.opcfoundation.org/foo/bar",
         "any other root-relative path is combined with the base (Linux quirk regression test)"),

        ("https://example.com/abs",
         "https://example.com/abs",
         "https:// absolute URL is returned unchanged"),

        ("HTTP://EXAMPLE.COM/Abs",
         "HTTP://EXAMPLE.COM/Abs",
         "scheme detection is case-insensitive and preserves casing"),

        ("relative/path",
         "https://reference.opcfoundation.org/relative/path",
         "schemeless relative path joins with a slash"),

        ("",
         "",
         "empty href is returned unchanged"),

        ("//cdn.example.com/foo",
         "https://cdn.example.com/foo",
         "protocol-relative URL inherits base scheme"),
    };

    var failures = new List<string>();
    foreach (var (href, expected, description) in cases)
    {
        var actual = UrlHelper.Absolutize(Base, href);
        var ok = actual == expected;
        var marker = ok ? "✓" : "✗";
        Console.WriteLine($"  {marker} {description}");
        Console.WriteLine($"      href     = \"{href}\"");
        Console.WriteLine($"      expected = \"{expected}\"");
        Console.WriteLine($"      actual   = \"{actual}\"");
        if (!ok) failures.Add(description);
    }

    if (failures.Count > 0)
    {
        Console.WriteLine($"\n  URL absolutizer test FAILED ({failures.Count} assertion(s) failed)");
        throw new InvalidOperationException(
            $"URL absolutizer test failed: {string.Join("; ", failures)}");
    }

    Console.WriteLine("  URL absolutizer test PASSED ✓");
    Console.WriteLine(new string('═', 80));
}

// ═══════════════════════════════════════════════════════════════════════
// STS metadata parser test
// ═══════════════════════════════════════════════════════════════════════
static void RunStsMetadataParserTest()
{
    Console.WriteLine("STS metadata parser test");
    Console.WriteLine(new string('═', 80));

    var path = Path.Combine(AppContext.BaseDirectory, "testdata", "opc-10000-3-v1.05.06-sts.xml");
    if (!File.Exists(path))
    {
        // Fall back to the source tree (when running outside the bin directory)
        path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "testdata", "opc-10000-3-v1.05.06-sts.xml");
        path = Path.GetFullPath(path);
    }

    if (!File.Exists(path))
        throw new FileNotFoundException("STS test snapshot not found", path);

    Console.WriteLine($"  Snapshot: {path}");
    var xml = File.ReadAllText(path);

    var parser = new StsMetadataParser(NullLogger<StsMetadataParser>.Instance);
    var meta = parser.Parse(xml);

    var failures = new List<string>();
    void Assert(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"  ✓ {description}");
        }
        else
        {
            Console.WriteLine($"  ✗ {description}");
            failures.Add(description);
        }
    }

    Assert(meta.SpecId == "OPC-10000-3", $"SpecId == \"OPC-10000-3\" (actual: \"{meta.SpecId}\")");
    Assert(meta.SpecVersion == "1.05.06", $"SpecVersion == \"1.05.06\" (actual: \"{meta.SpecVersion}\")");
    Assert(meta.PublicationDate == new DateOnly(2025, 10, 22),
        $"PublicationDate == 2025-10-22 (actual: {meta.PublicationDate})");
    Assert(meta.NamespaceUri == "http://opcfoundation.org/UA/",
        $"NamespaceUri == \"http://opcfoundation.org/UA/\" (actual: \"{meta.NamespaceUri}\")");
    Assert(meta.GitHubTag == "UA-1.05.06-2025-11-08/Schema",
        $"GitHubTag == \"UA-1.05.06-2025-11-08/Schema\" (actual: \"{meta.GitHubTag}\")");
    Assert(meta.SectionSlugByNumber.TryGetValue("5.6.2", out var s562) && s562 == "sec_5-6-2_variable-nodeclass",
        $"SectionSlugByNumber[\"5.6.2\"] == \"sec_5-6-2_variable-nodeclass\" (actual: \"{(meta.SectionSlugByNumber.GetValueOrDefault("5.6.2"))}\")");
    Assert(meta.SectionSlugByNumber.TryGetValue("1", out var s1) && s1 == "sec_1_scope",
        $"SectionSlugByNumber[\"1\"] == \"sec_1_scope\" (actual: \"{(meta.SectionSlugByNumber.GetValueOrDefault("1"))}\")");

    Console.WriteLine($"  Sections parsed: {meta.SectionSlugByNumber.Count}");

    if (failures.Count > 0)
    {
        Console.WriteLine($"\n  STS test FAILED ({failures.Count} assertion(s) failed)");
        throw new InvalidOperationException($"STS metadata parser test failed: {string.Join("; ", failures)}");
    }

    Console.WriteLine("  STS test PASSED ✓");
    Console.WriteLine(new string('═', 80));
}

// ═══════════════════════════════════════════════════════════════════════
// Spec HTML parser test — exercises SpecHtmlParser against the live
// Single Page view of Part 3 (Address Space Model) v1.05.06.
// ═══════════════════════════════════════════════════════════════════════
static void RunSpecHtmlParserTest()
{
    Console.WriteLine("Spec HTML parser test");
    Console.WriteLine(new string('═', 80));

    var path = Path.Combine(AppContext.BaseDirectory, "testdata", "opc-10000-3-v1.05.06-full.html");
    if (!File.Exists(path))
    {
        path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "testdata", "opc-10000-3-v1.05.06-full.html");
        path = Path.GetFullPath(path);
    }

    if (!File.Exists(path))
        throw new FileNotFoundException("HTML test snapshot not found", path);

    Console.WriteLine($"  Snapshot: {path}");
    var html = File.ReadAllText(path);

    var metadata = new SpecMetadata(
        SpecId: "OPC-10000-3",
        SpecTitle: "OPC Unified Architecture - Part 3: Address Space Model",
        SpecVersion: "1.05.06",
        PublicationDate: new DateOnly(2025, 10, 22),
        NamespaceUri: "http://opcfoundation.org/UA/",
        GitHubTag: "UA-1.05.06-2025-11-08/Schema");

    // Pre-build an STS slug map from the sibling snapshot if available so we can
    // also assert that slug resolution wires through end-to-end.
    IReadOnlyDictionary<string, string>? slugMap = null;
    var stsPath = Path.Combine(Path.GetDirectoryName(path)!, "opc-10000-3-v1.05.06-sts.xml");
    if (File.Exists(stsPath))
    {
        try
        {
            var stsParser = new StsMetadataParser(NullLogger<StsMetadataParser>.Instance);
            var stsMeta = stsParser.Parse(File.ReadAllText(stsPath));
            slugMap = stsMeta.SectionSlugByNumber;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (note) STS slug map unavailable: {ex.Message}");
        }
    }

    var parser = new SpecHtmlParser(NullLogger<SpecHtmlParser>.Instance);
    var chunks = parser.ParseSections(html, metadata, slugMap).ToList();

    Console.WriteLine($"  Sections parsed: {chunks.Count}");

    var failures = new List<string>();
    void Assert(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"  ✓ {description}");
        }
        else
        {
            Console.WriteLine($"  ✗ {description}");
            failures.Add(description);
        }
    }

    Assert(chunks.Count > 100, $"chunks.Count > 100 (actual: {chunks.Count})");

    var s562 = chunks.FirstOrDefault(c => c.SectionNumber == "5.6.2");
    Assert(s562 != null, "Section 5.6.2 exists");

    if (s562 != null)
    {
        Assert(s562.SectionTitle == "Variable NodeClass",
            $"5.6.2 title == \"Variable NodeClass\" (actual: \"{s562.SectionTitle}\")");

        var crumb = string.Join(" | ", s562.Breadcrumb);
        Assert(
            s562.Breadcrumb.Count == 2
                && s562.Breadcrumb[0] == "5 Standard NodeClasses"
                && s562.Breadcrumb[1] == "5.6 Variables",
            $"5.6.2 breadcrumb == [\"5 Standard NodeClasses\", \"5.6 Variables\"] (actual: [{crumb}])");

        Assert(s562.PageChunk.Contains("shall not be null", StringComparison.Ordinal),
            "5.6.2 page chunk contains \"shall not be null\"");

        Assert(s562.SectionPath == "/specs/OPC-10000-3/v1.05.06/5.6.2",
            $"5.6.2 section path == \"/specs/OPC-10000-3/v1.05.06/5.6.2\" (actual: \"{s562.SectionPath}\")");

        Assert(s562.SourceUrl == "https://reference.opcfoundation.org/specs/OPC-10000-3/v1.05.06/5.6.2",
            $"5.6.2 source url is well-formed (actual: \"{s562.SourceUrl}\")");

        if (slugMap != null)
        {
            Assert(s562.SectionId == "sec_5-6-2_variable-nodeclass",
                $"5.6.2 SectionId == \"sec_5-6-2_variable-nodeclass\" via STS slug (actual: \"{s562.SectionId}\")");
        }
        else
        {
            Assert(s562.SectionId == "n_5-6-2",
                $"5.6.2 SectionId == \"n_5-6-2\" (fallback) (actual: \"{s562.SectionId}\")");
        }
    }

    var s331 = chunks.FirstOrDefault(c => c.SectionNumber == "3.3.1");
    Assert(s331 != null, "Section 3.3.1 exists");
    if (s331 != null)
    {
        const string expectedSha = "8014926a8f9a58f706ff2536d9d67a2eeca85592677b97489fe9370db2382299";
        Assert(s331.Figures.Contains(expectedSha),
            $"3.3.1 Figures contains \"{expectedSha[..16]}...\" (actual: [{string.Join(", ", s331.Figures.Select(f => f[..Math.Min(16, f.Length)] + "..."))}])");
        Assert(s331.PageChunk.Contains("[Figure:", StringComparison.Ordinal),
            "3.3.1 page chunk contains a [Figure: ...] marker");
    }

    // Sanity: every chunk has a non-empty number and unique section id.
    var emptyNumber = chunks.Where(c => string.IsNullOrEmpty(c.SectionNumber)).ToList();
    Assert(emptyNumber.Count == 0,
        $"all chunks have non-empty SectionNumber (offenders: {emptyNumber.Count})");

    var dupes = chunks.GroupBy(c => c.SectionId).Where(g => g.Count() > 1).ToList();
    Assert(dupes.Count == 0,
        $"all chunk SectionIds are unique (duplicates: {dupes.Count})");

    if (failures.Count > 0)
    {
        Console.WriteLine($"\n  HTML parser test FAILED ({failures.Count} assertion(s) failed)");
        throw new InvalidOperationException(
            $"Spec HTML parser test failed: {string.Join("; ", failures)}");
    }

    Console.WriteLine("  HTML parser test PASSED ✓");
    Console.WriteLine(new string('═', 80));
}

// ═══════════════════════════════════════════════════════════════════════
// Companion-projection parser/filter test — local and deterministic.
// Exercises a full live-server-style export containing both standard Server
// infrastructure and a vendor compressor subtree.
// ═══════════════════════════════════════════════════════════════════════
static async Task RunMappingParserTestAsync()
{
    Console.WriteLine("Companion projection parser/filter test");
    Console.WriteLine(new string('═', 80));

    var path = TestDataPath("mapping-source.nodeset2.xml");
    await using var stream = File.OpenRead(path);
    var graph = await new AddressSpaceNodeSetReader().ReadAsync(stream);
    var filter = await new AddressSpaceCoreFilter().FilterAsync(graph);

    var failures = new List<string>();
    void Assert(bool condition, string description)
    {
        Console.WriteLine($"  {(condition ? "✓" : "✗")} {description}");
        if (!condition) failures.Add(description);
    }

    const string VendorNs = "urn:example:live-server";
    var compressorId = $"nsu={VendorNs};s=CompressorA";
    var pressureId = $"nsu={VendorNs};s=CompressorA.Pressure";
    var serverId = $"nsu={AddressSpaceNodeSetReader.UaNamespace};i=2253";

    Assert(graph.Nodes.ContainsKey(serverId), "input graph retains the exported standard Server object");
    Assert(graph.Nodes.ContainsKey(compressorId), "input graph contains the custom compressor object");
    Assert(graph.Nodes[compressorId].TypeDefinition ==
        $"nsu={VendorNs};s=VendorCompressorType",
        "custom HasTypeDefinition is canonicalized with a namespace URI");
    Assert(graph.Nodes[pressureId].AccessLevel == 3 && graph.Nodes[pressureId].UserAccessLevel == 3,
        "read/write AccessLevel metadata is retained for gateway direction inference");
    Assert(graph.Nodes[pressureId].BrowsePath.EndsWith(
        "/Plant/CompressorA/DischargePressure", StringComparison.Ordinal),
        $"hierarchical BrowsePath is reconstructed (actual: {graph.Nodes[pressureId].BrowsePath})");

    Assert(filter.ExcludedNodes.Any(n => n.NodeId == serverId),
        "standard Server infrastructure is excluded");
    Assert(filter.IncludedNodes.Any(n => n.NodeId == compressorId),
        "custom application object remains eligible for semantic projection");
    Assert(filter.IncludedNodes.Any(n => n.NodeId == pressureId),
        "custom application variable remains eligible for semantic projection");

    var stableA = CompanionTargetNodeId.Create(
        "urn:example:projection", compressorId,
        "nsu=urn:test:companion:pumps;i=1001", "$", 0);
    var stableB = CompanionTargetNodeId.Create(
        "urn:example:projection", compressorId,
        "nsu=urn:test:companion:pumps;i=1001", "$", 99);
    Assert(stableA == stableB,
        "target NodeId is content-derived and independent of projection ordering");
    Assert(stableA.StartsWith("nsu=urn:example:projection;s=projection-", StringComparison.Ordinal),
        $"target NodeId uses the output model namespace (actual: {stableA})");
    Assert(AddressSpaceNodeSetReader.CanonicalizeNodeId(
            "MandatoryPlaceholder", graph.NamespaceUris, graph.Aliases)
        .EndsWith(";i=11510", StringComparison.Ordinal),
        "MandatoryPlaceholder resolves to the OPC UA standard NodeId i=11510");
    Assert(AddressSpaceNodeSetReader.CanonicalizeNodeId(
            "OptionalPlaceholder", graph.NamespaceUris, graph.Aliases)
        .EndsWith(";i=11508", StringComparison.Ordinal),
        "OptionalPlaceholder resolves to the OPC UA standard NodeId i=11508");

    var versionRows = new[]
    {
        new NodeSetModelCatalogEntry
        {
            ModelUri = "urn:test:versioned",
            Version = "1.0.0",
            PublicationDate = "2025-01-01T00:00:00Z",
            SourceBlob = "nodesets/v1.xml",
            NamespaceUris = ["urn:test:versioned"],
            RequiredModels = [],
        },
        new NodeSetModelCatalogEntry
        {
            ModelUri = "urn:test:versioned",
            Version = "2.0.0",
            PublicationDate = "2026-01-01T00:00:00Z",
            SourceBlob = "nodesets/v2.xml",
            NamespaceUris = ["urn:test:versioned"],
            RequiredModels = [],
        },
    };
    var versionMetadata = NodeSetModelCatalogStore.BuildVersionMetadata(versionRows);
    Assert(versionMetadata[("urn:test:versioned", "2.0.0", "nodesets/v2.xml")]
        == (true, 1), "newest official model is marked latest with version_rank=1");
    Assert(versionMetadata[("urn:test:versioned", "1.0.0", "nodesets/v1.xml")]
        == (false, 2), "historical official model is not marked latest");

    if (failures.Count > 0)
        throw new InvalidOperationException(
            $"Companion projection parser/filter test failed: {string.Join("; ", failures)}");

    Console.WriteLine("  Companion projection parser/filter test PASSED ✓");
    Console.WriteLine(new string('═', 80));
}

static async Task RunMappingArtifactTestAsync()
{
    Console.WriteLine("Companion projection artifact test");
    Console.WriteLine(new string('═', 80));

    const string OutputModel = "urn:example:projection";
    const string SourceId = "nsu=urn:example:live-server;s=CompressorA";
    var generatedAt = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    var metadata = new CompanionProjectionModelMetadata
    {
        OutputModelUri = OutputModel,
        OutputVersion = "1.0.0",
        SourceName = "mapping-source.nodeset2.xml",
        SourceModelUris = ["urn:example:live-server"],
        RequiredModels =
        [
            new AddressSpaceRequiredModel
            {
                ModelUri = AddressSpaceNodeSetReader.UaNamespace,
                Version = "1.05.04",
                PublicationDate = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new AddressSpaceRequiredModel
            {
                ModelUri = "urn:test:companion:pumps",
                Version = "1.0.0",
                PublicationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new AddressSpaceRequiredModel
            {
                ModelUri = "urn:test:companion:machinery",
                Version = "1.0.0",
                PublicationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        ],
        RequiredModelUris =
        [
            AddressSpaceNodeSetReader.UaNamespace,
            "urn:test:companion:pumps",
            "urn:test:companion:machinery",
        ],
        GeneratedAt = generatedAt,
    };

    var source = new CompanionNodeIdentifier
    {
        ExpandedNodeId = SourceId,
        BrowseName = "CompressorA",
        BrowsePath = "/Plant/CompressorA",
        ModelUri = "urn:example:live-server",
    };
    var pumpType = new CompanionNodeIdentifier
    {
        ExpandedNodeId = "nsu=urn:test:companion:pumps;i=1001",
        BrowseName = "PumpType",
        ModelUri = "urn:test:companion:pumps",
        ModelVersion = "1.0.0",
        ModelPublicationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
    var machineType = new CompanionNodeIdentifier
    {
        ExpandedNodeId = "nsu=urn:test:companion:machinery;i=2001",
        BrowseName = "MachineType",
        ModelUri = "urn:test:companion:machinery",
        ModelVersion = "1.0.0",
        ModelPublicationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    CompanionProjectionDefinition Projection(
        int ordinal,
        CompanionNodeIdentifier targetType,
        string declarationName)
    {
        var rootId = CompanionTargetNodeId.Create(
            OutputModel, SourceId, targetType.ExpandedNodeId, "$", ordinal);
        var childPath = "$/" + declarationName;
        var childId = CompanionTargetNodeId.Create(
            OutputModel, SourceId, targetType.ExpandedNodeId, childPath, ordinal);
        return new CompanionProjectionDefinition
        {
            Ordinal = ordinal,
            SourceRoot = source,
            TargetType = targetType,
            Status = CompanionDecisionStatus.Accepted,
            Confidence = 0.94 - ordinal * 0.01,
            Mappings =
            [
                Mapping(
                    $"root-{ordinal}", source,
                    new CompanionNodeIdentifier
                    {
                        ExpandedNodeId = rootId,
                        BrowseName = targetType.BrowseName,
                        BrowsePath = "$",
                        ModelUri = OutputModel,
                    },
                    CompanionMappingKind.Object, CompanionMappingDirection.Read,
                    "$", "Object", targetType.ExpandedNodeId, null),
                Mapping(
                    $"child-{ordinal}", source,
                    new CompanionNodeIdentifier
                    {
                        ExpandedNodeId = childId,
                        BrowseName = declarationName,
                        BrowsePath = childPath,
                        ModelUri = OutputModel,
                    },
                    CompanionMappingKind.Variable, CompanionMappingDirection.ReadWrite,
                    childPath, "Variable", "nsu=http://opcfoundation.org/UA/;i=63",
                    "nsu=http://opcfoundation.org/UA/;i=11",
                    declarationName == "DischargePressure"
                        ? "nsu=http://opcfoundation.org/UA/;i=47"
                        : "nsu=http://opcfoundation.org/UA/;i=46",
                    targetType.ModelUri),
            ],
        };
    }

    var projections =
        new[]
        {
            Projection(0, pumpType, "DischargePressure"),
            Projection(1, machineType, "Manufacturer"),
        };
    var mapping = new CompanionMappingDocument
    {
        Model = metadata,
        Projections = projections,
    };
    var report = new CompanionProjectionReport
    {
        Model = metadata,
        SourceNodeCount = 8,
        IncludedNodeCount = 5,
        AcceptedProjectionCount = 2,
        ProposedProjectionCount = 0,
        UnresolvedProjectionCount = 0,
        Projections = projections,
    };
    var result = new CompanionProjectionEngineResult
    {
        Mapping = mapping,
        Report = report,
        Filter = new AddressSpaceFilterResult
        {
            IncludedNodes = [],
            PassThroughNodes = [],
            ExcludedNodes = [],
        },
    };

    var bundle = await new ModelMappingArtifactWriter().WriteAsync(result);
    var xml = Encoding.UTF8.GetString(bundle.ProjectionNodeSet.Content);
    var mappingJson = Encoding.UTF8.GetString(bundle.MappingJson.Content);
    var csv = Encoding.UTF8.GetString(bundle.MappingCsv.Content);
    var failures = new List<string>();
    void Assert(bool condition, string description)
    {
        Console.WriteLine($"  {(condition ? "✓" : "✗")} {description}");
        if (!condition) failures.Add(description);
    }

    var xdoc = System.Xml.Linq.XDocument.Parse(xml);
    var ns = System.Xml.Linq.XNamespace.Get(AddressSpaceNodeSetReader.NodeSetNamespace);
    var requiredModels = xdoc.Root!.Element(ns + "Models")!
        .Element(ns + "Model")!.Elements(ns + "RequiredModel").ToList();
    var headerOrder = xdoc.Root.Elements()
        .TakeWhile(element => element.Name.LocalName is "NamespaceUris" or "ServerUris" or "Models")
        .Select(element => element.Name.LocalName)
        .ToArray();
    Assert(Array.IndexOf(headerOrder, "NamespaceUris") < Array.IndexOf(headerOrder, "Models"),
        "UANodeSet writes NamespaceUris before Models as required by the XSD");
    Assert(requiredModels.Count == 3, "generated NodeSet declares all exact required models");
    Assert(requiredModels.Any(m =>
            (string?)m.Attribute("ModelUri") == "urn:test:companion:pumps"
            && (string?)m.Attribute("Version") == "1.0.0"
            && (string?)m.Attribute("PublicationDate") == "2026-01-01T00:00:00.0000000Z"),
        "RequiredModel retains version and publication date");
    Assert(xdoc.Root.Elements(ns + "UAObject").Count() >= 5,
        "NodeSet contains semantic root, two spec groups, and two projection instances");
    Assert(xdoc.Root.Elements(ns + "UAObject").Any(node =>
            (string?)node.Attribute("BrowseName") is string browseName
            && browseName.EndsWith(":Pumps", StringComparison.Ordinal)),
        "spec-group BrowseName is readable and contains no URI-derived underscores");
    var dischargePressure = xdoc.Root.Elements(ns + "UAVariable").Single(element =>
        ((string?)element.Attribute("BrowseName"))?.EndsWith(
            ":DischargePressure", StringComparison.Ordinal) == true);
    var dischargeNodeId = (string)dischargePressure.Attribute("NodeId")!;
    var dischargeParentId = (string)dischargePressure.Attribute("ParentNodeId")!;
    var namespaceUris = xdoc.Root.Element(ns + "NamespaceUris")!
        .Elements(ns + "Uri")
        .Select(element => element.Value)
        .ToList();
    var pumpsNamespaceIndex = namespaceUris.IndexOf("urn:test:companion:pumps") + 1;
    Assert((string?)dischargePressure.Attribute("BrowseName") ==
           $"{pumpsNamespaceIndex}:DischargePressure" &&
           dischargeNodeId.StartsWith("ns=1;", StringComparison.Ordinal),
        "DischargePressure BrowseName uses the pumps namespace while NodeId uses output namespace");
    var dischargeInverseReferences = dischargePressure.Element(ns + "References")!
        .Elements(ns + "Reference")
        .Where(reference => (string?)reference.Attribute("IsForward") == "false")
        .ToList();
    Assert(dischargeInverseReferences.Any(reference =>
            (string?)reference.Attribute("ReferenceType") == "i=47") &&
           dischargeInverseReferences.All(reference =>
            (string?)reference.Attribute("ReferenceType") != "i=46"),
        "DischargePressure inverse reference preserves exact HasComponent (i=47)");
    var dischargeParent = xdoc.Root.Elements(ns + "UAObject").Single(element =>
        (string?)element.Attribute("NodeId") == dischargeParentId);
    Assert(dischargeParent.Element(ns + "References")!.Elements(ns + "Reference").Any(reference =>
            reference.Attribute("IsForward") is null &&
            (string?)reference.Attribute("ReferenceType") == "i=47" &&
            reference.Value == dischargeNodeId),
        "DischargePressure parent forward reference preserves exact HasComponent (i=47)");
    Assert(mappingJson.Contains("\"projections\"", StringComparison.Ordinal)
        && mappingJson.Contains("urn:test:companion:pumps", StringComparison.Ordinal)
        && mappingJson.Contains("urn:test:companion:machinery", StringComparison.Ordinal),
        "mapping JSON preserves multiple companion-spec projections for one source node");
    Assert(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5,
        "mapping CSV contains a header plus four node mappings");

    using (var zipStream = new MemoryStream(bundle.Zip.Content, writable: false))
    using (var zip = new System.IO.Compression.ZipArchive(
               zipStream, System.IO.Compression.ZipArchiveMode.Read))
    {
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
        var expected = new[]
        {
            "mapping.csv", "mapping.json", "projection.nodeset2.xml",
            "report.json", "report.md",
        };
        Assert(names.SequenceEqual(expected),
            $"ZIP contains the exact five payload artifact names (actual: {string.Join(", ", names)})");
    }

    foreach (var artifact in bundle.Files)
    {
        Assert(artifact.Metadata.Length == artifact.Content.LongLength,
            $"{artifact.Metadata.Name} length metadata matches content");
        Assert(artifact.Metadata.Sha256.Length == 64,
            $"{artifact.Metadata.Name} has a SHA-256 digest");
    }

    if (failures.Count > 0)
        throw new InvalidOperationException(
            $"Companion projection artifact test failed: {string.Join("; ", failures)}");

    Console.WriteLine("  Companion projection artifact test PASSED ✓");
    Console.WriteLine(new string('═', 80));

    static CompanionMappingEntry Mapping(
        string id,
        CompanionNodeIdentifier source,
        CompanionNodeIdentifier target,
        CompanionMappingKind kind,
        CompanionMappingDirection direction,
        string declarationPath,
        string targetNodeClass,
        string? targetTypeDefinition,
        string? targetDataType,
        string? targetReferenceType = null,
        string? targetBrowseNameNamespaceUri = null) =>
        new()
        {
            MappingId = id,
            Source = source,
            Target = target,
            Kind = kind,
            Direction = direction,
            Status = CompanionDecisionStatus.Accepted,
            Confidence = 0.95,
            Evidence = new CompanionMappingEvidence
            {
                DeterministicScore = 0.9,
                SemanticScore = 1,
                Reasons = ["fixture"],
            },
            IsMandatory = true,
            DeclarationPath = declarationPath,
            TargetNodeClass = targetNodeClass,
            TargetTypeDefinition = targetTypeDefinition,
            TargetDataType = targetDataType,
            TargetReferenceType = targetReferenceType,
            TargetBrowseNameNamespaceUri = targetBrowseNameNamespaceUri,
        };
}

static async Task RunMappingEngineTestAsync()
{
    Console.WriteLine("Companion projection engine test");
    Console.WriteLine(new string('═', 80));

    await using var sourceStream = File.OpenRead(TestDataPath("mapping-source.nodeset2.xml"));
    var source = await new AddressSpaceNodeSetReader().ReadAsync(sourceStream);
    var repository = await FixtureCompanionModelRepository.CreateAsync(
        ("urn:test:companion:pumps", "mapping-target-pumps.nodeset2.xml"),
        ("urn:test:companion:machinery", "mapping-target-machinery.nodeset2.xml"));
    var search = new FixtureCompanionTypeSearch(
    [
        FixtureCandidate(
            "nsu=urn:test:companion:pumps;i=1001",
            "urn:test:companion:pumps",
            "PumpType",
            "Rotating equipment that moves a fluid and exposes pressure and temperature."),
        FixtureCandidate(
            "nsu=urn:test:companion:machinery;i=2001",
            "urn:test:companion:machinery",
            "MachineType",
            "Base semantic representation of a machine."),
    ]);
    var candidateService = new CompanionCandidateService(search, repository);
    var reasoner = new CompanionSemanticReasoner(
        new SelectAllCompanionChatClient(),
        new CompanionReasonerOptions
        {
            AcceptedThreshold = 0.30,
            ProposedThreshold = 0.10,
            MaxProjectionsPerSource = 3,
        });
    var engine = new CompanionProjectionEngine(
        new AddressSpaceCoreFilter(repository),
        candidateService,
        reasoner,
        repository);
    var result = await engine.ProjectAsync(source, new CompanionProjectionJobRequest
    {
        JobId = "fixture",
        OutputModelUri = "urn:example:engine-projection",
        SourceName = "mapping-source.nodeset2.xml",
        AcceptedThreshold = 0.30,
        ProposedThreshold = 0.10,
        MaxProjectionsPerSource = 3,
    });

    var compressorProjections = result.Mapping.Projections
        .Where(p => p.SourceRoot.ExpandedNodeId.EndsWith(";s=CompressorA", StringComparison.Ordinal)
                    && p.TargetType is not null)
        .ToList();
    var targetModels = compressorProjections
        .Select(p => p.TargetType!.ModelUri)
        .ToHashSet(StringComparer.Ordinal);
    var failures = new List<string>();
    void Assert(bool condition, string description)
    {
        Console.WriteLine($"  {(condition ? "✓" : "✗")} {description}");
        if (!condition) failures.Add(description);
    }

    Assert(compressorProjections.Count == 2,
        $"one source entity produces two unrelated projections (actual: {compressorProjections.Count})");
    Assert(targetModels.SetEquals(
        ["urn:test:companion:pumps", "urn:test:companion:machinery"]),
        $"projections target both fixture companion models (actual: {string.Join(", ", targetModels)})");
    Assert(compressorProjections.All(p => p.Mappings.Any(m => m.DeclarationPath == "$")),
        "every selected companion projection has a generated root mapping");
    Assert(compressorProjections.SelectMany(p => p.Mappings).Any(m =>
            m.DeclarationPath == "$/DischargePressure"
            && m.TargetReferenceType?.EndsWith(";i=47", StringComparison.Ordinal) == true),
        "exact Pump declaration enrichment survives candidate search into the mapping");
    Assert(result.Filter.ExcludedNodes.Any(n =>
            n.NodeId.EndsWith(";i=2253", StringComparison.Ordinal)),
        "Core Server infrastructure remains excluded in the end-to-end engine path");
    var requiredCore = result.Mapping.Model.RequiredModels.Single(m =>
        m.ModelUri == AddressSpaceNodeSetReader.UaNamespace);
    Assert(requiredCore.Version == "1.05.04",
        $"transitive exact Core model constraint is preserved (actual: {requiredCore.Version})");
    Assert(requiredCore.PublicationDate ==
        new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero),
        $"transitive exact Core publication date is preserved (actual: {requiredCore.PublicationDate:O})");

    if (failures.Count > 0)
        throw new InvalidOperationException(
            $"Companion projection engine test failed: {string.Join("; ", failures)}");

    Console.WriteLine("  Companion projection engine test PASSED ✓");
    Console.WriteLine(new string('═', 80));

    static CompanionSearchDocument FixtureCandidate(
        string nodeId,
        string modelUri,
        string browseName,
        string description) =>
        new(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["node_id"] = nodeId,
                ["model_uri"] = modelUri,
                ["model_version"] = "1.0.0",
                ["publication_date"] = new DateTimeOffset(
                    2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ["browse_name"] = browseName,
                ["description"] = description,
                ["source_blob"] = modelUri,
                ["parent_node_id"] = "",
                ["page_chunk"] = description,
            },
            10);
}

static string TestDataPath(string fileName)
{
    var path = Path.Combine(AppContext.BaseDirectory, "testdata", fileName);
    if (File.Exists(path)) return path;
    path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "testdata", fileName));
    return File.Exists(path)
        ? path
        : throw new FileNotFoundException($"Test snapshot '{fileName}' not found.", path);
}

sealed class FixtureCompanionTypeSearch(IReadOnlyList<CompanionSearchDocument> documents)
    : ICompanionTypeSearch
{
    public Task<IReadOnlyList<CompanionSearchDocument>> SearchAsync(
        string query,
        string filter,
        IReadOnlyList<string> select,
        int top,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "node_id", "model_uri", "model_version", "spec_version",
            "publication_date", "browse_name", "description", "source_blob",
            "parent_node_id", "page_chunk",
        };
        if (select.Any(field => !allowedFields.Contains(field)))
            throw new InvalidOperationException(
                $"Candidate search selected an undeployed field: {string.Join(", ", select.Where(f => !allowedFields.Contains(f)))}");
        return Task.FromResult<IReadOnlyList<CompanionSearchDocument>>(
            documents.Take(top).ToArray());
    }
}

sealed class SelectAllCompanionChatClient : ICompanionChatClient
{
    public bool Available => true;

    public Task<JsonNode?> CompleteAsync(
        object body,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = JsonSerializer.SerializeToNode(body)!;
        var userContent = request["messages"]![1]!["content"]!.GetValue<string>();
        var entities = JsonNode.Parse(userContent)!.AsArray();
        var decisions = new JsonArray();
        foreach (var entity in entities)
        {
            decisions.Add(new JsonObject
            {
                ["entity_id"] = entity!["entity_id"]!.GetValue<string>(),
                ["candidates"] = new JsonArray(
                    entity["candidates"]!.AsArray().Select(candidate =>
                        (JsonNode)new JsonObject
                        {
                            ["candidate_id"] = candidate!["candidate_id"]!.GetValue<string>(),
                            ["score"] = 1.0,
                            ["rationale"] = "Fixture selects each unrelated closed-set candidate.",
                        }).ToArray()),
            });
        }
        var content = new JsonObject { ["decisions"] = decisions }.ToJsonString();
        return Task.FromResult<JsonNode?>(new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = content },
                },
            },
        });
    }
}

sealed class FixtureCompanionModelRepository : ICompanionModelRepository
{
    readonly Dictionary<string, CompanionModelGraph> _models;

    FixtureCompanionModelRepository(Dictionary<string, CompanionModelGraph> models) =>
        _models = models;

    public static async Task<FixtureCompanionModelRepository> CreateAsync(
        params (string ModelUri, string FileName)[] models)
    {
        var result = new Dictionary<string, CompanionModelGraph>(StringComparer.Ordinal);
        foreach (var (modelUri, fileName) in models)
        {
            await using var stream = File.OpenRead(TestDataPathForType(fileName));
            var graph = await new AddressSpaceNodeSetReader().ReadAsync(stream);
            var hasSubtype = AddressSpaceNodeSetReader.CanonicalizeNodeId(
                "HasSubtype", graph.NamespaceUris, graph.Aliases);
            var supertypes = graph.Nodes.Values
                .Select(node => (
                    node.NodeId,
                    Supertype: node.References.FirstOrDefault(r =>
                        !r.IsForward && r.ReferenceType == hasSubtype)?.TargetNodeId))
                .Where(x => x.Supertype is not null)
                .ToDictionary(x => x.NodeId, x => x.Supertype!, StringComparer.Ordinal);
            result[modelUri] = new CompanionModelGraph
            {
                CatalogEntry = new CompanionModelCatalogEntry
                {
                    ModelUri = modelUri,
                    NamespaceUri = modelUri,
                    NamespaceUris = graph.NamespaceUris,
                    Version = "1.0.0",
                    PublicationDate = new DateTimeOffset(
                        2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    SourceBlob = fileName,
                    Source = "opcfoundation",
                    IsLatest = true,
                    IsOfficial = true,
                    RequiredModels = graph.Models.SelectMany(m => m.RequiredModels).ToArray(),
                },
                AddressSpace = graph,
                Supertypes = supertypes,
            };
        }
        return new FixtureCompanionModelRepository(result);
    }

    public Task<IReadOnlyList<CompanionModelCatalogEntry>> GetCatalogAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CompanionModelCatalogEntry>>(
            _models.Values.Select(m => m.CatalogEntry).ToArray());

    public Task<CompanionModelCatalogEntry?> ResolveAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        _models.TryGetValue(modelUri, out var model);
        return Task.FromResult(model?.CatalogEntry);
    }

    public Task<CompanionModelGraph> LoadModelAsync(
        string modelUri,
        string? version = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_models[modelUri]);

    public Task<IReadOnlyList<CompanionDeclaration>> ExpandDeclarationsAsync(
        string modelUri,
        string targetTypeExpandedNodeId,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var graph = _models[modelUri].AddressSpace;
        var parent = graph.Nodes[targetTypeExpandedNodeId];
        var declarations = new List<CompanionDeclaration>();
        foreach (var childId in parent.Children)
        {
            var child = graph.Nodes[childId];
            var forward = parent.References.First(r =>
                r.IsForward && r.TargetNodeId == childId);
            declarations.Add(new CompanionDeclaration
            {
                DeclarationPath = "$/" + child.BrowseName.Name,
                Node = child,
                DeclaringTypeNodeId = targetTypeExpandedNodeId,
                ReferenceType = forward.ReferenceType,
                IsMandatory = child.ModellingRule?.EndsWith(";i=78", StringComparison.Ordinal) == true,
                IsPlaceholder = false,
            });
        }
        return Task.FromResult<IReadOnlyList<CompanionDeclaration>>(declarations);
    }

    static string TestDataPathForType(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", fileName);
        if (File.Exists(path)) return path;
        path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "testdata", fileName));
        return path;
    }
}

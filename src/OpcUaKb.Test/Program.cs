using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

// ── Test selector ──────────────────────────────────────────────────────
// Usage:
//   dotnet run --project src/OpcUaKb.Test           → run all tests (KB test skipped if no SEARCH_API_KEY)
//   dotnet run --project src/OpcUaKb.Test -- sts    → run only the STS metadata parser test
//   dotnet run --project src/OpcUaKb.Test -- html   → run only the spec HTML parser test
//   dotnet run --project src/OpcUaKb.Test -- kb     → run only the live Knowledge Base test
var selector = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

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

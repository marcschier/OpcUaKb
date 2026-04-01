using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ── Configuration ──────────────────────────────────────────────────────
const string SearchEndpoint    = "https://opcua-kb-search.search.windows.net";
const string KnowledgeBaseName = "opcua-kb";
const string ApiVersion        = "2025-11-01-preview";

var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
    ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");

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

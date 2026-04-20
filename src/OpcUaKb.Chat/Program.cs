using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base Chatbot
// Interactive console chat grounded by the OPC UA KB MCP endpoint.
// ═══════════════════════════════════════════════════════════════════════

const string SearchEndpoint    = "https://opcua-kb-search.search.windows.net";
const string AoaiEndpoint      = "https://opcua-kb-foundry.openai.azure.com";
const string KnowledgeBaseName = "opcua-kb";
const string GptDeployment     = "gpt-4o";
const string ApiVersion        = "2025-11-01-preview";
const string AoaiApiVersion    = "2024-10-21";

var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
    ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");
TokenCredential aoaiCredential = new DefaultAzureCredential();

using var searchHttp = new HttpClient();
searchHttp.DefaultRequestHeaders.Add("api-key", searchApiKey);

using var aoaiHttp = new HttpClient();

var conversationHistory = new List<ChatMessage>();
var systemPrompt = """
    You are an OPC UA expert assistant. You answer questions about OPC UA technology
    using the OPC UA reference specifications as your knowledge base.
    
    When answering:
    - Cite specification part numbers and sections (e.g., "Part 4, Section 5.6.2")
    - Be technically precise — use correct OPC UA terminology
    - When asked for code, use C# with the OPC UA .NET Standard SDK
    - Format code in markdown code blocks
    - If the grounding data doesn't cover the question, say so
    
    You have access to grounding data from reference.opcfoundation.org covering
    all OPC UA specification parts, companion specs, and NodeSet definitions.
    """;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║       OPC UA Knowledge Base Chatbot                     ║");
Console.WriteLine("║  Grounded by reference.opcfoundation.org via AI Search  ║");
Console.WriteLine("║  Type 'quit' to exit, 'clear' to reset conversation     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("You > ");
    Console.ResetColor();

    var userInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(userInput)) continue;
    if (userInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
    if (userInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        conversationHistory.Clear();
        Console.WriteLine("  (conversation cleared)\n");
        continue;
    }

    conversationHistory.Add(new("user", userInput));

    try
    {
        // Step 1: Retrieve grounding data from the Knowledge Base
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  [Searching OPC UA specs...");
        var groundingData = await RetrieveGroundingAsync(userInput);
        Console.WriteLine($" {(groundingData != null ? "found" : "no results")}]");
        Console.ResetColor();

        // Step 2: Send to GPT-4o with grounding context
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  [Generating answer...");
        var answer = await ChatCompletionAsync(userInput, groundingData);
        Console.WriteLine(" done]");
        Console.ResetColor();

        conversationHistory.Add(new("assistant", answer));

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\nAssistant > ");
        Console.ResetColor();
        Console.WriteLine(answer);
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  Error: {ex.Message}\n");
        Console.ResetColor();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Retrieve grounding data from the OPC UA Knowledge Base
// ═══════════════════════════════════════════════════════════════════════
async Task<string?> RetrieveGroundingAsync(string query)
{
    // Build messages including recent conversation for context
    var messages = new List<object>();
    foreach (var msg in conversationHistory.TakeLast(6))
    {
        messages.Add(new
        {
            role = msg.Role,
            content = new[] { new { type = "text", text = msg.Content } }
        });
    }

    var body = new
    {
        messages,
        retrievalReasoningEffort = new { kind = "low" }
    };

    var response = await searchHttp.PostAsync(
        $"{SearchEndpoint}/knowledgebases/{KnowledgeBaseName}/retrieve?api-version={ApiVersion}",
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    if (!response.IsSuccessStatusCode) return null;

    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
    return (json?["response"]?[0]?["content"]?[0]?["text"])?.GetValue<string>();
}

// ═══════════════════════════════════════════════════════════════════════
// Chat completion with GPT-4o, injecting grounding data
// ═══════════════════════════════════════════════════════════════════════
async Task<string> ChatCompletionAsync(string userQuery, string? groundingData)
{
    var messages = new List<object>
    {
        new { role = "system", content = systemPrompt }
    };

    // Add conversation history (last few turns for context)
    foreach (var msg in conversationHistory.SkipLast(1).TakeLast(6))
    {
        messages.Add(new { role = msg.Role, content = msg.Content });
    }

    // Inject grounding data as a system message before the user's question
    if (!string.IsNullOrWhiteSpace(groundingData))
    {
        messages.Add(new
        {
            role = "system",
            content = $"Use the following OPC UA specification data to answer the user's question. " +
                      $"Cite [ref_id:N] references where applicable.\n\n{groundingData}"
        });
    }

    messages.Add(new { role = "user", content = userQuery });

    var body = new
    {
        messages,
        model = GptDeployment,
        temperature = 0.3,
        max_tokens = 2000
    };

    var token = await aoaiCredential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
        default);
    var req = new HttpRequestMessage(HttpMethod.Post,
        $"{AoaiEndpoint}/openai/deployments/{GptDeployment}/chat/completions?api-version={AoaiApiVersion}")
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var response = await aoaiHttp.SendAsync(req);

    response.EnsureSuccessStatusCode();
    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
    return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
        ?? "(no response)";
}

record ChatMessage(string Role, string Content);

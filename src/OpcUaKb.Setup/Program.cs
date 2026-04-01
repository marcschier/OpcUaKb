using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ── Configuration ──────────────────────────────────────────────────────
const string SearchEndpoint     = "https://opcua-kb-search.search.windows.net";
const string AoaiEndpoint       = "https://opcua-kb-openai.openai.azure.com";
const string GptDeployment      = "gpt-4o";
const string GptModel           = "gpt-4o";
const string KnowledgeSourceName = "opcua-web-ks";
const string KnowledgeBaseName   = "opcua-kb";
const string ApiVersion          = "2025-11-01-preview";

var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
    ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("api-key", searchApiKey);

// ── Step 1: Create Web Knowledge Source ────────────────────────────────
Console.WriteLine("Creating Web Knowledge Source...");

var ksBody = new
{
    name = KnowledgeSourceName,
    kind = "web",
    description = "OPC UA reference specifications from the OPC Foundation. " +
                  "Covers all OPC UA parts: address space, services, security, " +
                  "information model, profiles, data access, alarms, pub/sub, " +
                  "companion specs, and NodeSets.",
    webParameters = new
    {
        domains = new
        {
            allowedDomains = new[] { new { address = "reference.opcfoundation.org", includeSubpages = true } }
        }
    }
};

var ksResponse = await http.PutAsync(
    $"{SearchEndpoint}/knowledgesources/{KnowledgeSourceName}?api-version={ApiVersion}",
    new StringContent(JsonSerializer.Serialize(ksBody), Encoding.UTF8, "application/json"));
ksResponse.EnsureSuccessStatusCode();
Console.WriteLine($"  ✓ Knowledge source '{KnowledgeSourceName}' created.");

// ── Step 2: Create Knowledge Base ──────────────────────────────────────
Console.WriteLine("Creating Knowledge Base...");

var kbBody = new
{
    name = KnowledgeBaseName,
    description = "OPC UA knowledge base for answering questions about OPC UA specifications, " +
                  "generating test code for OPC UA stack implementations, and looking up " +
                  "NodeSet definitions, services, data types, and security models.",
    knowledgeSources = new[] { new { name = KnowledgeSourceName } },
    models = new[]
    {
        new
        {
            kind = "azureOpenAI",
            azureOpenAIParameters = new { resourceUri = AoaiEndpoint, deploymentId = GptDeployment, modelName = GptModel }
        }
    },
    retrievalReasoningEffort = new { kind = "low" },
    outputMode = "answerSynthesis",
    retrievalInstructions =
        "Use the OPC UA web knowledge source to answer questions about OPC UA specifications. " +
        "This source covers all OPC 10000 specification parts (1-26+), companion specifications, " +
        "and NodeSet definitions. When the user asks about specific data types, services, " +
        "node classes, or information models, search for the relevant specification part. " +
        "When generating test code, reference the exact service calls and data types from the specs.",
    answerInstructions =
        "Provide technically precise answers grounded in the OPC UA specifications. " +
        "Include specification part numbers and section references when available. " +
        "When generating code, use the OPC UA .NET Standard SDK conventions. " +
        "For protocol-level questions, cite the exact service names and parameter structures. " +
        "Format code blocks with C# syntax."
};

var kbResponse = await http.PutAsync(
    $"{SearchEndpoint}/knowledgebases/{KnowledgeBaseName}?api-version={ApiVersion}",
    new StringContent(JsonSerializer.Serialize(kbBody), Encoding.UTF8, "application/json"));
kbResponse.EnsureSuccessStatusCode();
Console.WriteLine($"  ✓ Knowledge base '{KnowledgeBaseName}' created.");

// ── Step 3: Display MCP endpoint ───────────────────────────────────────
var mcpEndpoint = $"{SearchEndpoint}/knowledgebases/{KnowledgeBaseName}/mcp?api-version={ApiVersion}";
Console.WriteLine();
Console.WriteLine($"MCP Endpoint: {mcpEndpoint}");
Console.WriteLine();

// ── Step 4: Verify with a test query ───────────────────────────────────
Console.WriteLine("Sending test query...");

var retrieveBody = new
{
    messages = new[]
    {
        new { role = "user", content = new[] { new { type = "text", text = "What are the main OPC UA service sets defined in Part 4?" } } }
    },
    retrievalReasoningEffort = new { kind = "low" }
};

var retrieveResponse = await http.PostAsync(
    $"{SearchEndpoint}/knowledgebases/{KnowledgeBaseName}/retrieve?api-version={ApiVersion}",
    new StringContent(JsonSerializer.Serialize(retrieveBody), Encoding.UTF8, "application/json"));
retrieveResponse.EnsureSuccessStatusCode();

var json = await retrieveResponse.Content.ReadAsStringAsync();
var doc = JsonNode.Parse(json)!;
var responseText = doc["response"]?[0]?["content"]?[0]?["text"]?.GetValue<string>();
Console.WriteLine("Response:");
Console.WriteLine(responseText);
Console.WriteLine();
Console.WriteLine("── Setup complete ─────────────────────────────────────────────────");

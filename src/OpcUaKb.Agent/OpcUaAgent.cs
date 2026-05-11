using System.Text.Json.Nodes;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace OpcUaKb.Agent;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert Agent — Microsoft 365 Agents SDK custom engine agent.
// Routes incoming Bot Framework activities to a tool-using GPT-4o loop
// over the OPC UA Knowledge Base. The agent itself does not call KB
// retrieve directly; instead it advertises every [McpServerTool]-tagged
// method in OpcUaKb.Core to GPT-4o and resolves each tool_calls request
// via AgentToolDispatcher. GPT can choose structured tools (search_nodes,
// get_type_hierarchy, list_specs, …) or fall back to the RAG tool
// (search_docs_rag) which wraps KbService.AskAsync.
// ═══════════════════════════════════════════════════════════════════════

public sealed class OpcUaAgent : AgentApplication
{
    const int MaxIterations = 8;

    const string WelcomeText =
        "👋 Welcome to **OPC UA Expert**! I'm your assistant for OPC UA specifications, " +
        "NodeSets, and companion specs. Ask me anything about Part 3, Part 9 alarms, " +
        "Pumps, Machinery, DI, or compliance. Type `/help` for examples.";

    const string HelpText =
        "**OPC UA Expert — example questions**\n\n" +
        "• What is the difference between an ObjectType and a VariableType?\n" +
        "• Summarize Part 9 alarm states and their transitions\n" +
        "• What members does the PumpType in the Pumps companion spec define?\n" +
        "• Which Part 3 service sets are required for compliance level Standard?\n" +
        "• Show the supertype chain for AnalogUnitType\n" +
        "• How does the Machinery companion spec model identification?\n\n" +
        "Just type your question and I'll search the OPC UA reference specs for you.";

    const string SystemPrompt =
        "You are OPC UA Expert. Use the available tools to answer questions about " +
        "OPC UA specifications, NodeSets, type hierarchies, compliance, and companion " +
        "specs. Prefer structured tools (search_nodes, get_type_hierarchy, count_nodes, " +
        "list_specs, compare_versions, get_spec_summary, etc.) over free-form retrieval. " +
        "Use search_docs or search_docs_rag for free-form spec text questions. " +
        "Cite specification part numbers and sections. Be technically precise.";

    readonly AgentToolDispatcher _dispatcher;
    readonly AoaiChatClient _aoai;
    readonly ILogger<OpcUaAgent> _logger;

    public OpcUaAgent(
        AgentApplicationOptions options,
        KbService kbService,
        AgentToolDispatcher dispatcher,
        AoaiChatClient aoai,
        ILogger<OpcUaAgent> logger)
        : base(options)
    {
        _ = kbService; // injected so DI fails fast if SEARCH_*/AOAI_ env vars are missing; tools use it via dispatcher
        _dispatcher = dispatcher;
        _aoai = aoai;
        _logger = logger;

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, OnMembersAddedAsync);
        OnMessage("/help", OnHelpAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    async Task OnMembersAddedAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(WelcomeText), cancellationToken);
            }
        }
    }

    async Task OnHelpAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await turnContext.SendActivityAsync(MessageFactory.Text(HelpText), cancellationToken);
    }

    async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var query = turnContext.Activity.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (!_aoai.Available)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "⚠️ The knowledge base is not configured. Set `SEARCH_ENDPOINT`, `SEARCH_API_KEY`, " +
                "and `AOAI_ENDPOINT` environment variables on the agent host."),
                cancellationToken);
            return;
        }

        // Surface "thinking" feedback while the tool-using loop runs.
        await turnContext.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, cancellationToken);

        try
        {
            _logger.LogInformation("[AGENT] Query=\"{Query}\"", query);
            var answer = await RunAgentLoopAsync(query, cancellationToken);
            await turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AGENT] Error answering query");
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"❌ Sorry, I hit an error while answering: {ex.Message}"),
                cancellationToken);
        }
    }

    async Task<string> RunAgentLoopAsync(string query, CancellationToken cancellationToken)
    {
        // messages is a List<object> because we mix anonymous objects (system/user/tool messages)
        // with JsonNode clones of the assistant's tool_calls turn. System.Text.Json serializes
        // both correctly through the runtime type of each element.
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = query },
        };

        var tools = _dispatcher.GetToolDefinitions();

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            _logger.LogDebug("[AGENT] Iteration={Iter} MessageCount={Count}", iter, messages.Count);

            var body = new
            {
                messages,
                model = _aoai.Deployment,
                temperature = 0.3,
                tools,
                tool_choice = "auto",
                max_tokens = 2000,
            };

            var response = await _aoai.ChatCompletionAsync(body, cancellationToken);
            var msg = response?["choices"]?[0]?["message"];
            if (msg == null)
            {
                _logger.LogWarning("[AGENT] No message in AOAI response on iteration {Iter}", iter);
                break;
            }

            var toolCalls = msg["tool_calls"];
            if (toolCalls is JsonArray arr && arr.Count > 0)
            {
                // Echo the assistant turn (with its tool_calls) back into the conversation so
                // the follow-up tool messages have something to anchor against.
                messages.Add(msg.DeepClone());

                foreach (var call in arr)
                {
                    var id = call?["id"]?.GetValue<string>() ?? "";
                    var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                    var args = call?["function"]?["arguments"]?.ToString() ?? "{}";

                    _logger.LogInformation("[AGENT] Tool={Name} Args={Args}", name, args);

                    string result;
                    try
                    {
                        result = await _dispatcher.InvokeAsync(name, args, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AGENT] Tool={Name} failed", name);
                        result = $"Error invoking tool '{name}': {ex.Message}";
                    }

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = result,
                    });
                }
                continue;
            }

            var content = msg["content"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(content))
                return content!;

            _logger.LogWarning("[AGENT] Empty content with no tool calls on iteration {Iter}", iter);
            break;
        }

        _logger.LogWarning("[AGENT] Iteration cap ({Cap}) reached without final answer", MaxIterations);
        return "I'm having trouble reaching an answer; please try rephrasing your question.";
    }
}

using System.Buffers;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Renders one <c>/v1/chat/completions</c> request body.
/// </summary>
/// <remarks>
/// The sibling of <see cref="AnthropicRequestWriter"/>, and the reason there are two: the same
/// conversation renders differently here. The system prompt heads the message list, tool arguments
/// are a JSON-encoded string rather than an object, and one <see cref="AssistantMessage.ToolResults"/>
/// becomes one <c>role: "tool"</c> message per result. That last rule is the exact inverse of
/// Anthropic's, so it belongs to the writer — never to the conversation, which stays one logical
/// message carrying a list.
/// </remarks>
internal static class OpenAiRequestWriter
{
    public static byte[] Write(AssistantTurn turn, IReadOnlyList<IAssistantTool> tools, AssistantConnection connection)
    {
        var model = connection.Capabilities(turn.Tier);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ToolJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", connection.ModelFor(turn.Tier));
            writer.WriteNumber(
                model.UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens",
                connection.MaxTokensFor(turn));
            writer.WriteBoolean("stream", true);
            WriteTools(writer, tools);
            WriteToolReasoningEffort(writer, tools, model);
            WriteMessages(writer, turn);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<IAssistantTool> tools)
    {
        if (tools.Count == 0)
            return;

        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in ordered)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WritePropertyName("function");
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            writer.WriteRawValue(tool.JsonSchema);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Saying nothing is not the safe option it looks like: OpenAI's models reason by default and
    // then refuse a request that also carries function tools, so a toolset — which is every turn
    // here — 400s unless the body opts out by name. Models that take reasoning alongside tools
    // declare no effort and keep whatever they do on their own.
    private static void WriteToolReasoningEffort(
        Utf8JsonWriter writer,
        IReadOnlyList<IAssistantTool> tools,
        AssistantModel model)
    {
        if (tools.Count > 0 && model.ToolReasoningEffort is { } effort)
            writer.WriteString("reasoning_effort", effort);
    }

    private static void WriteMessages(Utf8JsonWriter writer, AssistantTurn turn)
    {
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        WriteTextMessage(writer, "system", turn.SystemPrompt);
        foreach (var message in turn.Messages)
        {
            switch (message)
            {
                case AssistantMessage.User user:
                    WriteTextMessage(writer, "user", user.Text);
                    break;
                // There is no mid-conversation system entry here, so live repo state rides in the
                // user turn — the same fallback the quick tier already takes.
                case AssistantMessage.RepoContext context:
                    WriteTextMessage(writer, "user", context.Text);
                    break;
                case AssistantMessage.Assistant assistant:
                    WriteAssistantMessage(writer, assistant);
                    break;
                case AssistantMessage.ToolResults results:
                    WriteToolResults(writer, results);
                    break;
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteTextMessage(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text);
        writer.WriteEndObject();
    }

    private static void WriteAssistantMessage(Utf8JsonWriter writer, AssistantMessage.Assistant assistant)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        var text = string.Concat(assistant.Content.OfType<AssistantContent.Text>().Select(t => t.Value));
        if (text.Length > 0)
            writer.WriteString("content", text);
        else
            writer.WriteNull("content");

        var calls = assistant.Content.OfType<AssistantContent.ToolUse>().ToArray();
        if (calls.Length > 0)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in calls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", Arguments(call.Input));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    // One message per result, each naming the call it answers. There is no is_error flag on the
    // wire, so a failure says so in the content the model reads.
    private static void WriteToolResults(Utf8JsonWriter writer, AssistantMessage.ToolResults results)
    {
        foreach (var result in results.Results)
        {
            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            writer.WriteString("tool_call_id", result.ToolUseId);
            writer.WriteString("content", result.IsError ? "Error: " + result.Content : result.Content);
            writer.WriteEndObject();
        }
    }

    private static string Arguments(JsonElement input) =>
        input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
}

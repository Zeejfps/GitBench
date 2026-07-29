using System.Buffers;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Renders one Messages API request body.
/// </summary>
/// <remarks>
/// Written by hand rather than serialized from a DTO graph so the bytes are deterministic: the
/// tool list is ordinal-sorted and every block is emitted in a fixed order, which is what keeps
/// the cached tools + system prefix intact from one turn to the next.
/// </remarks>
internal static class AnthropicRequestWriter
{
    public static byte[] Write(AssistantTurn turn, IReadOnlyList<IAssistantTool> tools, AssistantConnection connection)
    {
        var model = connection.Capabilities(turn.Tier);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ToolJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", connection.ModelFor(turn.Tier));
            writer.WriteNumber("max_tokens", connection.MaxTokensFor(turn));
            writer.WriteBoolean("stream", true);
            if (model.ServerSideFallbacks)
                writer.WriteString("fallbacks", "default");
            WriteTools(writer, tools);
            WriteSystem(writer, turn.SystemPrompt);
            WriteMessages(writer, turn, model);
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
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("input_schema");
            writer.WriteRawValue(tool.JsonSchema);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // The breakpoint sits on the last system block, so tools and system cache together.
    private static void WriteSystem(Utf8JsonWriter writer, string systemPrompt)
    {
        writer.WritePropertyName("system");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", systemPrompt);
        writer.WritePropertyName("cache_control");
        writer.WriteStartObject();
        writer.WriteString("type", "ephemeral");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter writer, AssistantTurn turn, AssistantModel model)
    {
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in turn.Messages)
        {
            switch (message)
            {
                case AssistantMessage.User user:
                    WriteTextMessage(writer, "user", user.Text);
                    break;
                case AssistantMessage.RepoContext context when model.MidConversationSystem:
                    writer.WriteStartObject();
                    writer.WriteString("role", "system");
                    writer.WriteString("content", context.Text);
                    writer.WriteEndObject();
                    break;
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
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAssistantMessage(Utf8JsonWriter writer, AssistantMessage.Assistant assistant)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (var block in assistant.Content)
        {
            writer.WriteStartObject();
            switch (block)
            {
                case AssistantContent.Text text:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Value);
                    break;
                case AssistantContent.ToolUse use:
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", use.Id);
                    writer.WriteString("name", use.Name);
                    writer.WritePropertyName("input");
                    if (use.Input.ValueKind == JsonValueKind.Undefined)
                        writer.WriteRawValue("{}");
                    else
                        use.Input.WriteTo(writer);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Every result from one assistant message travels in a single user message — splitting them
    // across messages trains the model out of calling tools in parallel.
    private static void WriteToolResults(Utf8JsonWriter writer, AssistantMessage.ToolResults results)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (var result in results.Results)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "tool_result");
            writer.WriteString("tool_use_id", result.ToolUseId);
            writer.WriteString("content", result.Content);
            if (result.IsError)
                writer.WriteBoolean("is_error", true);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The client half of Streamable HTTP, enough to drive one session against an in-process MCP
/// server: initialize (capturing <c>Mcp-Session-Id</c>), then requests, notifications and the
/// terminating DELETE. A JSON-RPC error fails the test with the server's message.
/// </summary>
internal sealed class McpTestClient : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http = new() { Timeout = Timeout };
    private readonly Uri _endpoint;
    private int _nextId = 1;

    public McpTestClient(Uri endpoint) => _endpoint = endpoint;

    public string? SessionId { get; private set; }

    /// <summary>A port nothing is listening on right now.</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task<JsonElement> Initialize()
    {
        var id = _nextId++;
        using var response = await Send(RequestBody(id, "initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "test", version = "1" },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
        var result = Result(await response.Content.ReadAsStringAsync());
        await Notify("notifications/initialized", new { });
        return result;
    }

    public async Task<IReadOnlyList<JsonElement>> ListTools()
    {
        var result = await Request("tools/list", new { });
        return result.GetProperty("tools").EnumerateArray().ToList();
    }

    public Task<JsonElement> Call(string tool, object arguments) =>
        Request("tools/call", new { name = tool, arguments });

    public Task<JsonElement> GetPrompt(string name, object arguments) =>
        Request("prompts/get", new { name, arguments });

    public async Task<JsonElement> Request(string method, object @params)
    {
        var id = _nextId++;
        using var response = await Send(RequestBody(id, method, @params));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Result(await response.Content.ReadAsStringAsync());
    }

    public async Task Notify(string method, object @params)
    {
        using var response = await Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>Posts a bare initialize and answers with the status alone — for an endpoint that
    /// should not exist.</summary>
    public async Task<HttpStatusCode> ProbeStatus()
    {
        using var response = await Send(RequestBody(1, "initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "test", version = "1" },
        }));
        return response.StatusCode;
    }

    public async Task Delete()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
        AddSessionHeaders(request);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The text of a tool result's first content block.</summary>
    public static string TextOf(JsonElement result) =>
        result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;

    public static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;

    private static string RequestBody(int id, string method, object @params) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });

    private Task<HttpResponseMessage> Send(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddSessionHeaders(request);
        return _http.SendAsync(request);
    }

    private void AddSessionHeaders(HttpRequestMessage request)
    {
        if (SessionId is null) return;
        request.Headers.Add("Mcp-Session-Id", SessionId);
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
    }

    private static JsonElement Result(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (root.TryGetProperty("error", out var error))
            Assert.Fail($"JSON-RPC error: {error}");
        return root.GetProperty("result");
    }

    public void Dispose() => _http.Dispose();
}

using Xunit;

namespace GitBench.Lsp.Tests;

// Diagnostics are the one notification this client models, and they arrive in waves for minutes after
// a file is opened. These pin the parse: which document and which version the wave is about, and an
// empty list read as "no errors now" rather than as nothing at all.
public sealed class DiagnosticsNotificationTests
{
    private static async Task<ServerNotification.Diagnostics> Published(string paramsJson)
    {
        await using var fx = new LspFixture();
        await fx.Server.Notify(LspMethod.PublishDiagnostics, paramsJson);
        return Assert.IsType<ServerNotification.Diagnostics>(await fx.Client.NextNotification());
    }

    [Fact]
    public async Task A_wave_names_the_document_and_its_version()
    {
        var published = await Published("""{"uri":"file:///repo/src/lib.rs","version":7,"diagnostics":[]}""");

        Assert.Equal("file:///repo/src/lib.rs", published.Uri.Value);
        Assert.Equal(new DocumentVersion(7), published.Version);
    }

    [Fact]
    public async Task A_wave_from_a_server_that_does_not_version_them_has_no_version()
    {
        var published = await Published("""{"uri":"file:///repo/src/lib.rs","diagnostics":[]}""");

        Assert.Null(published.Version);
    }

    [Fact]
    public async Task An_empty_wave_is_a_message_saying_the_file_is_clean()
    {
        var published = await Published("""{"uri":"file:///repo/src/lib.rs","diagnostics":[]}""");

        Assert.Empty(published.Items);
    }

    [Fact]
    public async Task Each_diagnostic_carries_its_range_message_source_and_code()
    {
        var published = await Published(
            """
            {"uri":"file:///repo/src/lib.rs","diagnostics":[
              {"range":{"start":{"line":2,"character":4},"end":{"line":2,"character":9}},
               "severity":1,"message":"cannot find value `x`","source":"rustc","code":"E0425"}]}
            """);

        var item = Assert.Single(published.Items);
        Assert.Equal(new LspLine(2), item.Range.Start.Line);
        Assert.Equal("cannot find value `x`", item.Message);
        Assert.Equal("rustc", item.Source);
        Assert.Equal("E0425", item.Code);
        Assert.Equal(DiagnosticSeverity.Error, item.Severity);
    }

    [Fact]
    public async Task A_numeric_code_is_kept_rather_than_dropped()
    {
        var published = await Published(
            """
            {"uri":"file:///a.rs","diagnostics":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m","code":2304}]}
            """);

        Assert.Equal("2304", Assert.Single(published.Items).Code);
    }

    [Theory]
    [InlineData("\"severity\":1,", DiagnosticSeverity.Error)]
    [InlineData("\"severity\":2,", DiagnosticSeverity.Warning)]
    [InlineData("\"severity\":3,", DiagnosticSeverity.Information)]
    [InlineData("\"severity\":4,", DiagnosticSeverity.Hint)]
    [InlineData("", DiagnosticSeverity.Unspecified)]
    [InlineData("\"severity\":9,", DiagnosticSeverity.Unspecified)]
    public async Task Severity_the_client_does_not_recognise_is_unspecified_rather_than_an_error(
        string severityField, DiagnosticSeverity expected)
    {
        var published = await Published(
            "{\"uri\":\"file:///a.rs\",\"diagnostics\":[{" + severityField +
            "\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}," +
            "\"message\":\"m\"}]}");

        Assert.Equal(expected, Assert.Single(published.Items).Severity);
    }

    [Fact]
    public async Task Diagnostics_keep_the_order_the_server_sent_them_in()
    {
        var published = await Published(
            """
            {"uri":"file:///a.rs","diagnostics":[
              {"range":{"start":{"line":9,"character":0},"end":{"line":9,"character":1}},"message":"later"},
              {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"message":"earlier"}]}
            """);

        Assert.Equal(["later", "earlier"], published.Items.Select(item => item.Message));
    }
}

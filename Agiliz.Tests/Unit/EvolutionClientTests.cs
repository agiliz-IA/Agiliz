using Agiliz.Core.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class EvolutionClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    private EvolutionClient CreateClient(bool nativeButtons = false) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Evolution:ApiUrl"] = _server.Url,
            ["Evolution:ApiToken"] = "token-teste",
            ["Evolution:UseNativeButtons"] = nativeButtons.ToString()
        }).Build());

    [Fact]
    public async Task SendAsync_UsesV2RouteApikeyHeaderAndTopLevelText()
    {
        _server.Given(Request.Create().WithPath("/message/sendText/5521900000000").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201));

        await CreateClient().SendAsync("5521911111111@s.whatsapp.net", "5521900000000", "Olá!");

        var request = _server.LogEntries.Should().ContainSingle().Subject.RequestMessage;
        request.Headers!["apikey"].Should().ContainSingle().Which.Should().Be("token-teste");
        request.Body.Should().Contain("\"number\":\"5521911111111\"").And.Contain("\"text\":\"Ol");
        request.Body.Should().NotContain("textMessage");
    }

    [Fact]
    public async Task SendButtonsAsync_ByDefault_SendsNumberedOptionsAsText()
    {
        _server.Given(Request.Create().WithPath("/message/sendText/5521900000000").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201));

        await CreateClient().SendButtonsAsync("5521911111111", "5521900000000", "Confirma a consulta?", ["Confirmar", "Remarcar"]);

        var body = _server.LogEntries.Should().ContainSingle().Subject.RequestMessage.Body;
        body.Should().Contain("1 - Confirmar").And.Contain("2 - Remarcar");
    }

    [Fact]
    public async Task SendButtonsAsync_WhenNativeButtonsFail_FallsBackToText()
    {
        _server.Given(Request.Create().WithPath("/message/sendButtons/5521900000000").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400));
        _server.Given(Request.Create().WithPath("/message/sendText/5521900000000").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201));

        await CreateClient(nativeButtons: true).SendButtonsAsync("5521911111111", "5521900000000", "Confirma?", ["Sim"]);

        _server.LogEntries.Select(e => e.RequestMessage.Path)
            .Should().Equal("/message/sendButtons/5521900000000", "/message/sendText/5521900000000");
    }

    [Fact]
    public async Task DownloadMediaAsync_DecodesBase64()
    {
        var bytes = new byte[] { 1, 2, 3 };
        _server.Given(Request.Create().WithPath("/chat/getBase64FromMediaMessage/5521900000000").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201)
                   .WithBodyAsJson(new { base64 = Convert.ToBase64String(bytes), mimetype = "audio/ogg; codecs=opus" }));

        var media = await CreateClient().DownloadMediaAsync("5521900000000", "MSGID");

        media.Should().NotBeNull();
        media!.Data.Should().Equal(bytes);
        media.MimeType.Should().StartWith("audio/ogg");
    }

    [Theory]
    [InlineData(200, """{ "instance": { "instanceName": "5521900000000", "state": "open" } }""", true, "conectado")]
    [InlineData(200, """{ "instance": { "instanceName": "5521900000000", "state": "close" } }""", false, "QR code")]
    [InlineData(404, """{ "error": "not found" }""", false, "não existe")]
    [InlineData(401, "{}", false, "token")]
    public async Task CheckConnectionAsync_TranslatesInstanceState(int status, string body, bool ok, string expected)
    {
        _server.Given(Request.Create().WithPath("/instance/connectionState/5521900000000").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        var result = await CreateClient().CheckConnectionAsync("5521900000000");

        result.Ok.Should().Be(ok);
        result.Message.Should().Contain(expected);
    }

    public void Dispose() => _server.Stop();
}

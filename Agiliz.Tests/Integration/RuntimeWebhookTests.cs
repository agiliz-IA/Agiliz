using System.Net;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.LLM;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Tests.Fakes;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agiliz.Tests.Integration;

public sealed class RuntimeWebhookTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly FakeMessageProvider _fakeProvider = new();
    private readonly FakeLlmClient _fakeLlm = new();

    public RuntimeWebhookTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-anthropic-key");
    }

    private WebApplicationFactory<Runtime.Program> BuildFactory()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(
            tenantId: "test-bot",
            whatsappNumber: "15005550006"
        ).WithFlows(("oi", "Ola! Como posso ajudar?")));

        return new WebApplicationFactory<Runtime.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConfigsDir", _dir.Path);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IMessageProvider>(_fakeProvider);
                    // Provide a fake LLM factory so TenantRegistry never calls real APIs
                    services.AddSingleton<Func<BotConfig, ILlmClient>>(_ => _ => _fakeLlm);
                });
            });
    }

    private static StringContent CreateEvolutionPayload(string instanceName, string remoteJid, string message)
    {
        var payload = new
        {
            @event = "messages.upsert",
            instance = instanceName,
            data = new
            {
                message = new
                {
                    key = new
                    {
                        remoteJid = remoteJid,
                        id = Guid.NewGuid().ToString()
                    },
                    conversation = message,
                    fromMe = false,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            }
        };

        return new StringContent(
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json");
    }

    [Fact]
    public async Task Health_ReturnsOkWithBotCount()
    {
        using var factory = BuildFactory();
        var response = await factory.CreateClient().GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_WithMissingFields_ReturnsBadRequest()
    {
        using var factory = BuildFactory();
        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await factory.CreateClient().PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fakeProvider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_WithUnknownWhatsAppNumber_ReturnsOkButDoesNotSend()
    {
        using var factory = BuildFactory();
        var payload = CreateEvolutionPayload(
            instanceName: "99999",
            remoteJid: "5521999@s.whatsapp.net",
            message: "oi");

        var response = await factory.CreateClient().PostAsync("/webhook", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _fakeProvider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_FlowMatch_SendsFlowResponseWithoutLlm()
    {
        using var factory = BuildFactory();
        var payload = CreateEvolutionPayload(
            instanceName: "15005550006",
            remoteJid: "5521999@s.whatsapp.net",
            message: "oi tudo bem");

        var response = await factory.CreateClient().PostAsync("/webhook", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _fakeProvider.Sent.Should().ContainSingle(m =>
            m.To == "5521999" &&
            m.From == "15005550006" &&
            m.Body == "Ola! Como posso ajudar?");
    }

    [Fact]
    public async Task Webhook_SetsCorrectToAndFromWhenReplying()
    {
        using var factory = BuildFactory();
        var payload = CreateEvolutionPayload(
            instanceName: "15005550006",
            remoteJid: "5521123456789@s.whatsapp.net",
            message: "oi");

        await factory.CreateClient().PostAsync("/webhook", payload);
        _fakeProvider.Sent.Should().ContainSingle();
        var sent = _fakeProvider.Sent[0];
        sent.To.Should().Be("5521123456789");
        sent.From.Should().Be("15005550006");
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    }
}

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
    private readonly FakeCostRecorder _costs = new();

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
                    services.AddSingleton<Agiliz.Core.Billing.ICostRecorder>(_costs);
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

    // ─── Formato real da Evolution v2 (key em data.key) ──────────────────────

    private static StringContent V2Payload(string instance, string remoteJid, object message, bool fromMe = false, string id = "MSG1")
    {
        var payload = new
        {
            @event = "messages.upsert",
            instance,
            data = new
            {
                key = new { remoteJid, fromMe, id },
                pushName = "Cliente",
                messageType = "conversation",
                message
            }
        };
        return new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Webhook_V2Payload_RepliesToSender()
    {
        using var factory = BuildFactory();
        var payload = V2Payload("15005550006", "5521988887777@s.whatsapp.net", new { conversation = "oi" });

        await factory.CreateClient().PostAsync("/webhook", payload);

        _fakeProvider.Sent.Should().ContainSingle(m => m.To == "5521988887777" && m.Body == "Ola! Como posso ajudar?");
    }

    [Fact]
    public async Task Webhook_V2ExtendedTextMessage_IsRead()
    {
        using var factory = BuildFactory();
        var payload = V2Payload("15005550006", "5521988887777@s.whatsapp.net",
            new { extendedTextMessage = new { text = "oi, respondendo sua mensagem" } });

        await factory.CreateClient().PostAsync("/webhook", payload);

        _fakeProvider.Sent.Should().ContainSingle(m => m.Body == "Ola! Como posso ajudar?");
    }

    [Fact]
    public async Task Webhook_V2FromMe_IsIgnored()
    {
        using var factory = BuildFactory();
        var payload = V2Payload("15005550006", "5521988887777@s.whatsapp.net", new { conversation = "oi" }, fromMe: true);

        await factory.CreateClient().PostAsync("/webhook", payload);

        _fakeProvider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_GroupMessage_IsIgnored()
    {
        using var factory = BuildFactory();
        var payload = V2Payload("15005550006", "120363000000000000@g.us", new { conversation = "oi" });

        await factory.CreateClient().PostAsync("/webhook", payload);

        _fakeProvider.Sent.Should().BeEmpty();
        _fakeLlm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Webhook_AudioWithBase64_IsTranscribedAndSentToBot()
    {
        var transcriber = new FakeTranscriber("quero marcar uma consulta");
        _fakeLlm.Returns("Claro! Para qual dia?");
        using var factory = BuildFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<Agiliz.Core.Media.IAudioTranscriber>(transcriber)));

        var payload = V2Payload("15005550006", "5521988887777@s.whatsapp.net", new
        {
            audioMessage = new { mimetype = "audio/ogg; codecs=opus", seconds = 4, ptt = true },
            base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 })
        });

        await factory.CreateClient().PostAsync("/webhook", payload);

        transcriber.Calls.Should().Be(1);
        _fakeLlm.ReceivedHistories.Should().ContainSingle();
        _fakeLlm.ReceivedHistories[0].Should().Contain(m =>
            m.Role == MessageRole.User && m.Content.Contains("quero marcar uma consulta"));
        _fakeProvider.Sent.Should().ContainSingle(m => m.Body == "Claro! Para qual dia?");
        _costs.Entries.Should().Contain(e => e.TenantId == "test-bot" && e.Description.StartsWith("Transcrição de áudio"));
        _costs.Entries.Should().Contain(e => e.TenantId == "test-bot" && e.Type == CostType.TokensLLM);
    }

    [Fact]
    public async Task Webhook_AudioWhenIntegrationIsOff_AsksClientToType()
    {
        var transcriber = new FakeTranscriber("não deveria transcrever");
        using var factory = BuildFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<Agiliz.Core.Media.IAudioTranscriber>(transcriber)));
        // Sobrescreve o bot antes do host subir: transcrição desligada na aba Integrações.
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "test-bot", whatsappNumber: "15005550006") with
        {
            Integrations = new() { [Agiliz.Core.Integrations.IntegrationCatalog.Audio] = new IntegrationSettings { Enabled = false } }
        });

        var payload = V2Payload("15005550006", "5521988887777@s.whatsapp.net", new
        {
            audioMessage = new { mimetype = "audio/ogg; codecs=opus", seconds = 4, ptt = true },
            base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 })
        });
        await factory.CreateClient().PostAsync("/webhook", payload);

        transcriber.Calls.Should().Be(0);
        _fakeLlm.ReceivedHistories.Single().Should().Contain(m => m.Role == MessageRole.User && m.Content.Contains("escrever"));
        _costs.Entries.Should().NotContain(e => e.IntegrationId == Agiliz.Core.Integrations.IntegrationCatalog.Audio);
    }

    private sealed class FakeTranscriber(string text) : Agiliz.Core.Media.IAudioTranscriber
    {
        public int Calls { get; private set; }
        public bool IsEnabled => true;

        public Task<string?> TranscribeAsync(Agiliz.Core.Media.MediaContent audio, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>(text);
        }
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    }
}

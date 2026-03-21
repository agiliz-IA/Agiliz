using System.Net;
using System.Net.Http.Json;
using Agiliz.Core.Config;
using Agiliz.Core.Twilio;
using Agiliz.Runtime.Services;
using Agiliz.Tests.Fakes;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>
/// Testa o Runtime em memória usando WebApplicationFactory.
/// Sem Docker, sem rede real — usa Fakes injetados via DI.
/// </summary>
public sealed class RuntimeWebhookTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly FakeTwilioSender _fakeSender = new();
    private readonly FakeLlmClient _fakeLlm = new();

    private WebApplicationFactory<global::Program> BuildFactory()
    {
        // Salva config de teste em disco para o TenantRegistry encontrar
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-key");

        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(
            tenantId: "test-bot",
            twilioNumber: "whatsapp:+15005550006"
        ).WithFlows(("oi", "Olá! Como posso ajudar?")));

        return new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConfigsDir", _dir.Path);
                builder.ConfigureServices(services =>
                {
                    // Substitui o TwilioSender real pelo fake
                    services.AddSingleton<ITwilioSender>(_fakeSender);

                    // Substitui o TenantRegistry para injetar nosso FakeLlmClient
                    services.AddSingleton(sp =>
                    {
                        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TenantRegistry>>();
                        var registry = new TenantRegistry(_dir.Path, logger);
                        return registry;
                    });
                });
            });
    }

    // ─── /health ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_ReturnsOkWithBotCount()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        body.Should().NotBeNull();
    }

    // ─── /webhook ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_WithMissingFields_ReturnsBadRequest()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var form = new FormUrlEncodedContent([]);
        var response = await client.PostAsync("/webhook", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fakeSender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_WithUnknownTwilioNumber_ReturnsOkButDoesNotSend()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["From"] = "whatsapp:+5521999",
            ["To"]   = "whatsapp:+99999", // número sem bot
            ["Body"] = "oi"
        });

        var response = await client.PostAsync("/webhook", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _fakeSender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_FlowMatch_SendsFlowResponseWithoutLlm()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["From"] = "whatsapp:+5521999",
            ["To"]   = "whatsapp:+15005550006",
            ["Body"] = "oi tudo bem"
        });

        var response = await client.PostAsync("/webhook", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _fakeSender.Sent.Should().ContainSingle(m =>
            m.To == "whatsapp:+5521999" &&
            m.From == "whatsapp:+15005550006" &&
            m.Body == "Olá! Como posso ajudar?");
    }

    [Fact]
    public async Task Webhook_SetsCorrectToAndFromWhenReplying()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["From"] = "whatsapp:+5521123456789",
            ["To"]   = "whatsapp:+15005550006",
            ["Body"] = "oi"
        });

        await client.PostAsync("/webhook", form);

        var sent = _fakeSender.Sent.Single();
        sent.To.Should().Be("whatsapp:+5521123456789");   // responde para quem mandou
        sent.From.Should().Be("whatsapp:+15005550006");   // enviado pelo número do bot
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}

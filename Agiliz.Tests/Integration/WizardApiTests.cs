using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Agiliz.Tests.Integration;

public sealed class WizardApiTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();

    private WebApplicationFactory<global::WizardProgram> BuildFactory()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-key");

        return new WebApplicationFactory<global::WizardProgram>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("ConfigsDir", _dir.Path));
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // ─── GET /api/bots ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBots_WhenNoBots_ReturnsEmptyArray()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/bots");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBots_WhenBotsExist_ReturnsAllWithCorrectFields()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-a", twilioNumber: "whatsapp:+1"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-b", twilioNumber: "whatsapp:+2"));

        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/bots");
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        body.Should().HaveCount(2);
        body!.Select(b => b.GetProperty("tenantId").GetString())
            .Should().Contain("bot-a", "bot-b");
    }

    // ─── GET /api/bots/{id} ───────────────────────────────────────────────────

    [Fact]
    public async Task GetBot_WhenExists_ReturnsConfig()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(
            tenantId: "meu-bot", systemPrompt: "Prompt específico."));

        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/bots/meu-bot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("systemPrompt").GetString().Should().Be("Prompt específico.");
    }

    [Fact]
    public async Task GetBot_WhenNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/bots/nao-existe");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── POST /api/bots ───────────────────────────────────────────────────────

    [Fact]
    public async Task PostBot_WithValidData_Creates201AndSavesFile()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var payload = new
        {
            tenantId = "novo-bot",
            twilioNumber = "whatsapp:+5521999",
            provider = "Groq",
            systemPrompt = "Você é um bot de testes.",
            flows = Array.Empty<object>(),
            maxTokens = 300
        };

        var response = await client.PostAsync("/api/bots", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        File.Exists(Path.Combine(_dir.Path, "novo-bot.json")).Should().BeTrue();
    }

    [Fact]
    public async Task PostBot_NormalizesWhatsappPrefix()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var payload = new
        {
            tenantId = "bot-prefix",
            twilioNumber = "+5521999", // sem o prefixo whatsapp:
            provider = "Groq",
            systemPrompt = "Teste.",
            flows = Array.Empty<object>(),
            maxTokens = 300
        };

        await client.PostAsync("/api/bots", Json(payload));

        var saved = BotConfigLoader.Load(_dir.Path, "bot-prefix");
        saved.TwilioNumber.Should().StartWith("whatsapp:");
    }

    [Fact]
    public async Task PostBot_NormalizesTenantIdToLowerKebab()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var payload = new
        {
            tenantId = "Meu Bot Legal",  // espaços e maiúsculas
            twilioNumber = "whatsapp:+1",
            provider = "Groq",
            systemPrompt = "Teste.",
            flows = Array.Empty<object>(),
            maxTokens = 300
        };

        var response = await client.PostAsync("/api/bots", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        File.Exists(Path.Combine(_dir.Path, "meu-bot-legal.json")).Should().BeTrue();
    }

    // ─── DELETE /api/bots/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBot_WhenExists_Returns204AndRemovesFile()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "deletar-me"));

        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/bots/deletar-me");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_dir.Path, "deletar-me.json")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteBot_WhenNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/bots/nao-existe");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── POST /api/bots/{id}/test ─────────────────────────────────────────────

    [Fact]
    public async Task TestBot_FlowMatch_ReturnFlowResponseWithMatchedTrigger()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "test-bot")
            .WithFlows(("cardápio", "Pizza e lasanha.")));

        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var payload = new { message = "quero o cardápio", history = Array.Empty<object>() };
        var response = await client.PostAsync("/api/bots/test-bot/test", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reply").GetString().Should().Be("Pizza e lasanha.");
        body.GetProperty("flowMatch").GetString().Should().Be("cardápio");
    }

    [Fact]
    public async Task TestBot_WhenBotNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        var payload = new { message = "oi", history = Array.Empty<object>() };
        var response = await client.PostAsync("/api/bots/nao-existe/test", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}

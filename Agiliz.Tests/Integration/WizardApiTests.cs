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

    public WizardApiTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY",      "fake-groq-key");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-anthropic-key");
    }

    private WebApplicationFactory<Agiliz.Wizard.Program> BuildFactory() =>
        new WebApplicationFactory<Agiliz.Wizard.Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConfigsDir", _dir.Path));

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetBots_WhenNoBots_ReturnsEmptyArray()
    {
        using var factory = BuildFactory();
        var response = await factory.CreateClient().GetAsync("/api/bots");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBots_WhenBotsExist_ReturnsAllWithCorrectFields()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-a", whatsappNumber: "1"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-b", whatsappNumber: "2"));

        using var factory = BuildFactory();
        var body = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement[]>("/api/bots");

        body.Should().HaveCount(2);
        body!.Select(b => b.GetProperty("tenantId").GetString())
             .Should().Contain("bot-a", "bot-b");
    }

    [Fact]
    public async Task GetBot_WhenExists_ReturnsConfig()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(
            tenantId: "meu-bot", systemPrompt: "Prompt especifico."));

        using var factory = BuildFactory();
        var response = await factory.CreateClient().GetAsync("/api/bots/meu-bot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("systemPrompt").GetString().Should().Be("Prompt especifico.");
    }

    [Fact]
    public async Task GetBot_WhenNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var response = await factory.CreateClient().GetAsync("/api/bots/nao-existe");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostBot_WithValidData_Creates201AndSavesFile()
    {
        using var factory = BuildFactory();
        var payload = new
        {
            tenantId   = "novo-bot",
            whatsappNumber = "5521999",
            provider   = "Groq",
            systemPrompt = "Voce e um bot de testes.",
            flows      = Array.Empty<object>(),
            maxTokens  = 300
        };

        var response = await factory.CreateClient().PostAsync("/api/bots", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        File.Exists(Path.Combine(_dir.Path, "novo-bot.json")).Should().BeTrue();
    }

    [Fact]
    public async Task PostBot_NormalizesTenantIdToLowerKebab()
    {
        using var factory = BuildFactory();
        var payload = new
        {
            tenantId     = "Meu Bot Legal",
            whatsappNumber = "1",
            provider     = "Groq",
            systemPrompt = "Teste.",
            flows        = Array.Empty<object>(),
            maxTokens    = 300
        };

        await factory.CreateClient().PostAsync("/api/bots", Json(payload));

        File.Exists(Path.Combine(_dir.Path, "meu-bot-legal.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteBot_WhenExists_Returns204AndRemovesFile()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "deletar-me"));

        using var factory = BuildFactory();
        var response = await factory.CreateClient().DeleteAsync("/api/bots/deletar-me");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_dir.Path, "deletar-me.json")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteBot_WhenNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var response = await factory.CreateClient().DeleteAsync("/api/bots/nao-existe");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TestBot_FlowMatch_ReturnsFlowResponseWithMatchedTrigger()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "test-bot")
            .WithFlows(("cardapio", "Pizza e lasanha.")));

        using var factory = BuildFactory();
        var payload = new { message = "quero o cardapio", history = Array.Empty<object>() };
        var response = await factory.CreateClient()
            .PostAsync("/api/bots/test-bot/test", Json(payload));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reply").GetString().Should().Be("Pizza e lasanha.");
        body.GetProperty("flowMatch").GetString().Should().Be("cardapio");
    }

    [Fact]
    public async Task TestBot_WhenBotNotFound_Returns404()
    {
        using var factory = BuildFactory();
        var payload = new { message = "oi", history = Array.Empty<object>() };
        var response = await factory.CreateClient()
            .PostAsync("/api/bots/nao-existe/test", Json(payload));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY",      null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    }
}

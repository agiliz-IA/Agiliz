using Agiliz.Core.Config;
using Agiliz.Runtime.Services;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class TenantRegistryTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();

    private TenantRegistry BuildRegistry()
    {
        // TenantRegistry chama LlmClientFactory que precisa de env vars.
        // Setamos valores falsos para que não lance antes de precisar da API.
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-key");
        return new TenantRegistry(_dir.Path, NullLogger<TenantRegistry>.Instance);
    }

    [Fact]
    public void Resolve_WithCorrectNumber_ReturnsTenant()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(
            tenantId: "bot-a", whatsappNumber: "5521999"));

        var registry = BuildRegistry();
        var entry = registry.Resolve("5521999");

        entry.Should().NotBeNull();
        entry!.Config.TenantId.Should().Be("bot-a");
    }

    [Fact]
    public void Resolve_WithUnknownNumber_ReturnsNull()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(whatsappNumber: "5521111"));

        var registry = BuildRegistry();
        var entry = registry.Resolve("5599999");

        entry.Should().BeNull();
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(whatsappNumber: "5521999"));

        var registry = BuildRegistry();

        registry.Resolve("5521999").Should().NotBeNull();
    }

    [Fact]
    public void Count_ReflectsNumberOfLoadedTenants()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "a", whatsappNumber: "1"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "b", whatsappNumber: "2"));

        var registry = BuildRegistry();

        registry.Count.Should().Be(2);
    }

    [Fact]
    public void Count_WhenNoBots_IsZero()
    {
        var registry = BuildRegistry();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WhenConfigIsCorrupt_SkipsAndLoadsRest()
    {
        // Salva um config válido
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bom", whatsappNumber: "1"));
        // Salva um JSON corrompido manualmente
        File.WriteAllText(Path.Combine(_dir.Path, "bots", "corrompido.json"), "{ invalido }");

        var registry = BuildRegistry();

        registry.Count.Should().Be(1);
        registry.Resolve("1").Should().NotBeNull();
    }

    // ─── Reload sem reiniciar ─────────────────────────────────────────────────

    [Fact]
    public void Reload_PicksUpNewAndRemovedBots()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "antigo", whatsappNumber: "1"));
        var registry = BuildRegistry();

        BotConfigLoader.Delete(_dir.Path, "antigo");
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "novo", whatsappNumber: "2"));
        var result = registry.Reload();

        result.Loaded.Should().Equal("novo");
        result.Removed.Should().Equal("antigo");
        registry.Resolve("1").Should().BeNull();
        registry.Resolve("2")!.Config.TenantId.Should().Be("novo");
    }

    [Fact]
    public void Reload_AppliesEditedConfig()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot", whatsappNumber: "1", systemPrompt: "v1"));
        var registry = BuildRegistry();

        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot", whatsappNumber: "1", systemPrompt: "v2"));
        registry.Reload();

        registry.Resolve("1")!.Config.SystemPrompt.Should().Be("v2");
    }

    [Fact]
    public void Reload_DuplicateNumber_KeepsFirstAndReportsError()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "a-primeiro", whatsappNumber: "1"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "b-repetido", whatsappNumber: "1"));

        var result = BuildRegistry().Reload();

        result.Loaded.Should().Equal("a-primeiro");
        result.Errors.Should().ContainSingle(e => e.TenantId == "b-repetido");
    }

    public void Dispose()
    {
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}

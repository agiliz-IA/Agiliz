using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class BotConfigLoaderTests : IDisposable
{
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();

    // ─── Save / Load round-trip ───────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_PreservesAllFields()
    {
        var original = TestFixtures.DefaultConfig(
            tenantId: "pizzaria-roma",
            twilioNumber: "whatsapp:+5521999999999",
            provider: LlmProvider.Claude,
            systemPrompt: "Você é a Lia.",
            flows: [TestFixtures.Flow("cardápio", "Pizza e lasanha.")]
        );

        BotConfigLoader.Save(_dir.Path, original);
        var loaded = BotConfigLoader.Load(_dir.Path, "pizzaria-roma");

        loaded.TenantId.Should().Be("pizzaria-roma");
        loaded.TwilioNumber.Should().Be("whatsapp:+5521999999999");
        loaded.SystemPrompt.Should().Be("Você é a Lia.");
        loaded.Llm.Provider.Should().Be(LlmProvider.Claude);
        loaded.Flows.Should().ContainSingle(f =>
            f.Trigger == "cardápio" && f.Response == "Pizza e lasanha.");
    }

    [Fact]
    public void Save_CreatesJsonFileWithCorrectName()
    {
        var config = TestFixtures.DefaultConfig(tenantId: "meu-bot");
        BotConfigLoader.Save(_dir.Path, config);

        File.Exists(Path.Combine(_dir.Path, "meu-bot.json")).Should().BeTrue();
    }

    [Fact]
    public void Save_WhenDirDoesNotExist_CreatesIt()
    {
        var nestedDir = Path.Combine(_dir.Path, "sub", "configs");
        var config = TestFixtures.DefaultConfig();

        BotConfigLoader.Save(nestedDir, config);

        Directory.Exists(nestedDir).Should().BeTrue();
    }

    [Fact]
    public void Save_Twice_OverwritesFile()
    {
        var config = TestFixtures.DefaultConfig(systemPrompt: "v1");
        BotConfigLoader.Save(_dir.Path, config);

        var updated = config with { SystemPrompt = "v2" };
        BotConfigLoader.Save(_dir.Path, updated);

        var loaded = BotConfigLoader.Load(_dir.Path, config.TenantId);
        loaded.SystemPrompt.Should().Be("v2");
    }

    // ─── Load erros ───────────────────────────────────────────────────────────

    [Fact]
    public void Load_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => BotConfigLoader.Load(_dir.Path, "nao-existe");
        act.Should().Throw<FileNotFoundException>();
    }

    // ─── ListTenants ──────────────────────────────────────────────────────────

    [Fact]
    public void ListTenants_ReturnsAllSavedTenantIds()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-a"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-b"));
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot-c"));

        var tenants = BotConfigLoader.ListTenants(_dir.Path).ToList();

        tenants.Should().HaveCount(3);
        tenants.Should().Contain("bot-a", "bot-b", "bot-c");
    }

    [Fact]
    public void ListTenants_WhenDirDoesNotExist_ReturnsEmpty()
    {
        var tenants = BotConfigLoader.ListTenants("/path/que/nao/existe");
        tenants.Should().BeEmpty();
    }

    [Fact]
    public void ListTenants_IgnoresNonJsonFiles()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "real-bot"));
        File.WriteAllText(Path.Combine(_dir.Path, "lixo.txt"), "ignorar");

        var tenants = BotConfigLoader.ListTenants(_dir.Path).ToList();

        tenants.Should().ContainSingle().Which.Should().Be("real-bot");
    }

    public void Dispose() => _dir.Dispose();
}

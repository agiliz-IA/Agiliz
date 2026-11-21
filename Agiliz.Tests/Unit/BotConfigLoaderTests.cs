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
            whatsappNumber: "5521999999999",
            provider: LlmProvider.Claude,
            systemPrompt: "Você é a Lia.",
            flows: [TestFixtures.Flow("cardápio", "Pizza e lasanha.")]
        );

        BotConfigLoader.Save(_dir.Path, original);
        var loaded = BotConfigLoader.Load(_dir.Path, "pizzaria-roma");

        loaded.TenantId.Should().Be("pizzaria-roma");
        loaded.WhatsAppNumber.Should().Be("5521999999999");
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

        File.Exists(Path.Combine(_dir.Path, "bots", "meu-bot.json")).Should().BeTrue();
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
        File.WriteAllText(Path.Combine(_dir.Path, "bots", "lixo.txt"), "ignorar");

        var tenants = BotConfigLoader.ListTenants(_dir.Path).ToList();

        tenants.Should().ContainSingle().Which.Should().Be("real-bot");
    }

    [Fact]
    public void ListTenants_IgnoresSystemFilesInConfigsRoot()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "real-bot"));
        File.WriteAllText(Path.Combine(_dir.Path, "billing.json"), "[]");
        File.WriteAllText(Path.Combine(_dir.Path, "telemetry.json"), "[]");

        BotConfigLoader.ListTenants(_dir.Path).Should().Equal("real-bot");
    }

    [Theory]
    [InlineData("../billing")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("com espaco")]
    public void Load_WithInvalidTenantId_Throws(string tenantId)
    {
        var act = () => BotConfigLoader.Load(_dir.Path, tenantId);
        act.Should().Throw<ArgumentException>();
        BotConfigLoader.IsValidTenantId(tenantId).Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesConfigAndReturnsFalseWhenMissing()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "apagar"));

        BotConfigLoader.Delete(_dir.Path, "apagar").Should().BeTrue();
        BotConfigLoader.Delete(_dir.Path, "apagar").Should().BeFalse();
        BotConfigLoader.ListTenants(_dir.Path).Should().BeEmpty();
    }

    // ─── Migração do layout antigo (configs/*.json → configs/bots/) ──────────

    [Fact]
    public void MigrateLegacyLayout_MovesOnlyBotConfigs()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "dentista.json"), """{ "tenantId": "dentista", "whatsAppNumber": "1" }""");
        File.WriteAllText(Path.Combine(_dir.Path, "billing.json"), """[{ "TenantId": "x" }]""");
        File.WriteAllText(Path.Combine(_dir.Path, "google_credentials.json"), """{ "type": "service_account" }""");
        File.WriteAllText(Path.Combine(_dir.Path, "outro.json"), """{ "qualquer": true }""");

        var moved = BotConfigLoader.MigrateLegacyLayout(_dir.Path);

        moved.Should().Equal("dentista");
        File.Exists(Path.Combine(_dir.Path, "bots", "dentista.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir.Path, "billing.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir.Path, "google_credentials.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir.Path, "outro.json")).Should().BeTrue();
        BotConfigLoader.Load(_dir.Path, "dentista").WhatsAppNumber.Should().Be("1");
    }

    [Fact]
    public void MigrateLegacyLayout_DoesNotOverwriteNewerConfig()
    {
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "bot", systemPrompt: "novo"));
        File.WriteAllText(Path.Combine(_dir.Path, "bot.json"), """{ "tenantId": "bot", "systemPrompt": "antigo" }""");

        BotConfigLoader.MigrateLegacyLayout(_dir.Path).Should().BeEmpty();
        BotConfigLoader.MigrateLegacyLayout(_dir.Path).Should().BeEmpty();

        BotConfigLoader.Load(_dir.Path, "bot").SystemPrompt.Should().Be("novo");
    }

    public void Dispose() => _dir.Dispose();
}

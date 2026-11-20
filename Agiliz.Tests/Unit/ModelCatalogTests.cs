using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class ModelCatalogTests
{
    [Theory]
    [InlineData("llama-3.3-70b-versatile", "openai/gpt-oss-120b")]
    [InlineData("llama-3.1-8b-instant", "openai/gpt-oss-20b")]
    [InlineData("claude-sonnet-4-20250514", "claude-haiku-5-5")]
    [InlineData("openai/gpt-oss-120b", "openai/gpt-oss-120b")]
    public void Resolve_ReplacesShutDownModels(string configured, string expected)
    {
        ModelCatalog.Resolve(configured).Should().Be(expected);
    }

    [Fact]
    public void EstimateCostUsd_UsesModelPrice()
    {
        // gpt-oss-120b: US$ 0,15 entrada / 0,60 saída por 1M tokens
        var cost = ModelCatalog.EstimateCostUsd("openai/gpt-oss-120b", new TokenUsage(1_000_000, 1_000_000));
        cost.Should().Be(0.75m);
    }

    [Fact]
    public void EstimateCostUsd_DeprecatedModelIsPricedAsReplacement()
    {
        var cost = ModelCatalog.EstimateCostUsd("llama-3.3-70b-versatile", new TokenUsage(2_000, 200));
        cost.Should().Be(ModelCatalog.EstimateCostUsd("openai/gpt-oss-120b", new TokenUsage(2_000, 200)));
    }

    [Fact]
    public void EstimateCostUsd_UnknownModelUsesConservativePrice()
    {
        ModelCatalog.TryGetPrice("modelo-que-nao-existe", out var price).Should().BeFalse();
        price.Should().Be(ModelCatalog.UnknownModelPrice);
    }

    [Fact]
    public void EstimateTranscriptionCostUsd_BillsAtLeastTenSeconds()
    {
        ModelCatalog.EstimateTranscriptionCostUsd(3).Should().Be(ModelCatalog.EstimateTranscriptionCostUsd(10));
        ModelCatalog.EstimateTranscriptionCostUsd(3600).Should().Be(ModelCatalog.WhisperTurboPerHourUsd);
    }

    [Fact]
    public void DefaultLlmSettings_UseSupportedModel()
    {
        var settings = new LlmSettings();
        ModelCatalog.IsDeprecated(settings.Model).Should().BeFalse();
        ModelCatalog.TryGetPrice(settings.Model, out _).Should().BeTrue();
    }
}

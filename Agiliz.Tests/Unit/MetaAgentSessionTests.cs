using Agiliz.CLI.Agent;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class MetaAgentSessionTests
{
    // ─── HasConfigBlock ───────────────────────────────────────────────────────

    [Fact]
    public void HasConfigBlock_WithBothMarkers_ReturnsTrue()
    {
        var reply = TestFixtures.AgentConfigReplyFormatted();
        MetaAgentSession.HasConfigBlock(reply).Should().BeTrue();
    }

    [Fact]
    public void HasConfigBlock_WithoutMarkers_ReturnsFalse()
    {
        MetaAgentSession.HasConfigBlock("Qual o nome do seu negócio?").Should().BeFalse();
    }

    [Fact]
    public void HasConfigBlock_OnlyStartMarker_ReturnsFalse()
    {
        MetaAgentSession.HasConfigBlock("===JSON_START===\n{...}").Should().BeFalse();
    }

    [Fact]
    public void HasConfigBlock_OnlyEndMarker_ReturnsFalse()
    {
        MetaAgentSession.HasConfigBlock("{...}\n===JSON_END===").Should().BeFalse();
    }

    // ─── ExtractConfig ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractConfig_ExtractsSystemPromptCorrectly()
    {
        var reply = TestFixtures.AgentConfigReplyFormatted(
            systemPrompt: "Você é a Lia, atendente da Pizzaria Roma.");

        var (systemPrompt, _) = MetaAgentSession.ExtractConfig(reply);

        systemPrompt.Should().Be("Você é a Lia, atendente da Pizzaria Roma.");
    }

    [Fact]
    public void ExtractConfig_ExtractsFlowsCorrectly()
    {
        var reply = TestFixtures.AgentConfigReplyFormatted(flowsJson: """
            [
              {"trigger": "cardápio", "response": "Pizza e lasanha."},
              {"trigger": "endereço", "response": "Rua das Flores, 123."}
            ]
            """);

        var (_, flows) = MetaAgentSession.ExtractConfig(reply);

        flows.Should().HaveCount(2);
        flows[0].Trigger.Should().Be("cardápio");
        flows[0].Response.Should().Be("Pizza e lasanha.");
        flows[1].Trigger.Should().Be("endereço");
    }

    [Fact]
    public void ExtractConfig_WhenNoFlows_ReturnsEmptyList()
    {
        var reply = TestFixtures.AgentConfigReplyFormatted(flowsJson: "[]");

        var (_, flows) = MetaAgentSession.ExtractConfig(reply);

        flows.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConfig_IgnoresTextBeforeStartMarker()
    {
        // O agente frequentemente coloca texto antes do bloco JSON
        var reply = """
            Tenho tudo que preciso. Gerando configuração...

            ===JSON_START===
            {
              "systemPrompt": "Prompt correto.",
              "flows": []
            }
            ===JSON_END===
            """;

        var (systemPrompt, _) = MetaAgentSession.ExtractConfig(reply);

        systemPrompt.Should().Be("Prompt correto.");
    }

    [Fact]
    public void ExtractConfig_WhenJsonIsMalformed_ThrowsException()
    {
        var reply = "===JSON_START===\n{ json inválido \n===JSON_END===";

        var act = () => MetaAgentSession.ExtractConfig(reply);

        act.Should().Throw<Exception>();
    }
}

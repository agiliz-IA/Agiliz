// ────────────────────────────────────────────────────────────────────────────
// Pré-requisito: instalar o Playwright após o build
//
//   dotnet build
//   pwsh bin/Debug/net10.0/playwright.ps1 install chromium
//
// Para rodar: dotnet test --filter "Category=E2E"
// Os testes E2E precisam do Wizard em http://localhost:5001 E do WireMock
// para simular as respostas do LLM (evita custos e torna testes determinísticos).
// ────────────────────────────────────────────────────────────────────────────

using Agiliz.Tests.Helpers;
using Microsoft.Playwright;
using Xunit;

namespace Agiliz.Tests.E2E;

[Trait("Category", "E2E")]
public sealed class WizardCreateFlowTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private readonly LlmWireMockServer _llmMock = new();
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();

    private const string WizardUrl = "http://localhost:5001";

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true // mude para false para depurar visualmente
        });

        // Configura o mock LLM para simular o meta-agente retornando um config pronto
        _llmMock.SetupGroqMetaAgentConfig(
            systemPrompt: "Você é a Lia, atendente de teste.",
            flows: """[{"trigger":"oi","response":"Olá!"}]"""
        );
    }

    // ─── Fluxo completo: criar bot ────────────────────────────────────────────

    [Fact(Skip = "Requer Wizard rodando em localhost:5001")]
    public async Task CreateBot_FullFlow_BotAppearsInDashboard()
    {
        var page = await _browser.NewPageAsync();
        await page.GotoAsync(WizardUrl);

        // Aguarda dashboard carregar
        await page.WaitForSelectorAsync(".bots-grid");

        // Clica em Novo Bot
        await page.ClickAsync("button:has-text('Novo Bot')");
        await page.WaitForSelectorAsync("#f-tenant");

        // Preenche o formulário (Passo 1)
        await page.FillAsync("#f-tenant", "e2e-test-bot");
        await page.FillAsync("#f-number", "whatsapp:+15005550006");

        // Clica em Iniciar Entrevista
        await page.ClickAsync("button:has-text('Iniciar Entrevista')");
        await page.WaitForSelectorAsync("#wizard-chat");

        // Aguarda a saudação do agente
        await page.WaitForSelectorAsync(".chat-bubble.agent");

        // Envia uma mensagem — o WireMock responderá com o config pronto
        await page.FillAsync("#wizard-input", "Somos uma pizzaria chamada Roma.");
        await page.ClickAsync("button:has-text('Enviar')");

        // Aguarda o wizard avançar para o passo 3 (config gerado)
        await page.WaitForSelectorAsync("#config-preview-json", new() { Timeout = 15_000 });

        // Confirma os dados e salva
        await page.ClickAsync("button:has-text('Salvar Bot')");

        // Deve voltar ao dashboard com o novo card
        await page.WaitForSelectorAsync("#card-e2e-test-bot");
        var card = await page.QuerySelectorAsync("#card-e2e-test-bot");
        Assert.NotNull(card);
    }

    // ─── Fluxo de teste de bot ────────────────────────────────────────────────

    [Fact(Skip = "Requer Wizard rodando em localhost:5001")]
    public async Task TestBot_FlowMatch_ShowsFlowBadge()
    {
        var page = await _browser.NewPageAsync();
        await page.GotoAsync(WizardUrl);

        // Cria bot diretamente via API para não depender do meta-agente
        await page.EvaluateAsync("""
            fetch('/api/bots', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    tenantId: 'ui-test-bot',
                    twilioNumber: 'whatsapp:+15005550006',
                    provider: 'Groq',
                    systemPrompt: 'Bot de teste.',
                    flows: [{ trigger: 'oi', response: 'Olá!' }],
                    maxTokens: 300
                })
            })
        """);

        await page.ReloadAsync();
        await page.WaitForSelectorAsync("#card-ui-test-bot");

        // Clica em Testar
        await page.ClickAsync("#card-ui-test-bot button:has-text('Testar')");
        await page.WaitForSelectorAsync("#test-chat");

        // Envia mensagem que faz match com flow
        await page.FillAsync("#test-input", "oi tudo bem");
        await page.ClickAsync("button:has-text('Enviar')");

        // Deve aparecer o badge de flow match
        var flowBadge = await page.WaitForSelectorAsync(".flow-badge-inline");
        Assert.NotNull(flowBadge);
        var badgeText = await flowBadge.TextContentAsync();
        Assert.Contains("oi", badgeText);
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
        _llmMock.Dispose();
        _dir.Dispose();
    }
}

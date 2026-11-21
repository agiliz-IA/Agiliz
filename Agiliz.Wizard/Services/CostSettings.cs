using Agiliz.Core.Integrations;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

/// <summary>
/// Conversão para reais e custos fixos mensais, lidos da configuração do painel:
/// <code>
/// Billing:UsdToBrl = 5.50                    (câmbio usado no Financeiro)
/// Billing:FixedMonthlyBrl:whatsapp = 15      (custo fixo por cliente/mês de cada integração ligada)
/// </code>
/// Em docker-compose: Billing__UsdToBrl, Billing__FixedMonthlyBrl__whatsapp.
/// </summary>
public sealed class CostSettings(IConfiguration configuration)
{
    public const decimal DefaultUsdToBrl = 5.50m;

    /// <summary>Câmbio configurado, ou null se ainda não foi definido.</summary>
    public decimal? ConfiguredUsdToBrl =>
        decimal.TryParse(configuration["Billing:UsdToBrl"], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null;

    public decimal UsdToBrl => ConfiguredUsdToBrl ?? DefaultUsdToBrl;

    public decimal ToBrl(decimal usd) => Math.Round(usd * UsdToBrl, 4);

    /// <summary>Custo fixo por cliente/mês de uma integração (0 se não configurado).</summary>
    public decimal FixedMonthlyBrl(string integrationId) =>
        decimal.TryParse(configuration[$"Billing:FixedMonthlyBrl:{integrationId}"], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0m;

    /// <summary>Soma dos custos fixos das integrações ligadas no bot.</summary>
    public decimal FixedMonthlyBrl(BotConfig config) =>
        BotIntegrations.Enabled(config).Sum(i => FixedMonthlyBrl(i.Id));
}

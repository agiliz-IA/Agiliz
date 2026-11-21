namespace Agiliz.Core.Integrations;

/// <summary>Resultado do botão "Testar conexão". A mensagem vai direto para o painel, em português.</summary>
public sealed record IntegrationTestResult(bool Ok, string Message);

/// <summary>Corpo de POST /api/admin/integrations/{id}/test. Fields sobrescreve o config salvo (testar antes de salvar).</summary>
public sealed record IntegrationTestRequest
{
    public required string TenantId { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
}

/// <summary>O que o servidor sabe sobre as integrações (GET /api/admin/integrations/info).</summary>
public sealed record IntegrationServerInfo
{
    /// <summary>E-mail com que o cliente compartilha a agenda.</summary>
    public string? GoogleServiceAccountEmail { get; init; }
    public bool GoogleConfigured { get; init; }
    public bool AudioConfigured { get; init; }
    public bool SmtpConfigured { get; init; }
}

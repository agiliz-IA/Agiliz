namespace Agiliz.Runtime.Services;

public record RuntimeContextData(string TenantId, string UserPhone);

public static class RuntimeContext
{
    public static AsyncLocal<RuntimeContextData> Current { get; } = new();
}

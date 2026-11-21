using Agiliz.Runtime.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class AdminTokenFilterTests
{
    private const string Passed = "passou";

    private static async Task<object?> RunAsync(string? configuredToken, string environment, string? header)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Runtime:AdminToken"] = configuredToken })
            .Build();
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environment);

        var http = new DefaultHttpContext();
        if (header is not null)
            http.Request.Headers[AdminTokenFilter.HeaderName] = header;

        var filter = new AdminTokenFilter(config, env.Object);
        return await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>(Passed));
    }

    private static int? StatusOf(object? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Fact]
    public async Task CorrectToken_Passes() =>
        (await RunAsync("segredo", Environments.Production, "segredo")).Should().Be(Passed);

    [Theory]
    [InlineData(null)]
    [InlineData("errado")]
    [InlineData("segredo-com-sufixo")]
    public async Task MissingOrWrongToken_Returns401(string? header) =>
        StatusOf(await RunAsync("segredo", Environments.Production, header)).Should().Be(401);

    [Fact]
    public async Task NoTokenConfigured_InProduction_Returns503() =>
        StatusOf(await RunAsync(null, Environments.Production, null)).Should().Be(503);

    [Fact]
    public async Task NoTokenConfigured_InDevelopment_Passes() =>
        (await RunAsync(null, Environments.Development, null)).Should().Be(Passed);
}

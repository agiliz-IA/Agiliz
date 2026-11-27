using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agiliz.Core.Config;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using Agiliz.Wizard.Accounts;
using Agiliz.Wizard.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>Entrada única (/entrar): contas, várias formas de entrar, convites, sessão e acesso ao painel.</summary>
public sealed class WizardAuthTests : IDisposable
{
    private const string ClientPhone = "5521911112222";
    private readonly TempDirectory _dir = TestFixtures.CreateTempDir();
    private readonly WireMockServer _runtime = WireMockServer.Start();

    public WizardAuthTests()
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "fake-groq-key");
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "clinica", whatsappNumber: "5521900000020") with
        {
            DisplayName = "Clínica Sorriso",
            PortalPhones = [ClientPhone]
        });
        BotConfigLoader.Save(_dir.Path, TestFixtures.DefaultConfig(tenantId: "outra", whatsappNumber: "5521900000021"));
        _runtime.Given(Request.Create().WithPath("/api/admin/sender/notify").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        _runtime.Given(Request.Create().WithPath("/api/admin/reload").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { }));
    }

    private WebApplicationFactory<Agiliz.Wizard.Program> BuildFactory() =>
        new WebApplicationFactory<Agiliz.Wizard.Program>().WithWebHostBuilder(b => b
            .UseSetting("ConfigsDir", _dir.Path)
            .UseSetting("Runtime:ApiUrl", _runtime.Url)
            .UseSetting("Runtime:AdminToken", "token")
            .UseSetting("Wizard:AdminPassword", "senha-admin")
            .UseSetting("Auth:Google:ClientId", "google-client")
            .UseSetting("Auth:Google:ClientSecret", "google-secret")
            .UseSetting("Portal:LoginRateLimitPerMinute", "100"));

    private static HttpRequestMessage Post(string path, object? body = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        if (csrf) request.Headers.Add(PortalEndpoints.CsrfHeader, "1");
        return request;
    }

    private static HttpClient NoRedirect(WebApplicationFactory<Agiliz.Wizard.Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(HttpClient Client, JsonElement Verify)> LoginAsync(WebApplicationFactory<Agiliz.Wizard.Program> factory, string identifier)
    {
        var client = NoRedirect(factory);
        (await client.SendAsync(Post("/entrar/api/start", new { identifier }))).StatusCode.Should().Be(HttpStatusCode.OK);
        var verify = await client.SendAsync(Post("/entrar/api/verify", new { identifier, code = TestFixtures.LastAccessCode(_runtime) }));
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        return (client, await verify.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static Account AddStaff(WebApplicationFactory<Agiliz.Wizard.Program> factory, StaffRole role, string phone, string name = "Ana") =>
        factory.Services.GetRequiredService<AccountStore>().Add(new Account
        {
            Name = name,
            Role = role,
            ProtectPromptSeen = true,
            Identities = [new AccountIdentity { Kind = IdentityKind.WhatsApp, Value = phone }, new AccountIdentity { Kind = IdentityKind.Email, Value = $"{name.ToLowerInvariant()}@agiliz.ia.br" }]
        });

    // ─── Entrar ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task PortalPhone_GetsAccountOnFirstLogin_AndGoesThroughProtectScreen()
    {
        using var factory = BuildFactory();

        var (client, verify) = await LoginAsync(factory, "(21) 91111-2222");

        verify.GetProperty("next").GetString().Should().Be("/entrar/proteger?volta=%2Fportal", "primeira vez e uma forma só de entrar");
        var me = await client.GetFromJsonAsync<JsonElement>("/entrar/api/me");
        me.GetProperty("name").GetString().Should().Be("Clínica Sorriso");
        me.GetProperty("destination").GetString().Should().Be("/portal");
        me.GetProperty("staff").GetBoolean().Should().BeFalse();
        File.Exists(Path.Combine(_dir.Path, "accounts.json")).Should().BeTrue();
    }

    [Fact]
    public async Task PortalPhone_WithoutNinthDigit_StillFindsTheAccount()
    {
        using var factory = BuildFactory();

        await LoginAsync(factory, "21 1111-2222");
    }

    [Fact]
    public async Task InvalidIdentifier_AndMissingCsrfHeader_AreRejected()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        (await client.SendAsync(Post("/entrar/api/start", new { identifier = "abc" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Post("/entrar/api/start", new { identifier = ClientPhone }, csrf: false))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EmailCode_WithoutSmtp_SaysEmailIsUnavailable()
    {
        using var factory = BuildFactory();
        AddStaff(factory, StaffRole.Admin, "5521977776666");

        var response = await factory.CreateClient().SendAsync(Post("/entrar/api/start", new { identifier = "ana@agiliz.ia.br" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Contain("e-mail");
    }

    [Fact]
    public async Task EntryPage_IsServedOutsideBasicAuth_WithStrictHeaders()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();

        foreach (var path in new[] { "/entrar", "/entrar/conta", "/entrar/proteger" })
        {
            var page = await client.GetAsync(path);
            page.StatusCode.Should().Be(HttpStatusCode.OK, path);
            page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self'");
        }
        var script = await client.GetAsync("/entrar/entrar.js");
        script.StatusCode.Should().Be(HttpStatusCode.OK);
        script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript", "o arquivo de verdade, não a página");
        (await client.GetAsync("/entrar/entrar.css")).Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        var config = await client.GetFromJsonAsync<JsonElement>("/entrar/api/config");
        config.GetProperty("google").GetBoolean().Should().BeTrue();
        config.GetProperty("email").GetBoolean().Should().BeFalse();
    }

    // ─── Painel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Painel_UsesEmergencyPasswordOnlyUntilFirstAdmin()
    {
        using var factory = BuildFactory();
        var basic = NoRedirect(factory);
        basic.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:senha-admin")));

        (await basic.GetAsync("/api/bots")).StatusCode.Should().Be(HttpStatusCode.OK, "ninguém virou Admin ainda");

        AddStaff(factory, StaffRole.Admin, "5521977776666");

        (await basic.GetAsync("/api/bots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "com um Admin, a senha de emergência para de valer");
        var page = new HttpRequestMessage(HttpMethod.Get, "/acessos");
        page.Headers.Add("Accept", "text/html");
        var redirect = await basic.SendAsync(page);
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirect.Headers.Location!.ToString().Should().Be("/entrar?volta=%2Facessos");

        var (staff, verify) = await LoginAsync(factory, "21977776666");
        verify.GetProperty("next").GetString().Should().Be("/");
        (await staff.GetAsync("/api/bots")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Painel_ClosedToClients()
    {
        using var factory = BuildFactory();
        AddStaff(factory, StaffRole.Admin, "5521977776666");

        var (client, _) = await LoginAsync(factory, ClientPhone);

        (await client.GetAsync("/api/bots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/portal/api/bots/clinica/conversations")).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StaffWhoIsAlsoClient_ChoosesWhereToGo()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccountStore>();
        var ana = AddStaff(factory, StaffRole.Vendedor, "5521977776666");
        store.Update(ana.Id, a => a.Bots = ["outra"]);

        var (client, verify) = await LoginAsync(factory, "21977776666");

        verify.GetProperty("next").GetString().Should().Be("/entrar/escolher");
        var me = await client.GetFromJsonAsync<JsonElement>("/portal/api/me");
        me.GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("tenantId").GetString()).Should().Equal("outra");
    }

    [Fact]
    public void Vendedor_CannotOpenCostScreens()
    {
        var seller = new PainelUser(new Account { Role = StaffRole.Vendedor });

        seller.CanOpen("billing").Should().BeFalse();
        seller.CanOpen("telemetry").Should().BeFalse();
        seller.CanOpen("acessos").Should().BeTrue();
        seller.CanOpen("billing-x").Should().BeTrue();
        new PainelUser(null).CanOpen("billing").Should().BeTrue("senha de emergência vale como Admin");
    }

    // ─── Convites e acessos ───────────────────────────────────────────────────

    [Fact]
    public async Task Invite_LinkSignsInOnce_ThenStopsWorking()
    {
        using var factory = BuildFactory();
        var access = factory.Services.GetRequiredService<AccessService>();

        var outcome = await access.InviteAsync(new InviteRequest("Dra. Paula", null, "(11) 97777-1234, paula@odonto.com.br", ["outra"]), new PainelUser(null), "http://localhost/");

        outcome.Ok.Should().BeTrue(outcome.Error);
        outcome.Link.Should().StartWith("http://localhost/entrar/convite/");
        outcome.SentTo.Should().Equal("+55 (11) 97777-1234");
        JsonDocument.Parse(_runtime.LogEntries.Last().RequestMessage.Body!).RootElement.GetProperty("text").GetString().Should().Contain(outcome.Link);

        var path = new Uri(outcome.Link!).AbsolutePath;
        var client = NoRedirect(factory);
        var first = await client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        first.Headers.Location!.ToString().Should().Be("/portal", "já tem duas formas de entrar (WhatsApp e e-mail), então pula \"Proteja sua conta\"");
        first.Headers.GetValues("Set-Cookie").Single().Should().Contain(AuthService.CookieName);

        var again = await NoRedirect(factory).GetAsync(path);
        again.Headers.Location!.ToString().Should().Be("/entrar?erro=convite");
    }

    [Fact]
    public async Task Invite_RejectsIdentifierOfAnotherAccount_AndSellerCannotInviteStaff()
    {
        using var factory = BuildFactory();
        var access = factory.Services.GetRequiredService<AccessService>();
        AddStaff(factory, StaffRole.Admin, "5521977776666");
        var seller = new PainelUser(new Account { Role = StaffRole.Vendedor });

        (await access.InviteAsync(new InviteRequest("Outro", null, "21977776666", ["outra"]), seller, "http://x/")).Error.Should().Contain("já é de Ana");
        (await access.InviteAsync(new InviteRequest("Novo", StaffRole.Admin, "21966665555", []), seller, "http://x/")).Error.Should().Contain("Só um Admin");
        (await access.InviteAsync(new InviteRequest("Cliente", null, "21966665555", []), seller, "http://x/")).Error.Should().Contain("bot");
    }

    [Fact]
    public async Task Revoke_RemovesAccountAndLegacyPortalPhone()
    {
        using var factory = BuildFactory();
        var (client, _) = await LoginAsync(factory, ClientPhone);
        var store = factory.Services.GetRequiredService<AccountStore>();
        var account = store.FindByIdentity(IdentityKind.WhatsApp, ClientPhone)!;

        (await factory.Services.GetRequiredService<AccessService>().RevokeAsync(account.Id, new PainelUser(null))).Should().BeNull();

        (await client.GetAsync("/portal/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "vale na hora");
        BotConfigLoader.Load(_dir.Path, "clinica").PortalPhones.Should().BeEmpty("senão o número ganharia conta de novo");
        (await factory.CreateClient().SendAsync(Post("/entrar/api/start", new { identifier = ClientPhone }))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LastAdmin_CannotBeDemotedOrRevoked()
    {
        using var factory = BuildFactory();
        var access = factory.Services.GetRequiredService<AccessService>();
        var ana = AddStaff(factory, StaffRole.Admin, "5521977776666");

        (await access.UpdateAsync(ana.Id, "Ana", StaffRole.Vendedor, [], new PainelUser(null))).Should().Contain("única conta Admin");
        (await access.RevokeAsync(ana.Id, new PainelUser(null))).Should().Contain("única conta Admin");
    }

    // ─── Minha conta ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Account_AddsAndRemovesWaysToSignIn_ButNeverTheLast()
    {
        using var factory = BuildFactory();
        var (client, _) = await LoginAsync(factory, ClientPhone);

        (await client.SendAsync(Post("/entrar/api/identities/remove", new { kind = "WhatsApp", value = ClientPhone }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.SendAsync(Post("/entrar/api/identities/start", new { identifier = "21 95555-4444" }))).StatusCode.Should().Be(HttpStatusCode.OK);
        var added = await client.SendAsync(Post("/entrar/api/identities/verify", new { code = TestFixtures.LastAccessCode(_runtime) }));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("identities").GetArrayLength().Should().Be(2);

        (await client.SendAsync(Post("/entrar/api/identities/start", new { identifier = "21 95555-4444" }))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var removed = await client.SendAsync(Post("/entrar/api/identities/remove", new { kind = "WhatsApp", value = ClientPhone }));
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        // O número novo continua entrando na mesma conta.
        var (_, verify) = await LoginAsync(factory, "21955554444");
        verify.GetProperty("next").GetString().Should().Be("/entrar/proteger?volta=%2Fentrar%2Fconta",
            "sem o número antigo, a conta não vê mais o bot que listava esse número");
    }

    [Fact]
    public async Task SignOutEverywhere_InvalidatesOldSessions()
    {
        using var factory = BuildFactory();
        var (first, _) = await LoginAsync(factory, ClientPhone);
        var (second, _) = await LoginAsync(factory, ClientPhone);

        (await second.SendAsync(Post("/entrar/api/logout-all"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await first.GetAsync("/entrar/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Google ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Google_RedirectsWithState_AndRejectsReturnWithoutIt()
    {
        using var factory = BuildFactory();
        var client = NoRedirect(factory);

        var start = await client.GetAsync("/entrar/google?volta=/portal");
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        start.Headers.Location!.ToString().Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth?client_id=google-client");
        start.Headers.GetValues("Set-Cookie").Single().Should().Contain(GoogleSignIn.StateCookie);

        var forged = await NoRedirect(factory).GetAsync("/entrar/google/retorno?code=x&state=y");
        forged.Headers.Location!.ToString().Should().Be("/entrar?erro=google");
    }

    [Fact]
    public void GoogleIdToken_ChecksAudienceExpiryAndVerifiedEmail()
    {
        using var factory = BuildFactory();
        var google = factory.Services.GetRequiredService<GoogleSignIn>();
        static string Token(object payload) =>
            "x." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".y";
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        google.ReadIdToken(Token(new { iss = "https://accounts.google.com", aud = "google-client", exp, sub = "123", email = "Ana@Agiliz.ia.br", email_verified = true }))
            .Should().Be(new GoogleSignIn.GoogleUser("123", "ana@agiliz.ia.br"));
        google.ReadIdToken(Token(new { iss = "https://accounts.google.com", aud = "outro", exp, sub = "123", email = "a@b.c", email_verified = true })).Should().BeNull();
        google.ReadIdToken(Token(new { iss = "https://accounts.google.com", aud = "google-client", exp, sub = "123", email = "a@b.c", email_verified = false })).Should().BeNull();
        google.ReadIdToken(Token(new { iss = "https://accounts.google.com", aud = "google-client", exp = 1, sub = "123", email = "a@b.c", email_verified = true })).Should().BeNull();
    }

    [Fact]
    public void Google_SignInFindsAccountBySameEmail_AndRemembersIt()
    {
        using var factory = BuildFactory();
        var auth = factory.Services.GetRequiredService<AuthService>();
        AddStaff(factory, StaffRole.Admin, "5521977776666");

        var account = auth.SignInWithGoogle("google-sub", "ana@agiliz.ia.br");

        account!.Identities.Should().Contain(i => i.Kind == IdentityKind.Google && i.Value == "google-sub" && i.Label == "ana@agiliz.ia.br");
        auth.SignInWithGoogle("google-sub", "trocou@gmail.com")!.Id.Should().Be(account.Id);
        auth.SignInWithGoogle("outro-sub", "ninguem@gmail.com").Should().BeNull();
    }

    // ─── Auxiliares ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("(21) 98888-7777", IdentityKind.WhatsApp, "5521988887777")]
    [InlineData("Paula@Clinica.com.br", IdentityKind.Email, "paula@clinica.com.br")]
    public void Parse_RecognizesPhoneOrEmail(string input, IdentityKind kind, string value) =>
        AuthService.Parse(input).Should().Be((kind, value));

    [Theory]
    [InlineData("98888")]
    [InlineData("paula@")]
    [InlineData("")]
    public void Parse_RejectsGarbage(string input) => AuthService.Parse(input).Should().BeNull();

    [Theory]
    [InlineData("/portal", "/portal")]
    [InlineData("//evil.com", null)]
    [InlineData("https://evil.com", null)]
    [InlineData("/\\evil.com", null)]
    public void SafeReturn_OnlyAllowsLocalPaths(string input, string? expected) => AuthService.SafeReturn(input).Should().Be(expected);

    public void Dispose()
    {
        _runtime.Stop();
        _dir.Dispose();
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
    }
}

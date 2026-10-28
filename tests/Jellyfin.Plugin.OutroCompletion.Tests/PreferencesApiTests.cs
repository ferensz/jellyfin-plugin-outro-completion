using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.OutroCompletion.Api;
using Jellyfin.Plugin.OutroCompletion.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.OutroCompletion.Tests;

public sealed class PreferencesApiTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opencode", "outro-api-tests", Guid.NewGuid().ToString("N"));
    private WebApplication _application = null!;
    private HttpClient _client = null!;
    private string PreferencePath => Path.Combine(_directory, "users.json");

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new SettingsService(() => new PluginConfiguration(), new UserPreferenceStore(PreferencePath)));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(PreferencesController).Assembly);
        _application = builder.Build();
        _application.UsePathBase("/jellyfin");
        _application.UseAuthentication();
        _application.UseAuthorization();
        _application.MapControllers();
        await _application.StartAsync();
        _client = _application.GetTestClient();
    }

    [Fact]
    public async Task PreferenceReadsAndWritesRequireAuthentication()
    {
        using var get = await _client.GetAsync("/OutroCompletion/Me");
        using var put = await _client.PutAsJsonAsync("/OutroCompletion/Me", new { enabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task AnAuthenticatedApiKeyOrInvalidUserCannotChangePreferences(string subject)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", subject);
        using var get = await _client.GetAsync("/OutroCompletion/Me");
        using var put = await _client.PutAsJsonAsync("/OutroCompletion/Me", new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.False(File.Exists(PreferencePath));
    }

    [Fact]
    public async Task UsersCanOnlyReadAndChangeTheirOwnPreferences()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        SignIn(first);
        using var put = await _client.PutAsJsonAsync($"/OutroCompletion/Me?userId={second}", new
        {
            enabled = false,
            requireNextEpisode = true,
            userId = second // Neither query-string nor body IDs are trusted.
        });
        put.EnsureSuccessStatusCode();
        var own = await put.Content.ReadFromJsonAsync<PreferenceResponse>();
        Assert.False(own!.Enabled);
        Assert.True(own.RequireNextEpisode);
        SignIn(second);
        var other = await _client.GetFromJsonAsync<PreferenceResponse>("/OutroCompletion/Me");
        Assert.True(other!.Enabled);
        Assert.Null(other.RequireNextEpisode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"false\"}")]
    [InlineData("{\"enabled\":true,\"requireNextEpisode\":\"yes\"}")]
    public async Task InvalidPreferenceBodiesAreRejected(string json)
    {
        SignIn(Guid.NewGuid());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PutAsync("/OutroCompletion/Me", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResponseUsesStableCamelCaseAndDoesNotCacheUserSettings()
    {
        SignIn(Guid.NewGuid());
        using var response = await _client.GetAsync("/OutroCompletion/Me");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.False(json.GetProperty("defaultRequireNextEpisode").GetBoolean());
        Assert.Equal(120, json.GetProperty("confirmationWindowSeconds").GetInt32());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("/OutroCompletion/Preferences", "text/html")]
    [InlineData("/OutroCompletion/assets/preferences.js", "text/javascript")]
    [InlineData("/OutroCompletion/assets/preferences.css", "text/css")]
    [InlineData("/OutroCompletion/License", "text/plain")]
    [InlineData("/jellyfin/OutroCompletion/Preferences", "text/html")]
    [InlineData("/jellyfin/OutroCompletion/assets/preferences.js", "text/javascript")]
    public async Task AnonymousUsersCanLoadOnlyThePublicPageAndAssets(string path, string mediaType)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        if (mediaType == "text/html")
        {
            var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("script-src 'self'", policy);
            Assert.DoesNotContain("unsafe-inline", policy);
        }
    }

    [Fact]
    public async Task TrailingSlashRedirectPreservesTheServerBaseUrl()
    {
        using var response = await _client.GetAsync("/jellyfin/OutroCompletion/Preferences/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/jellyfin/OutroCompletion/Preferences", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ArbitraryEmbeddedResourcesCannotBeRequested()
    {
        using var response = await _client.GetAsync("/OutroCompletion/assets/config.html");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BrokenPreferenceStorageReturns503WithoutLeakingTheFilePath()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PreferencePath, "broken JSON");
        SignIn(Guid.NewGuid());
        using var response = await _client.GetAsync("/OutroCompletion/Me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(_directory, await response.Content.ReadAsStringAsync());
        Assert.Equal("broken JSON", await File.ReadAllTextAsync(PreferencePath));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _application.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }

    private void SignIn(Guid userId) => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", userId.ToString());

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Test ", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subject = header[5..];
            var claims = subject == "api-key" ? Array.Empty<Claim>() : [new Claim(PreferencesController.UserIdClaim, subject)];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}

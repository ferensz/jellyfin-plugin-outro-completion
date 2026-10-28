using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.OutroCompletion.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OutroCompletion.Api;

[ApiController]
[Route("OutroCompletion")]
public sealed class PreferencesController(SettingsService settings, ILogger<PreferencesController> logger) : ControllerBase
{
    // Jellyfin.Api is not a published plugin dependency. This is the claim emitted
    // by Jellyfin 12.2's authentication handler (InternalClaimTypes.UserId).
    public const string UserIdClaim = "Jellyfin-UserId";

    [HttpGet("Me")]
    [Authorize]
    public ActionResult<PreferenceResponse> GetMine()
    {
        NoCache();
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        try
        {
            return ResponseFor(userId);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return StorageFailure(exception);
        }
    }

    [HttpPut("Me")]
    [Authorize]
    public ActionResult<PreferenceResponse> UpdateMine([FromBody] PreferenceRequest request)
    {
        NoCache();
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        if (request.Enabled is null)
        {
            return BadRequest("enabled is required.");
        }

        try
        {
            settings.SetPreference(userId, new UserPreference
            {
                Enabled = request.Enabled.Value,
                RequireNextEpisode = request.RequireNextEpisode
            });
            return ResponseFor(userId);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return StorageFailure(exception);
        }
    }

    [HttpGet("Preferences")]
    [AllowAnonymous]
    public IActionResult PreferencesPage()
    {
        if (Request.Path.Value?.EndsWith('/') == true)
        {
            return Redirect($"{Request.PathBase}{Request.Path.Value.TrimEnd('/')}");
        }

        Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
            + "base-uri 'none'; form-action 'none'; frame-ancestors 'self'; object-src 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return EmbeddedFile("preferences.html", "text/html; charset=utf-8");
    }

    [HttpGet("assets/preferences.js")]
    [AllowAnonymous]
    public IActionResult PreferencesScript() => EmbeddedFile("preferences.js", "text/javascript; charset=utf-8");

    [HttpGet("assets/preferences.css")]
    [AllowAnonymous]
    public IActionResult PreferencesStyle() => EmbeddedFile("preferences.css", "text/css; charset=utf-8");

    [HttpGet("License")]
    [AllowAnonymous]
    public IActionResult LicenseText() => EmbeddedFile("LICENSE", "text/plain; charset=utf-8");

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out userId) && userId != Guid.Empty;

    private PreferenceResponse ResponseFor(Guid userId)
    {
        var preference = settings.GetPreference(userId);
        var effective = settings.GetForUser(userId);
        return new PreferenceResponse(
            preference.Enabled,
            preference.RequireNextEpisode,
            effective.RequireNextEpisode,
            settings.DefaultRequireNextEpisode,
            settings.ServerEnabled,
            (int)effective.ConfirmationWindow.TotalSeconds);
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;

    private ObjectResult StorageFailure(Exception exception)
    {
        logger.LogError(exception, "Outro Completion user preferences could not be read or saved");
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Preferences are unavailable. Ask the server administrator to check the plugin logs."
        });
    }

    private IActionResult EmbeddedFile(string name, string contentType)
    {
        NoCache();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.OutroCompletion.Web.{name}");
        return stream is null ? NotFound() : File(stream, contentType);
    }

    private void NoCache() => Response.Headers.CacheControl = "no-store";
}

public sealed class PreferenceRequest
{
    [Required]
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("requireNextEpisode")]
    public bool? RequireNextEpisode { get; init; }
}

public sealed record PreferenceResponse(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("requireNextEpisode")] bool? RequireNextEpisode,
    [property: JsonPropertyName("effectiveRequireNextEpisode")] bool EffectiveRequireNextEpisode,
    [property: JsonPropertyName("defaultRequireNextEpisode")] bool DefaultRequireNextEpisode,
    [property: JsonPropertyName("serverEnabled")] bool ServerEnabled,
    [property: JsonPropertyName("confirmationWindowSeconds")] int ConfirmationWindowSeconds);

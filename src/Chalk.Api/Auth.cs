using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Chalk;

public sealed class ApiKeyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Store store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ItemKey = "chalk.principal";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? key = Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            var auth = Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = auth[7..].Trim();
        }
        if (string.IsNullOrEmpty(key)) return AuthenticateResult.NoResult();
        if (!key.StartsWith("chk_", StringComparison.Ordinal)) return AuthenticateResult.Fail("not a Chalk API key");
        var p = await store.AuthenticateAsync(key, Context.RequestAborted);
        if (p is null) return AuthenticateResult.Fail("unknown or revoked API key");
        Context.Items[ItemKey] = p;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, p.UserId), new Claim(ClaimTypes.Name, p.Display)], SchemeName);
        foreach (var s in p.Scopes()) identity.AddClaim(new Claim("scope", s));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "ApiKey realm=\"chalk\", Bearer";
        return WriteError("unauthorized", "pass a Chalk API key in X-Api-Key or Authorization: Bearer");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        return WriteError("forbidden", "this key may not do that");
    }

    private Task WriteError(string code, string message)
    {
        Response.ContentType = "application/json";
        return Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message, path = (string?)null, index = (int?)null } }));
    }
}

public static class PrincipalExtensions
{
    public static Principal Principal(this HttpContext ctx) =>
        ctx.Items[ApiKeyAuthHandler.ItemKey] as Principal ?? throw new ChalkException(401, "unauthorized", "no API key on this request");
}

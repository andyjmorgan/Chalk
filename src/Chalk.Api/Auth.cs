using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chalk;

/// <summary>Keycloak (OIDC) settings. Humans sign in with it; agents use API keys minted by a signed-in user.</summary>
public sealed class OAuthOptions
{
    public const string Section = "OAuth";
    public bool Enabled { get; init; }
    /// <summary>Public issuer, e.g. https://auth.donkeywork.dev/realms/Agents. Tokens must carry this iss.</summary>
    public string Authority { get; init; } = "";
    /// <summary>Optional in-cluster URL for metadata/JWKS/token calls when the public one is not reachable from the pod.</summary>
    public string? InternalAuthority { get; init; }
    public string Audience { get; init; } = "chalk";
    public string ClientId { get; init; } = "chalk";
    public string? ClientSecret { get; init; }
    public bool RequireHttpsMetadata { get; init; } = true;
    /// <summary>Keycloak identity-provider aliases offered on the sign-in screen (kc_idp_hint).</summary>
    public string[] Providers { get; init; } = ["google", "github", "microsoft", "apple"];
    /// <summary>Scopes requested at sign-in and advertised to MCP clients; chalk-audience puts aud=chalk in the token.</summary>
    public string[] Scopes { get; init; } = ["openid", "profile", "email", "offline_access", "roles", "chalk-audience"];
    public string TokenEndpoint => $"{(InternalAuthority ?? Authority).TrimEnd('/')}/protocol/openid-connect/token";
    public string AuthorizeEndpoint => $"{Authority.TrimEnd('/')}/protocol/openid-connect/auth";
    public string LogoutEndpoint => $"{Authority.TrimEnd('/')}/protocol/openid-connect/logout";
}

public static class ChalkAuth
{
    public const string Smart = "Smart";
    public const string ItemKey = "chalk.principal";

    /// <summary>X-Api-Key or a chk_ bearer goes to the API-key handler; any other bearer is a Keycloak JWT.</summary>
    public static string Select(HttpContext ctx, bool oauthEnabled)
    {
        if (ctx.Request.Headers.ContainsKey("X-Api-Key")) return ApiKeyAuthHandler.SchemeName;
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && auth[7..].TrimStart().StartsWith("chk_", StringComparison.Ordinal))
            return ApiKeyAuthHandler.SchemeName;
        return oauthEnabled ? JwtBearerDefaults.AuthenticationScheme : ApiKeyAuthHandler.SchemeName;
    }

    /// <summary>Scheme://host as the browser sees it. cloudflared/Traefik hand us plain HTTP with X-Forwarded-* set.</summary>
    public static string ExternalBase(HttpContext ctx)
    {
        var proto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        return $"{(string.IsNullOrEmpty(proto) ? ctx.Request.Scheme : proto)}://{(string.IsNullOrEmpty(host) ? ctx.Request.Host.Value : host)}";
    }

    public static string Challenge(HttpContext ctx) =>
        $"Bearer realm=\"chalk\", resource_metadata=\"{ExternalBase(ctx)}/.well-known/oauth-protected-resource\", ApiKey realm=\"chalk\"";

    public static Task WriteError(HttpResponse res, int status, string code, string message)
    {
        res.StatusCode = status;
        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message, path = (string?)null, index = (int?)null } }));
    }

    public static void ConfigureJwt(JwtBearerOptions o, OAuthOptions oauth)
    {
        o.Authority = oauth.InternalAuthority ?? oauth.Authority;
        o.RequireHttpsMetadata = oauth.RequireHttpsMetadata;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = oauth.Authority,
            ValidateAudience = true,
            NameClaimType = "preferred_username",
            RoleClaimType = "roles",
            // aud=chalk comes from the chalk-audience client scope; azp=chalk covers tokens minted for our own client.
            AudienceValidator = (auds, token, _) =>
                auds.Contains(oauth.Audience, StringComparer.Ordinal) ||
                (token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("azp", out var azp) && string.Equals(azp, oauth.ClientId, StringComparison.Ordinal)),
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var user = ctx.Principal!;
                var sub = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(sub)) { ctx.Fail("token has no sub"); return Task.CompletedTask; }
                var username = user.FindFirstValue("preferred_username") ?? user.FindFirstValue("email") ?? sub;
                var p = new Principal(sub, $"user:{username}", true, true, true, true, null)
                {
                    Name = user.FindFirstValue("name") ?? username,
                    Email = user.FindFirstValue("email"),
                    Picture = user.FindFirstValue("picture"),
                };
                ctx.HttpContext.Items[ItemKey] = p;
                if (user.Identity is ClaimsIdentity id)
                {
                    id.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
                    foreach (var s in p.Scopes()) id.AddClaim(new Claim("scope", s));
                }
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.Headers.WWWAuthenticate = Challenge(ctx.HttpContext);
                var why = ctx.AuthenticateFailure is null ? "sign in, or pass a Chalk API key in X-Api-Key or Authorization: Bearer" : "token rejected: " + ErrorText.Brief(ctx.AuthenticateFailure.Message);
                return WriteError(ctx.Response, 401, "unauthorized", why);
            },
            OnForbidden = ctx => WriteError(ctx.Response, 403, "forbidden", "you may not do that"),
        };
    }
}

/// <summary>First sentence of an IdentityModel error (they run to pages), without the token or key dumps.</summary>
file static class ErrorText
{
    public static string Brief(string m)
    {
        var cut = m.IndexOfAny(['\n', '\r']); if (cut > 0) m = m[..cut];
        var dot = m.IndexOf(". ", StringComparison.Ordinal); if (dot > 0) m = m[..(dot + 1)];
        return m.Length > 160 ? m[..160] + "…" : m;
    }
}

public sealed class ApiKeyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Store store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

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
        if (p is null) return AuthenticateResult.Fail("unknown, revoked or expired API key");
        Context.Items[ChalkAuth.ItemKey] = p;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, p.UserId), new Claim(ClaimTypes.Name, p.Display)], SchemeName);
        foreach (var s in p.Scopes()) identity.AddClaim(new Claim("scope", s));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = ChalkAuth.Challenge(Context);
        return ChalkAuth.WriteError(Response, 401, "unauthorized", "unknown, revoked or expired API key");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ChalkAuth.WriteError(Response, 403, "forbidden", "this key may not do that");
}

public static class PrincipalExtensions
{
    public static Principal Principal(this HttpContext ctx) =>
        ctx.Items[ChalkAuth.ItemKey] as Principal ?? throw new ChalkException(401, "unauthorized", "no credentials on this request");
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Chalk;

/// <summary>
/// Browser sign-in via Keycloak (authorization code + PKCE, verifier kept in an HttpOnly cookie, tokens handed to the SPA
/// in a URL fragment) plus the RFC 9728 / RFC 8414 discovery documents MCP clients use to find the authorization server.
/// </summary>
public static class OAuthEndpoints
{
    private const string Cookie = "chalk_pkce";

    public static void MapChalkOAuth(this WebApplication app, OAuthOptions oauth)
    {
        app.MapGet("/api/v1/auth/config", (HttpContext ctx) => Results.Json(new
        {
            enabled = oauth.Enabled,
            providers = oauth.Enabled ? oauth.Providers : [],
            authority = oauth.Enabled ? oauth.Authority : null,
            clientId = oauth.Enabled ? oauth.ClientId : null,
            resource = ChalkAuth.ExternalBase(ctx),
        }, Json.Options)).AllowAnonymous();

        app.MapGet("/api/v1/auth/login", (HttpContext ctx, string? idpHint) =>
        {
            if (!oauth.Enabled) return Results.Json(new { error = new { code = "oauth_disabled", message = "sign-in is not configured; use an API key" } }, Json.Options, statusCode: 503);
            var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
            var state = B64Url(RandomNumberGenerator.GetBytes(16));
            var external = ChalkAuth.ExternalBase(ctx);
            ctx.Response.Cookies.Append(Cookie, $"{verifier}.{state}", new CookieOptions
            {
                HttpOnly = true, Secure = external.StartsWith("https://", StringComparison.Ordinal), SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/api/v1/auth",
            });
            var url = oauth.AuthorizeEndpoint +
                      $"?client_id={Uri.EscapeDataString(oauth.ClientId)}&response_type=code" +
                      $"&scope={Uri.EscapeDataString(string.Join(' ', oauth.Scopes))}" +
                      $"&redirect_uri={Uri.EscapeDataString(external + "/api/v1/auth/callback")}" +
                      $"&state={state}&code_challenge={B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))}&code_challenge_method=S256";
            if (!string.IsNullOrWhiteSpace(idpHint) && oauth.Providers.Contains(idpHint, StringComparer.Ordinal)) url += $"&kc_idp_hint={Uri.EscapeDataString(idpHint)}";
            return Results.Redirect(url);
        }).AllowAnonymous();

        app.MapGet("/api/v1/auth/callback", async (HttpContext ctx, IHttpClientFactory http, string? code, string? state, string? error, string? error_description, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error)) return Results.Redirect("/#error=" + Uri.EscapeDataString(error_description ?? error));
            if (!oauth.Enabled || string.IsNullOrEmpty(code) || !ctx.Request.Cookies.TryGetValue(Cookie, out var cookie) || cookie.Split('.') is not [var verifier, var expected] || expected != state)
                return Results.Redirect("/#error=" + Uri.EscapeDataString("the sign-in handshake did not match; try again"));
            ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/api/v1/auth" });
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = oauth.ClientId, ["code"] = code,
                ["redirect_uri"] = ChalkAuth.ExternalBase(ctx) + "/api/v1/auth/callback", ["code_verifier"] = verifier,
            };
            if (!string.IsNullOrEmpty(oauth.ClientSecret)) fields["client_secret"] = oauth.ClientSecret;
            var res = await http.CreateClient("oauth").PostAsync(oauth.TokenEndpoint, new FormUrlEncodedContent(fields), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return Results.Redirect("/#error=" + Uri.EscapeDataString("token exchange failed (" + (int)res.StatusCode + ")"));
            return Results.Redirect("/#token_response=" + Uri.EscapeDataString(body));
        }).AllowAnonymous();

        app.MapPost("/api/v1/auth/refresh", async (IHttpClientFactory http, [FromBody] JsonNode body, CancellationToken ct) =>
        {
            var rt = (body as JsonObject)?["refreshToken"]?.GetValue<string>();
            if (!oauth.Enabled || string.IsNullOrEmpty(rt)) return Results.Json(new { error = new { code = "bad_request", message = "refreshToken is required" } }, Json.Options, statusCode: 400);
            var fields = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = oauth.ClientId, ["refresh_token"] = rt };
            if (!string.IsNullOrEmpty(oauth.ClientSecret)) fields["client_secret"] = oauth.ClientSecret;
            var res = await http.CreateClient("oauth").PostAsync(oauth.TokenEndpoint, new FormUrlEncodedContent(fields), ct);
            return Results.Text(await res.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)res.StatusCode);
        }).AllowAnonymous();

        app.MapGet("/api/v1/auth/logout", (HttpContext ctx, string? idTokenHint) =>
        {
            var home = ChalkAuth.ExternalBase(ctx) + "/";
            if (!oauth.Enabled) return Results.Redirect(home);
            var url = oauth.LogoutEndpoint + $"?client_id={Uri.EscapeDataString(oauth.ClientId)}&post_logout_redirect_uri={Uri.EscapeDataString(home)}";
            if (!string.IsNullOrEmpty(idTokenHint)) url += "&id_token_hint=" + Uri.EscapeDataString(idTokenHint);
            return Results.Redirect(url);
        }).AllowAnonymous();

        // RFC 9728 protected-resource metadata. Served at the root and at the /mcp path form so either lookup style works.
        IResult Resource(HttpContext ctx)
        {
            var b = ChalkAuth.ExternalBase(ctx);
            return Results.Json(new
            {
                resource = b,
                authorization_servers = oauth.Enabled ? new[] { oauth.Authority } : [],
                bearer_methods_supported = new[] { "header" },
                scopes_supported = oauth.Enabled ? oauth.Scopes : [],
                resource_name = "Chalk",
                resource_documentation = b + "/openapi/v1.json",
            }, Json.Options);
        }
        app.MapGet("/.well-known/oauth-protected-resource", Resource).AllowAnonymous();
        app.MapGet("/.well-known/oauth-protected-resource/mcp", Resource).AllowAnonymous();
        if (oauth.Enabled)
        {
            var oidc = oauth.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
            app.MapGet("/.well-known/oauth-authorization-server", () => Results.Redirect(oidc, permanent: true)).AllowAnonymous();
            app.MapGet("/.well-known/openid-configuration", () => Results.Redirect(oidc, permanent: true)).AllowAnonymous();
        }
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

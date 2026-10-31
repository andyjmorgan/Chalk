using System.Text.Json;
using System.Text.Json.Serialization;
using Chalk;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var cs = builder.Configuration.GetConnectionString("Chalk")
         ?? builder.Configuration["Chalk:ConnectionString"]
         ?? throw new InvalidOperationException("ConnectionStrings__Chalk is required");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(cs).Build());
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Embedder>();
builder.Services.AddHttpClient("ollama", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHostedService<Sweeper>();
builder.Services.AddHttpContextAccessor();
var oauth = builder.Configuration.GetSection(OAuthOptions.Section).Get<OAuthOptions>() ?? new OAuthOptions();
if (oauth.Enabled && string.IsNullOrWhiteSpace(oauth.Authority)) throw new InvalidOperationException("OAuth__Authority is required when OAuth__Enabled=true");
builder.Services.AddSingleton(oauth);
builder.Services.AddHttpClient("oauth", c => c.Timeout = TimeSpan.FromSeconds(20));
var auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = ChalkAuth.Smart;
        o.DefaultAuthenticateScheme = ChalkAuth.Smart;
        o.DefaultChallengeScheme = ChalkAuth.Smart;
        o.DefaultForbidScheme = ChalkAuth.Smart;
    })
    .AddPolicyScheme(ChalkAuth.Smart, ChalkAuth.Smart, o => o.ForwardDefaultSelector = ctx => ChalkAuth.Select(ctx, oauth.Enabled))
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null);
if (oauth.Enabled) auth.AddJwtBearer(o => ChalkAuth.ConfigureJwt(o, oauth));
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var publicUrl = builder.Configuration["Chalk:PublicUrl"] ?? "https://chalk.andrewmorgan.ai";
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "Chalk", Title = "Chalk", Version = "0.1.0", WebsiteUrl = publicUrl };
        o.ServerInstructions =
            "Chalk holds short-lived records for agents. A board is a named set of JSON records under a schema; each record is keyed by values " +
            "inside it (the board's key pointers, joined with '/'), revisioned on every change, and expires after the board's TTL unless rewritten. " +
            "Use describe_board before the first write to a board. Write with upsert (bulk, atomic; identical values only refresh expiry). " +
            "Dedup with get(keys[]). At the start of a run call changes_since(board, lastRunTime) to learn what was created, changed, expired or deleted. " +
            "Use search for near-duplicates; it returns scored candidates and never blocks a write. Read the skill at skill://chalk/SKILL.md for the full method.";
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<Store>();
    await store.EnsureSchemaAsync(CancellationToken.None);
    var bootstrapKey = app.Configuration["Chalk:BootstrapApiKey"];
    if (!string.IsNullOrEmpty(bootstrapKey))
        await store.BootstrapKeyAsync(app.Configuration["Chalk:BootstrapUser"] ?? "andy", bootstrapKey, CancellationToken.None);
}

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Chalk");
app.Use((ctx, next) => RestEndpoints.ErrorEnvelope(ctx, next, log));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Json(new { ok = true, service = "chalk", version = "0.1.0" })).AllowAnonymous();
app.MapOpenApi().AllowAnonymous();
app.MapChalkOAuth(oauth);
app.MapChalkRest();
app.MapMcp("/mcp").RequireAuthorization();

app.Run();

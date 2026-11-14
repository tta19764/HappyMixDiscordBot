using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using HappyMix.Application;
using HappyMix.Bot.Configuration;
using HappyMix.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddBot(builder.Configuration);
var web = builder.Configuration.GetSection("Dashboard");
bool enabled = web.GetValue<bool>("Enabled");
if (enabled)
{
    if (web["KeyDirectory"] is { Length: > 0 } keyDirectory)
        builder.Services.AddDataProtection()
            .SetApplicationName("HappyMix.Dashboard")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

    builder.Services.AddControllersWithViews();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        })
        .AddOAuth("Discord", options =>
        {
            options.ClientId = web["ClientId"] ?? "";
            options.ClientSecret = web["ClientSecret"] ?? "";
            options.CallbackPath = "/signin-discord";
            options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
            options.TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
            options.UserInformationEndpoint = "https://discord.com/api/v10/users/@me";
            options.Scope.Add("identify");
            options.UsePkce = true;
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "username");
            options.Events.OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                context.RunClaimActions(profile.RootElement);
            };
        });
    if (string.IsNullOrWhiteSpace(web["ClientId"]) || string.IsNullOrWhiteSpace(web["ClientSecret"]))
        throw new InvalidOperationException("Dashboard Discord OAuth credentials are required when enabled.");

    if (System.Net.IPAddress.TryParse(web["TrustedProxy"], out var proxy))
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Add(proxy);
        });
}

await using var app = builder.Build();
if (enabled)
{
    app.UseForwardedHeaders();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
}

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();

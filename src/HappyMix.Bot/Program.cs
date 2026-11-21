using System.Security.Claims;
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
    builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
    if (web["KeyDirectory"] is { Length: > 0 } keyDirectory)
        builder.Services.AddDataProtection()
            .SetApplicationName("HappyMix.Dashboard")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

    builder.Services.AddControllersWithViews();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            // "Keep me signed in" makes the cookie persistent; either way the sign-in renews while it is used and
            // ends after 30 days without a visit. Without it, the browser drops the cookie when it closes.
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnValidatePrincipal = HappyMix.Bot.Web.DiscordProfileClaims.ValidateAsync;
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
            // Connected accounts, so My games can offer the Steam account already connected to Discord.
            options.Scope.Add("connections");
            options.UsePkce = true;
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "username");
            options.Events.OnCreatingTicket = HappyMix.Bot.Web.DiscordProfileClaims.OnCreatingTicketAsync;
            // Someone who already allowed HappyMix goes straight back without Discord's "Authorize" screen; Discord
            // still asks when the app needs scopes the person hasn't granted yet.
            options.Events.OnRedirectToAuthorizationEndpoint = context =>
            {
                context.Response.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(context.RedirectUri, "prompt", "none"));
                return Task.CompletedTask;
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
    app.UseRequestLocalization(new RequestLocalizationOptions()
        .SetDefaultCulture("en")
        .AddSupportedCultures("en", "uk", "ru")
        .AddSupportedUICultures("en", "uk", "ru"));
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
}

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();

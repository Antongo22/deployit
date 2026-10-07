using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DeployIt.Components;
using DeployIt.Data;
using DeployIt.DTOs;
using DeployIt.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.GetFullPath(builder.Configuration["DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, ".data"));
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection().SetApplicationName("DeployIt")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddDbContextFactory<DeployItDbContext>(options => options.UseSqlite(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
        DataSource = Path.Combine(dataDirectory, "deployit.db"), ForeignKeys = true, DefaultTimeout = 30
    }.ToString()));
builder.Services.AddSingleton(new AdminCredentials(builder.Configuration, dataDirectory));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<GitProviderService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<SshDeploymentService>();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.AddHttpClient("git", client => {
    client.Timeout = TimeSpan.FromSeconds(30); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 256 * 1024);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => {
    options.LoginPath = "/login";
    options.Cookie.Name = "DeployIt.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = context => {
        if (!context.HttpContext.RequestServices.GetRequiredService<AdminCredentials>().IsValid(context.Principal))
            context.RejectPrincipal();
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToLogin = context => {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/_blazor"))
            context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<DeployItDbContext>>().CreateDbContextAsync())
{
    await db.InitializeAsync();
}
app.UseExceptionHandler(handler => handler.Run(async context => {
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error is DomainException domain ? domain.StatusCode : 500;
    await context.Response.WriteAsJsonAsync(new { error = error is DomainException known
        ? known.Message : "Не удалось выполнить запрос. Проверьте журнал приложения." });
}));
app.UseStaticFiles();
app.UseRouting();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.Use(async (context, next) => {
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase)
        && context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect(context.Request.PathBase.Add("/").ToString());
        return;
    }
    if (context.Request.Path.StartsWithSegments("/swagger") && context.User.Identity?.IsAuthenticated != true)
    { await context.ChallengeAsync(); return; }
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Обновите страницу: проверка CSRF не пройдена." }); return;
        }
    }
    await next(context);
});
app.MapOpenApi().RequireAuthorization();
app.UseSwaggerUI(options => {
    options.SwaggerEndpoint("../openapi/v1.json", "DeployIt API v1");
    options.DocumentTitle = "DeployIt API — Swagger";
    options.UseRequestInterceptor("""
        (request) => {
            if (['GET', 'HEAD', 'OPTIONS'].includes((request.method || 'GET').toUpperCase())) return request;
            return fetch(new URL('../api/security/csrf', window.location.href))
                .then(response => response.json())
                .then(data => { request.headers['X-CSRF-TOKEN'] = data.requestToken; return request; });
        }
        """.ReplaceLineEndings(" "));
});
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/api/security/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken })).RequireAuthorization();
app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, AdminCredentials admin) => {
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest("Обновите страницу входа."); }
    var form = await context.Request.ReadFormAsync();
    if (!admin.Check(form["username"].ToString(), form["password"].ToString()))
        return Results.Redirect("/login?error=invalid");
    var expires = DateTimeOffset.UtcNow.AddHours(8);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, admin.Principal(expires),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expires });
    return Results.Redirect("/");
}).AllowAnonymous().RequireRateLimiting("login");
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) => {
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest("Обновите страницу."); }
    await context.SignOutAsync(); return Results.Redirect("/login");
}).RequireAuthorization();
app.MapControllers();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

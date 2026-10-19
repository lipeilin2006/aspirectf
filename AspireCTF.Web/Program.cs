using AspireCTF.Web.Components;
using AspireCTF.Web.Data;
using AspireCTF.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddMudServices();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "aspirectf.auth";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://apiservice";
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

builder.Services.AddScoped<CtfApi>();
builder.Services.AddScoped<TaskRealtime>();
var userConnection = builder.Configuration.GetConnectionString("ctfdb")
    ?? throw new InvalidOperationException("缺少 PostgreSQL 连接串 ConnectionStrings:ctfdb");
builder.Services.AddDbContextFactory<UserDbContext>(options => options.UseNpgsql(userConnection));
builder.Services.AddScoped<UserService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<UserService>().InitializeAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/account/login", async (HttpRequest request, HttpResponse response, UserService users) =>
{
    var form = await request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var powNonce = form["powNonce"].ToString();
    var powCounter = form["powCounter"].ToString();

    if (!IsValidProofOfWork(powNonce, powCounter))
    {
        return Results.Redirect("/login?error=pow");
    }

    var user = await users.AuthenticateAsync(username, password);
    if (user is null)
    {
        return Results.Redirect("/login?error=1");
    }

    var identity = new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.Username),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, user.IsAdmin ? "Admin" : "User")
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await response.HttpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new System.Security.Claims.ClaimsPrincipal(identity));

    return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
}).DisableAntiforgery();

app.MapPost("/account/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/tasks/{id}/writeup/download", async (string id, CtfApi api, CancellationToken ct) =>
{
    var task = await api.GetTaskAsync(id, ct);
    if (task?.HasWriteup != true) return Results.NotFound();
    var md = await api.GetWriteupAsync(id, ct);
    if (string.IsNullOrWhiteSpace(md)) return Results.NotFound();
    var safe = string.Join("_", task.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
    if (safe.Length == 0) safe = id;
    return Results.File(System.Text.Encoding.UTF8.GetBytes(md), "text/markdown", $"{safe}-writeup.md");
}).RequireAuthorization();

app.Run();

static bool IsValidProofOfWork(string nonce, string counterText)
{
    if (string.IsNullOrWhiteSpace(nonce) || !long.TryParse(counterText, out var counter) || counter < 0 || counter > 10_000_000)
        return false;

    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{nonce}:{counter}"));
    return hash[0] == 0 && hash[1] == 0;
}

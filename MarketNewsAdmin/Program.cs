using MarketNewsAdmin.Services;
using MarketNewsApp.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var sqliteConnectionString = Environment.GetEnvironmentVariable("SQLITE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(sqliteConnectionString))
{
    var databasePath = builder.Configuration["Sqlite:Path"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MarketNewsApp", "market-news.db");
    sqliteConnectionString = $"Data Source={Path.GetFullPath(databasePath)}";
}

builder.Services.AddDbContextFactory<MarketNewsDbContext>(options => options.UseSqlite(sqliteConnectionString));
builder.Services.AddScoped<AdminConfigurationService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<PipelineActivityService>();
builder.Services.AddScoped<ReportArchiveService>();
builder.Services.AddSingleton<PipelineRunnerService>();
builder.Services.AddControllersWithViews();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options => options.AddPolicy("Administrators", policy => policy.RequireRole("Administrator")));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MarketNewsDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler("/home/error");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", async (IDbContextFactory<MarketNewsDbContext> factory, CancellationToken cancellationToken) =>
{
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    return await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
});
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");
app.Run();

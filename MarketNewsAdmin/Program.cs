using MarketNewsAdmin.Services;
using MarketNewsApp.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var databasePath = builder.Configuration["Sqlite:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MarketNewsApp", "market-news.db");
builder.Services.AddDbContextFactory<MarketNewsDbContext>(options => options.UseSqlite($"Data Source={Path.GetFullPath(databasePath)}"));
builder.Services.AddScoped<AdminConfigurationService>();
builder.Services.AddScoped<DashboardService>();
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
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");
app.Run();

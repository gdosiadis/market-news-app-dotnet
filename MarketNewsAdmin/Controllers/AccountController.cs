using System.Security.Claims;
using MarketNewsApp.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketNewsAdmin.Controllers;

public sealed class AccountController(IDbContextFactory<MarketNewsDbContext> contextFactory) : Controller
{
    [HttpGet("account/login")]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        await using var db = await contextFactory.CreateDbContextAsync();
        var user = await db.AdminUsers.SingleOrDefaultAsync(candidate => candidate.Username == model.Username && candidate.IsActive);
        var hasher = new PasswordHasher<AdminUser>();
        var initialPassword = Environment.GetEnvironmentVariable("ADMIN_INITIAL_PASSWORD");
        var validInitialLogin = user?.PasswordHash == "SET_AT_FIRST_LOGIN" &&
            !string.IsNullOrEmpty(initialPassword) &&
            string.Equals(model.Password, initialPassword, StringComparison.Ordinal);
        var resetPassword = Environment.GetEnvironmentVariable("ADMIN_RESET_PASSWORD");
        var validPasswordReset = user is not null &&
            !string.IsNullOrEmpty(resetPassword) &&
            string.Equals(model.Password, resetPassword, StringComparison.Ordinal);
        if (user is null || (!validInitialLogin && !validPasswordReset && hasher.VerifyHashedPassword(user, user.PasswordHash, model.Password) == PasswordVerificationResult.Failed))
        {
            ModelState.AddModelError(string.Empty, "The username or password is invalid.");
            return View(model);
        }
        if (validInitialLogin || validPasswordReset)
        {
            user.PasswordHash = hasher.HashPassword(user, model.Password);
            Environment.SetEnvironmentVariable("ADMIN_RESET_PASSWORD", null, EnvironmentVariableTarget.Process);
        }
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var claims = new[] { new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return LocalRedirect(model.ReturnUrl is { Length: > 0 } && Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl : "/");
    }

    [HttpPost("account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("account/access-denied")]
    public IActionResult AccessDenied() => View();
}

public sealed class LoginViewModel
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Username { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
using System.Security.Claims;
using BinanceDataCollector.DataManager.Common.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

/// <summary>
/// Локальный вход для demo-окружения: выбор роли Viewer/Operator/Admin без внешнего IdP.
/// Активен только при Authentication:Mode=Demo; в боевом B2C-режиме все маршруты отдают 404.
/// Роль кладётся в claim ClaimTypes.Role — те же политики (RequireRole) действуют, что и с B2C.
/// </summary>
[AllowAnonymous]
public class DemoAuthController : Controller
{
    private readonly bool _demoEnabled;

    public DemoAuthController(IConfiguration configuration)
    {
        _demoEnabled = string.Equals(
            configuration["Authentication:Mode"], "Demo", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("/demo-login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!_demoEnabled) return NotFound();
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost("/demo-login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string role, string? returnUrl = null)
    {
        if (!_demoEnabled) return NotFound();
        if (!DataManagerRoles.All.Contains(role)) return BadRequest("Unknown role");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, $"demo-{role.ToLowerInvariant()}"),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }

    [HttpGet("/demo-logout")]
    public async Task<IActionResult> Logout()
    {
        if (!_demoEnabled) return NotFound();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/demo-login");
    }
}

using MarketNewsAdmin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketNewsAdmin.Controllers;

[Authorize(Policy = "Administrators")]
public sealed class DashboardController(DashboardService dashboardService) : Controller
{
    public async Task<IActionResult> Index() => View(await dashboardService.GetAsync());
}
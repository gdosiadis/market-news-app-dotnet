using MarketNewsAdmin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketNewsAdmin.Controllers;

[Authorize(Policy = "Administrators")]
public sealed class DashboardController(DashboardService dashboardService, PipelineRunnerService pipelineRunnerService) : Controller
{
    public async Task<IActionResult> Index() => View(await dashboardService.GetAsync());

    [HttpGet]
    public IActionResult ConsoleOutput() => Json(pipelineRunnerService.GetConsole());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RunNow()
    {
        if (!pipelineRunnerService.TryStart())
        {
            TempData["Success"] = "A report pipeline is already running. Follow its progress in Pipeline runs.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = "The report pipeline has started. It will scrape, summarize, and send the email as a --now run.";
        return RedirectToAction(nameof(Index));
    }
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GenesysSftpService.Controllers;

[Authorize]
public class HealthController : Controller
{
    [HttpGet("/health")] 
    public IActionResult Index()
    {
        return View();
    }
}


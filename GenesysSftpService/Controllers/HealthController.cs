using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using GenesysSftpService.Models;
using GenesysSftpService.Services;

namespace GenesysSftpService.Controllers;

[Authorize]
public class HealthController : Controller
{
    private readonly AppDbContext _db;
    private readonly IRecordingDownloader _downloader;
    private readonly ISftpUploader _uploader;
    private readonly ProcessingOptions _options;

    public HealthController(AppDbContext db, IRecordingDownloader downloader, ISftpUploader uploader, IOptions<ProcessingOptions> options)
    {
        _db = db;
        _downloader = downloader;
        _uploader = uploader;
        _options = options.Value;
    }

    [HttpGet("/health")] 
    public async Task<IActionResult> Index()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.ToDateTime(TimeOnly.MinValue);
        var end = today.ToDateTime(TimeOnly.MaxValue);
        var postedCount = await _db.GenesysConversations
            .Where(c => c.IsPosted && c.ConversationEnd >= start && c.ConversationEnd <= end)
            .CountAsync();
        ViewData["PostedCount"] = postedCount;
        return View();
    }

    [HttpPost("/health/download")] 
    public async Task<IActionResult> DownloadAndPost(string callId)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            TempData["Msg"] = "Please enter a call ID.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var result = await _downloader.DownloadAsync(callId.Trim(), HttpContext.RequestAborted);
            if (result == null)
            {
                TempData["Msg"] = "Download failed.";
                return RedirectToAction(nameof(Index));
            }

            // Determine destination using today's date
            var now = DateTime.UtcNow;
            var monthAbbrev = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(now.Month);
            var destinationFolder = $"Call Recordings/{now.Year}/{now.Month:D2}-{monthAbbrev}";
            await _uploader.UploadAsync(result.Value.filePath, destinationFolder, HttpContext.RequestAborted);

            // Mark as posted if present
            var convo = await _db.GenesysConversations.FirstOrDefaultAsync(c => c.ConversationId == callId);
            if (convo != null)
            {
                convo.IsPosted = true;
                await _db.SaveChangesAsync();
            }
            TempData["Msg"] = "Recording downloaded and posted.";
        }
        catch (Exception ex)
        {
            TempData["Msg"] = "Error: " + ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
}


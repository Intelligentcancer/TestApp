using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using GenesysRecordingPostingUtility.Models;
using GenesysRecordingPostingUtility.Services;

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
            .Where(c => c.isPosted == 1 && c.ConversationEnd >= start && c.ConversationEnd <= end)
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
            var trimmed = callId.Trim();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = cts.Token;

            var convo = await _db.GenesysConversations.FirstOrDefaultAsync(c => c.CallId == trimmed, token);

            var result = await _downloader.DownloadAsync(trimmed, convo?.ConversationEnd, token);
            if (result == null)
            {
                TempData["Msg"] = "Download failed.";
                return RedirectToAction(nameof(Index));
            }

            // Determine destination using conversation end if available, else now
            var stamp = convo?.ConversationEnd ?? DateTime.UtcNow;
            var monthAbbrev = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(stamp.Month);
            var destinationFolder = $"Call Recordings/{stamp.Year}/{stamp.Month:D2}-{monthAbbrev}";
            await _uploader.UploadAsync(result.Value.filePath, destinationFolder, token);

            try
            {
                if (System.IO.File.Exists(result.Value.filePath))
                    System.IO.File.Delete(result.Value.filePath);
            }
            catch (Exception delEx)
            {
                // Best-effort delete
            }

            // Mark as posted if present
            if (convo != null)
            {
                convo.isPosted = 1;
                await _db.SaveChangesAsync(token);
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


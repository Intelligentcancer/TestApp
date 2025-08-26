using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using Microsoft.Extensions.DependencyInjection;

namespace GenesysSftpService.Services;

public class ProcessingOptions
{
    public int IntervalMinutes { get; set; } = 60;
    public string DivisionId { get; set; } = string.Empty;
    public string GenesysApiUser { get; set; } = string.Empty;
    public string GenesysApiSecret { get; set; } = string.Empty;
    public string GenesysRegion { get; set; } = string.Empty;
    public string GenesysRecordingPath { get; set; } = ".";
}

public interface IRecordingDownloader
{
    Task<(string filePath, string fileName)?> DownloadAsync(string conversationId, CancellationToken cancellationToken);
}

public interface ISftpUploader
{
    Task UploadAsync(string localFilePath, string destinationFolder, CancellationToken cancellationToken);
}

public class GenesysRecordingDownloader : IRecordingDownloader
{
    private readonly ILogger<GenesysRecordingDownloader> _logger;
    private readonly ProcessingOptions _options;

    public GenesysRecordingDownloader(ILogger<GenesysRecordingDownloader> logger, IOptions<ProcessingOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<(string filePath, string fileName)?> DownloadAsync(string callId, CancellationToken cancellationToken)
    {
        // TODO: Replace this placeholder with real Genesys download using provided code and credentials.
        _logger.LogInformation("Simulating download for callId: {CallId}", callId);
        var directory = _options.GenesysRecordingPath;
        Directory.CreateDirectory(directory);
        var fileName = callId + ".mp3";
        var filePath = Path.Combine(directory, fileName);
        if (!File.Exists(filePath))
        {
            await File.WriteAllBytesAsync(filePath, Array.Empty<byte>(), cancellationToken);
        }
        return (filePath, fileName);
    }
}

public class RecordingWorker : BackgroundService
{
    private readonly ILogger<RecordingWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ProcessingOptions _options;

    public RecordingWorker(
        ILogger<RecordingWorker> logger,
        IServiceProvider serviceProvider,
        IOptions<ProcessingOptions> options)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing loop failed");
            }

            var delay = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task ProcessOnce(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GenesysSftpService.Models.AppDbContext>();
        var downloader = scope.ServiceProvider.GetRequiredService<IRecordingDownloader>();
        var uploader = scope.ServiceProvider.GetRequiredService<ISftpUploader>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.ToDateTime(TimeOnly.MinValue);
        var end = today.ToDateTime(TimeOnly.MaxValue);

        var eligibleIds = await db.GenesysConvDivs
            .Where(x => x.DivisionId == _options.DivisionId)
            .Select(x => x.ConversationId)
            .ToListAsync(cancellationToken);

        var conversations = await db.GenesysConversations
            .Where(c => eligibleIds.Contains(c.ConversationId)
                        && !c.IsPosted
                        && c.ConversationEnd >= start
                        && c.ConversationEnd <= end)
            .OrderBy(c => c.ConversationEnd)
            .ToListAsync(cancellationToken);

        foreach (var convo in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await downloader.DownloadAsync(convo.ConversationId, cancellationToken);
                if (result == null)
                {
                    _logger.LogWarning("No file downloaded for conversation {ConversationId}", convo.ConversationId);
                    continue;
                }

                await uploader.UploadAsync(result.Value.filePath, "/", cancellationToken);

                convo.IsPosted = true;
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Conversation {ConversationId} posted and updated.", convo.ConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing conversation {ConversationId}", convo.ConversationId);
            }
        }
    }
}


using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PureCloudPlatform.Client.V2.Api;
using PureCloudPlatform.Client.V2.Client;
using PureCloudPlatform.Client.V2.Extensions;
using PureCloudPlatform.Client.V2.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using PC = PureCloudPlatform.Client.V2.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GenesysRecordingPostingUtility.Services
{
    public class ProcessingOptions
    {
        public int IntervalMinutes { get; set; } = 60;
        public string DivisionId { get; set; } = string.Empty;
        public string GenesysApiUser { get; set; } = string.Empty;
        public string GenesysApiSecret { get; set; } = string.Empty;
        public string GenesysRegion { get; set; } = string.Empty;
        public string GenesysRecordingPath { get; set; } = ".";
        public bool ScreenRecordingEnabled { get; set; } = false;
        public string FfmpegPath { get; set; } = "ffmpeg";
        public string GenesysRecordingTempDir { get; set; } = "/tmp/genesys-recording-tmp";
    }

    public interface IRecordingDownloader
    {
        Task<(string filePath, string fileName)?> DownloadAsync(string conversationId, DateTime? conversationEnd, CancellationToken cancellationToken);
    }

    public interface ISftpUploader
    {
        Task UploadAsync(string localFilePath, string destinationFolder, CancellationToken cancellationToken);
    }

    public class GenesysRecordingDownloader : IRecordingDownloader
    {
        private readonly ILogger<GenesysRecordingDownloader> _logger;
        private readonly ProcessingOptions _options;
        private readonly ISftpUploader _uploader;

        public GenesysRecordingDownloader(ILogger<GenesysRecordingDownloader> logger, IOptions<ProcessingOptions> options, ISftpUploader uploader)
        {
            _logger = logger;
            _options = options.Value;
            _uploader = uploader;
        }

        public async Task<(string filePath, string fileName)?> DownloadAsync(string callId, DateTime? conversationEnd, CancellationToken cancellationToken)
        {

            _logger.LogInformation("Simulating download for callId: {CallId}", callId);
            // TODO: Replace this placeholder with real Genesys download using provided code and credentials.
            string filepath = null, filename = null;
            PureCloudRegionHosts region = (PureCloudRegionHosts)Enum.Parse(typeof(PureCloudRegionHosts),
        _options.GenesysRegion);
            PC.Configuration configuration = new PC.Configuration(new ApiClient());
            configuration.ApiClient.setBasePath(region);
            var accessTokenInfo = configuration.ApiClient.PostToken(
       clientId: _options.GenesysApiUser, clientSecret: _options.GenesysApiSecret);
            configuration.AccessToken = accessTokenInfo.AccessToken;
            RecordingApi recordingApi = new RecordingApi(configuration);
            var batchRequestBody = AddConversationRecordingsToBatch(recordingApi, new string[] { callId });
            _logger.LogInformation("Add Conversation Recordings To batch callId: {CallId}", callId);
            if (batchRequestBody.BatchDownloadRequestList == null)
            {
                return null;
            }
            _logger.LogInformation("Using WebClient for callId: {CallId}", callId);
            try
            {
                using (WebClient wc = new WebClient())
                {
                    foreach (var item in batchRequestBody.BatchDownloadRequestList)
                    {
                        PureCloudPlatform.Client.V2.Model.Recording recordings = null;
                        int retryCount = 0;
                        while (recordings == null && retryCount < 7)
                        {
                            recordings = recordingApi.GetConversationRecording(
                                                conversationId: item.ConversationId, recordingId: item.RecordingId,
                                                download: true);
                            if (recordings == null)
                            {
                                _logger.LogInformation("Attempt : {Attempt} failed to get recordings for conversation ID : {ConversationId} and Recording ID : {RecordingId}", (retryCount + 1), item.ConversationId, item.RecordingId);
                                retryCount++;
                                Thread.Sleep(5000);
                            }
                        }
                            if (recordings != null)
                        {
                            if (recordings.MediaUris?.Count > 0)
                            {
                                if (recordings.Media == "audio")
                                {
                                    var uri = new Uri(recordings.MediaUris.Values.First().MediaUri);
                                    _logger.LogInformation("going to download audio recording Conversation ID : {ConversationId} and Recording ID : {RecordingId} with URI : {URI}", item.ConversationId, item.RecordingId, uri);
                                    DownloadRecording(uri, item.ConversationId, conversationEnd, item.RecordingId, wc,
                                    out filepath, out filename);
                                }
                                else if (_options.ScreenRecordingEnabled && string.Equals(recordings.Media, "screen", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        Directory.CreateDirectory(_options.GenesysRecordingTempDir ?? "/tmp");
                                        Directory.CreateDirectory(_options.GenesysRecordingPath ?? ".");

                                        var orderedUris = recordings.MediaUris
                                            .OrderBy(kvp => kvp.Key)
                                            .Select(kvp => new Uri(kvp.Value.MediaUri))
                                            .ToList();

                                        var segmentFiles = new List<string>();
                                        string extension = null;
                                        foreach (var segUri in orderedUris)
                                        {
                                            extension ??= GetExtension(segUri);
                                            if (string.IsNullOrWhiteSpace(extension)) extension = ".mp4";
                                            var tempName = $"{item.ConversationId}_{item.RecordingId}_{segmentFiles.Count:D3}{extension}";
                                            var tempPath = Path.Combine(_options.GenesysRecordingTempDir, tempName);
                                            if (!File.Exists(tempPath))
                                            {
                                                wc.DownloadFile(segUri, tempPath);
                                            }
                                            segmentFiles.Add(tempPath);
                                        }

                                        string safeDate = conversationEnd.HasValue ? conversationEnd.Value.ToString("yyyy-MM-dd_HH-mm-ss") : "NoDate";
                                        var outputName = $"{safeDate}_{item.ConversationId}_{item.RecordingId}_screen_merged{extension}";
                                        var outputPath = Path.Combine(_options.GenesysRecordingPath, outputName);

                                        var listFilePath = Path.Combine(_options.GenesysRecordingTempDir, $"temp_{Guid.NewGuid():N}.txt");
                                        await File.WriteAllLinesAsync(listFilePath, segmentFiles.Select(p => $"file '{p.Replace("\\", "/")}'"), cancellationToken);

                                        var ffmpegArgs = $"-y -f concat -safe 0 -i \"{listFilePath}\" -c copy \"{outputPath}\"";
                                        var success = RunFfmpeg(ffmpegArgs, _options.FfmpegPath);

                                        try { if (File.Exists(listFilePath)) File.Delete(listFilePath); } catch { }
                                        foreach (var seg in segmentFiles)
                                        {
                                            try { if (File.Exists(seg)) File.Delete(seg); } catch { }
                                        }

                                        if (success)
                                        {
                                            var endTime = conversationEnd ?? DateTime.UtcNow;
                                            var year = endTime.Value.Year;
                                            var monthAbbrev = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(endTime.Value.Month);
                                            var monthFolder = $"{endTime.Value.Month:D2}-{monthAbbrev}";
                                            var screenFolder = $"/{year}/{monthFolder}_Screen";
                                            await _uploader.UploadAsync(outputPath, screenFolder, cancellationToken);
                                            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                                            _logger.LogInformation("Screen recording merged and uploaded for {ConversationId}/{RecordingId}", item.ConversationId, item.RecordingId);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("FFmpeg merge failed for screen recording {ConversationId}/{RecordingId}", item.ConversationId, item.RecordingId);
                                        }
                                    }
                                    catch (Exception scrEx)
                                    {
                                        _logger.LogError(scrEx, "Error handling screen recording for {ConversationId}/{RecordingId}", item.ConversationId, item.RecordingId);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogError("Failed to get recordings after 3 attempts for Conversation ID : {ConversationId} and Recording ID : {RecordingId} ", item.ConversationId, item.RecordingId);
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception occurred in Using webclient Exception Message : {Exception} Inner Exceptio : {RecordingId} ", ex.Message.ToString(), ex.InnerException.ToString());
            }
            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(filepath))
                return null;
            return (filepath, filename);
        }

        private bool RunFfmpeg(string arguments, string ffmpegPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogDebug(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogDebug("FFmpeg: {Msg}", e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                var exit = process.ExitCode;
                _logger.LogInformation("FFmpeg exited with code {Code}", exit);
                return exit == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed running ffmpeg");
                return false;
            }
        }
        public BatchDownloadJobSubmission AddConversationRecordingsToBatch(RecordingApi recordingApi,
        IEnumerable<string> conversationIds)
        {
            List<BatchDownloadRequest> batchDownloadRequestList = new List<BatchDownloadRequest>();
            BatchDownloadJobSubmission batchRequestBody = new BatchDownloadJobSubmission();
            foreach (var conversationId in conversationIds)
            {
                List<RecordingMetadata> recordingsData = recordingApi
                    .GetConversationRecordingmetadata(conversationId);
                foreach (var recording in recordingsData)
                {
                    //BatchDownloadRequest batchRequest = new BatchDownloadRequest()
                    //{
                    //    ConversationId = recording.ConversationId,
                    //    RecordingId = recording.Id
                    //};
                    //batchDownloadRequestList.Add(batchRequest);
                    //batchRequestBody.BatchDownloadRequestList = batchDownloadRequestList;
                    //_logger.LogInformation("Added : {ConversationId} to batch request ", recording.ConversationId);


                    if (!string.Equals(recording.Media, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Skipping non-audio recording: {ConversationId} [{Media}]",
                            recording.ConversationId, recording.Media);
                        continue;
                    }

                    BatchDownloadRequest batchRequest = new BatchDownloadRequest()
                    {
                        ConversationId = recording.ConversationId,
                        RecordingId = recording.Id
                    };

                    batchDownloadRequestList.Add(batchRequest);
                    batchRequestBody.BatchDownloadRequestList = batchDownloadRequestList;

                    _logger.LogInformation("Added audio recording: {ConversationId} to batch request",
                        recording.ConversationId);

                }
            }
            return batchRequestBody;
        }
        private void DownloadRecording(Uri uri, string conversationId, DateTime? conversationEnd, string recordingId, WebClient wc, out string filePath, out string filename)
        {
            _logger.LogInformation("Downloading now. Please wait...");
            string genesysRecordingPath = _options.GenesysRecordingPath.ToString();
            string path = genesysRecordingPath; // ".";
            string extension = GetExtension(uri);
            string safeDate = conversationEnd.HasValue
     ? conversationEnd.Value.ToString("yyyy-MM-dd_HH-mm-ss")
     : "NoDate";
            filename = safeDate + "_" + conversationId + "_" + recordingId + extension;
            filePath = Path.Combine(path, filename);
            if (!System.IO.File.Exists(filePath))
                wc.DownloadFile(uri, filePath);
        }
        private static string GetExtension(Uri uri)
        {
            string extension = "";
            if (uri.LocalPath.Length > 0)
            {
                var idx = uri.LocalPath.LastIndexOf('.');
                extension = uri.LocalPath.Substring(idx);
            }

            return extension;
        }

    }

    public class RecordingWorker : BackgroundService
    {
        private readonly ILogger<RecordingWorker> _logger;
        private readonly ProcessingOptions _options;
        private readonly IServiceProvider _serviceProvider;

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
            var db = scope.ServiceProvider.GetRequiredService<GenesysRecordingPostingUtility.Models.AppDbContext>();
            var downloader = scope.ServiceProvider.GetRequiredService<IRecordingDownloader>();


            var uploader = scope.ServiceProvider.GetRequiredService<ISftpUploader>();
            //var today = DateOnly.FromDateTime(DateTime.UtcNow);
            //var start = today.ToDateTime(TimeOnly.MinValue);
            //var end = today.ToDateTime(TimeOnly.MaxValue);

            var targetDate = new DateOnly(2025, 10, 20);  // Year, Month, Day
            var start = targetDate.ToDateTime(TimeOnly.MinValue);
            var end = targetDate.ToDateTime(TimeOnly.MaxValue);

            var eligibleIds = await db.GenesysConvDivs
                .Where(x => x.DivisionId == _options.DivisionId)
                .Select(x => x.ConversationId)
                .ToListAsync(cancellationToken);

            var conversations = await db.GenesysConversations
                .Where(c => eligibleIds.Contains(c.CallId)
                          //  && c.AgentID != "ivr"
                            && c.isPosted == 0
                            && c.ConversationEnd >= start   
                            && c.ConversationEnd <= end)
                .OrderBy(c => c.ConversationEnd)
                .ToListAsync(cancellationToken);

            foreach (var convo in conversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await downloader.DownloadAsync(convo.CallId, convo.ConversationEnd, cancellationToken);
                    if (result == null)
                    {
                        _logger.LogWarning("No file downloaded for conversation {ConversationId}", convo.CallId);
                        continue;
                    }
                    var endTime = convo.ConversationEnd;
                    var year = endTime.Year;
                    var monthAbbrev = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(endTime.Month);
                    var monthFolder = $"{endTime.Month:D2}-{monthAbbrev}";
                    var destinationFolder = $"/{year}/{monthFolder}";
                    await uploader.UploadAsync(result.Value.filePath, destinationFolder, cancellationToken);

                    // Screen recordings are handled inline in downloader when flag is enabled
                    try
                    {
                        if (File.Exists(result.Value.filePath))
                            File.Delete(result.Value.filePath);
                    }
                    catch (Exception delEx)
                    {
                        _logger.LogWarning(delEx, "Failed to delete local file {Path}", result.Value.filePath);
                    }
                    convo.isPosted = 1;
                    convo.PostedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Conversation {ConversationId} posted and updated.", convo.CallId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed processing conversation {ConversationId}", convo.CallId);
                }
            }
        }
    }
}

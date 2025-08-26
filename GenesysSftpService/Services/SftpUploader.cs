using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinSCP;

namespace GenesysSftpService.Services;

public class SftpOptions
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemoteBasePath { get; set; } = "/";
}

public class WinScpSftpUploader : ISftpUploader
{
    private readonly ILogger<WinScpSftpUploader> _logger;
    private readonly SftpOptions _options;

    public WinScpSftpUploader(ILogger<WinScpSftpUploader> logger, IOptions<SftpOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task UploadAsync(string localFilePath, string destinationFolder, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using (var session = new Session())
            {
                var sessionOptions = new WinSCP.SessionOptions
                {
                    Protocol = Protocol.Sftp,
                    HostName = _options.HostName,
                    PortNumber = _options.Port,
                    UserName = _options.UserName,
                    Password = _options.Password,
                    SshHostKeyPolicy = SshHostKeyPolicy.GiveUpSecurityAndAcceptAny,
                };

                session.Open(sessionOptions);

                var transferOptions = new TransferOptions
                {
                    TransferMode = TransferMode.Binary,
                    FilePermissions = null,
                    PreserveTimestamp = false,
                    ResumeSupport = { State = TransferResumeSupportState.Off }
                };

                var remotePath = CombineUnixPaths(_options.RemoteBasePath, destinationFolder);

                EnsureDirectories(session, remotePath);

                var target = CombineUnixPaths(remotePath, Path.GetFileName(localFilePath));

                var result = session.PutFiles(localFilePath, target, false, transferOptions);
                result.Check();

                _logger.LogInformation("Recording posted to SFTP: {Target}", target);
            }
        }, cancellationToken);
    }

    private static void EnsureDirectories(Session session, string fullPath)
    {
        var parts = fullPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var folder in parts)
        {
            path += "/" + folder;
            if (!session.FileExists(path))
            {
                session.CreateDirectory(path);
            }
        }
    }

    private static string CombineUnixPaths(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b ?? string.Empty;
        if (string.IsNullOrEmpty(b)) return a;
        return $"/{(a + "/" + b).Trim('/')}";
    }
}


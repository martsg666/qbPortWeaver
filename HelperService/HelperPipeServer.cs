using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Listens on a named pipe and dispatches privileged session 0 actions requested by the
/// user-session tray app. Runs as a hosted background service inside the helper Windows service.
/// Protocol: one text line per connection, pipe-delimited: action|target|logFilePath.
/// Supported actions: restart (restart the Windows service identified by the provider token)
/// and cycle-adapter (cycle a network adapter; if the adapter name matches a known provider,
/// the corresponding service is also restarted).
/// The log file path is sent per-call so the helper writes into the same log file as the
/// tray app, regardless of which user profile is active.
/// </summary>
internal sealed class HelperPipeServer : BackgroundService
{
    internal const string PipeName = "qbPortWeaverHelper"; // Must match AppConstants.HelperServicePipeName in qbPortWeaver
    private  const string ExpectedLogFileName = "qbPortWeaver.log"; // Must match AppConstants.LogFileName in qbPortWeaver

    private const string ActionRestart      = "restart";       // Must match AutoRecoveryManager.ActionRestart in qbPortWeaver
    private const string ActionCycleAdapter = "cycle-adapter"; // Must match AutoRecoveryManager.ActionCycleAdapter in qbPortWeaver

    private readonly ILogger<HelperPipeServer> _logger;

    public HelperPipeServer(ILogger<HelperPipeServer> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("qbPortWeaver Helper Service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe server error - retrying");
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
        _logger.LogInformation("qbPortWeaver Helper Service stopped");
    }

    private async Task ServeOneConnectionAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        using var pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize:  0,
            outBufferSize: 0,
            security);

        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

        using var reader = new StreamReader(pipe);
        var message = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message)) return;

        // Split on pipe character - avoids ambiguity with colons in Windows paths (e.g. C:\...)
        var parts = message.Split('|', 3);
        if (parts.Length != 3)
        {
            _logger.LogWarning("Received malformed pipe message");
            return;
        }

        var action      = parts[0];
        var target      = parts[1];
        var logFilePath = parts[2];

        // Validate the log file path to prevent a caller-controlled path being written
        // by this SYSTEM-level process to an arbitrary location. We check both the filename
        // and that the directory is under some user's AppData\Local\qbPortWeaver folder.
        if (!Path.GetFileName(logFilePath).Equals(ExpectedLogFileName, StringComparison.OrdinalIgnoreCase)
            || logFilePath.IndexOf(@"\AppData\Local\qbPortWeaver\", StringComparison.OrdinalIgnoreCase) < 0)
        {
            _logger.LogWarning("Rejected unexpected log file path '{Path}'", logFilePath);
            return;
        }

        var logger = new HelperLogger(logFilePath);

        switch (action)
        {
            case ActionRestart:
                string? serviceName = AutoRecovery.FindServiceForToken(target);
                if (serviceName is null)
                {
                    _logger.LogWarning("Rejected restart request for unknown provider token '{Token}'", target);
                    return;
                }
                await AutoRecovery.RestartServiceAsync(serviceName, logger).ConfigureAwait(false);
                break;

            case ActionCycleAdapter:
                if (string.IsNullOrWhiteSpace(target))
                {
                    _logger.LogWarning("Rejected cycle-adapter request with empty adapter name");
                    return;
                }
                await AutoRecovery.CycleAdapterAsync(target, logger).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning("Rejected unknown action '{Action}'", action);
                break;
        }
    }
}

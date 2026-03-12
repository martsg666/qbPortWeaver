using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace qbPortWeaver.HelperService;

// Listens on a named pipe and dispatches privileged session 0 actions requested by the
// user-session tray app. Runs as a hosted background service inside the helper Windows service.
//
// Protocol (one text line per connection):
//   restart:<serviceName>:<logFilePath>
//
// The log file path is sent per-call so the helper writes into the same log file as the
// tray app, regardless of which user profile is active.
internal sealed class HelperPipeServer : BackgroundService
{
    internal const string PipeName = "qbPortWeaverHelper"; // Must match AppConstants.HelperServicePipeName in qbPortWeaver

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

        // Split into exactly 3 parts - the log file path may contain colons (e.g. C:\...)
        var parts = message.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "restart")
        {
            _logger.LogWarning("Received malformed pipe message");
            return;
        }

        var serviceName = parts[1];
        var logFilePath = parts[2];

        await VpnAutoRecovery.RestartServiceAsync(serviceName, new HelperLogger(logFilePath)).ConfigureAwait(false);
    }
}

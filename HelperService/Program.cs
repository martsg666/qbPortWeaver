using qbPortWeaver.HelperService;
using qbPortWeaver.Shared;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = HelperProtocol.PipeName);
builder.Services.AddHostedService<HelperPipeServer>();

try
{
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    try
    {
        System.Diagnostics.EventLog.WriteEntry(
            HelperProtocol.PipeName,
            $"Fatal error: {ex}",
            System.Diagnostics.EventLogEntryType.Error);
    }
    catch { /* Event Log write failed (e.g. source not registered) - nothing left to do */ }
    Environment.Exit(1);
}

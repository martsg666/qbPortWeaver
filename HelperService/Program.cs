using qbPortWeaver.HelperService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = HelperPipeServer.HelperServicePipeName);
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
            HelperPipeServer.HelperServicePipeName,
            $"Fatal error: {ex}",
            System.Diagnostics.EventLogEntryType.Error);
    }
    catch { /* Event Log write failed (e.g. source not registered) - nothing left to do */ }
    Environment.Exit(1);
}

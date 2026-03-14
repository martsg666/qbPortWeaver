using qbPortWeaver.HelperService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = HelperPipeServer.PipeName);
builder.Services.AddHostedService<HelperPipeServer>();

await builder.Build().RunAsync();

using qbPortWeaver.HelperService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "qbPortWeaverHelper");
builder.Services.AddHostedService<HelperPipeServer>();

await builder.Build().RunAsync();

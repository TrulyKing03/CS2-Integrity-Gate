using AcClient.Service;
using AcClient.Service.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AcClientOptions>(builder.Configuration.GetSection(AcClientOptions.SectionName));
builder.Services.AddHttpClient("control-plane");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

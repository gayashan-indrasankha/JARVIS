using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

builder.Configuration.AddEnvironmentVariables(prefix: "JARVIS_");
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffK";
});

builder.Services.AddJarvisInfrastructure(builder.Configuration);
builder.Services.AddHostedService<VoiceConsoleHostedService>();

IHost host = builder.Build();

JarvisOptions options = host.Services.GetRequiredService<IOptions<JarvisOptions>>().Value;
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jarvis.Host");

HostLog.Starting(
    logger,
    options.InstanceName,
    builder.Environment.EnvironmentName);

try
{
    await host.RunAsync();
}
finally
{
    HostLog.Stopped(logger);
    if (host is IAsyncDisposable asyncHost)
    {
        await asyncHost.DisposeAsync();
    }
    else
    {
        host.Dispose();
    }
}

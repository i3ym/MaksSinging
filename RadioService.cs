using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;

namespace MaksSinging;

public sealed class RadioService(GatewayClient Discord, Radio Radio, ILogger<RadioService> Logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting");
        Discord.Ready += async args => await Radio.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken) => Logger.LogInformation("Stopping");
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaksSinging2;

public sealed class RadioBot(ILogger<RadioBot> Logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting");
    }

    public async Task StopAsync(CancellationToken cancellationToken) => Logger.LogInformation("Stopping");
}

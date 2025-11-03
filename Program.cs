using MaksSinging2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Services.ApplicationCommands;


var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<RadioBot>();
builder.Services
    .AddDiscordGateway(options => options.Intents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds)
    .AddApplicationCommands();

var host = builder.Build();
host.AddModules(typeof(Program).Assembly);

await host.RunAsync();


public sealed class ExampleModule : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("mgp", "maks gminag t")]
    public static string Mgt(
        [SlashCommandParameter(Description = "hi")] User user
    )
    {
        return $"pæep {user.Id}";
    }
}

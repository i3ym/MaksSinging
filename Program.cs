using MaksSinging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<Radio>();
builder.Services.AddHostedService<RadioService>();

builder.Services
    .AddDiscordGateway(options => options.Intents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds)
    .AddApplicationCommands();

var host = builder.Build();
host.AddModules(typeof(Program).Assembly);

await host.RunAsync();

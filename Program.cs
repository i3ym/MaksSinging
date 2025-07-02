using System.Collections;
using System.Diagnostics;
using Discord;
using Discord.Audio;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;

using var discord = new DiscordSocketClient(new DiscordSocketConfig() { GatewayIntents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds });
discord.Log += async (log) =>
    Console.WriteLine($"[{DateTimeOffset.Now}] [{log.Severity}] {(object) log.Exception ?? log.Message}");

var player = new SongStreamSourceFromDirectory("source");
var targets = new DiscordTargets(discord);
player.Targets = targets;

discord.Ready += async () => new Thread(async () => await player.Run()) { IsBackground = false }.Start();
discord.Ready += async () => new Thread(async () => await targets.Start()) { IsBackground = false }.Start();
discord.Ready += async () => new Thread(async () => await RunRpcSenderLoop()) { IsBackground = false }.Start();
discord.Ready += async () =>
{
    var command = new SlashCommandBuilder()
        .WithName("mgplay")
        .WithDescription("makss gminag play")
        .AddOption("search", ApplicationCommandOptionType.String, "search string", true);

    try
    {
        await discord.CreateGlobalApplicationCommandAsync(command.Build());
    }
    catch (HttpException exception)
    {
        var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);
        Console.WriteLine(json);
    }
};

discord.SlashCommandExecuted += async command =>
{
    if (command.CommandName == "mgplay")
    {
        var regex = (string) command.Data.Options.First(c => c.Name == "search").Value;

        var dic = Directory.GetFiles("source", $"*{regex}*");
        if (dic.Length == 0)
        {
            await command.RespondAsync("no");
            return;
        }

        var next = dic[0];
        await command.RespondAsync($"ok {Path.GetFileNameWithoutExtension(next)}");
        player.ForcePlayFile(next);
    }
};

await discord.LoginAsync(Discord.TokenType.Bot, File.ReadAllText("discordkey"));
await discord.StartAsync();

await Task.Delay(-1);
// GC.KeepAlive(interactions);


async Task RunRpcSenderLoop()
{
    Task sendRpc(string text) => discord.SetActivityAsync(new Game(Path.GetFileName(text).Split('[', StringSplitOptions.TrimEntries)[0], ActivityType.Listening));

    while (true)
    {
        if (player.CurrentlyPlaying is not null)
            await sendRpc(player.CurrentlyPlaying);

        await Task.Delay(10_000);
    }
}

interface ISongStreamTarget
{
    Task Write(ReadOnlyMemory<byte> data);
    Task Flush();
}
abstract class SongStreamSource
{
    public abstract string? CurrentlyPlaying { get; }

    protected static async Task WriteSongTo(string path, IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation)
    {
        var ffmpeg = File.Exists("ffmpeg") ? "./ffmpeg" : "ffmpeg";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-hide_banner",
                "-i", path,
                "-ac", "2",
                "-ar", "48000",
                "-f", "s16le",
                "pipe:1",
            },
        };
        Console.WriteLine($"Launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        using var ffmpegOutput = proc.StandardOutput.BaseStream;
        cancellation.Register(proc.Kill);

        var buffer = new byte[1024 * 128];
        while (true)
        {
            if (cancellation.IsCancellationRequested)
                break;

            var read = await ffmpegOutput.ReadAsync(buffer, cancellation);
            if (read < 1) break;

            while (!targets.Any())
                await Task.Delay(100, cancellation);

            await Task.WhenAll(targets.Select(c => c.Write(buffer.AsMemory(0, read))));
        }

        if (cancellation.IsCancellationRequested)
            return;

        await proc.WaitForExitAsync(cancellation);
        await Task.WhenAll(targets.Select(c => c.Flush()));
    }
}
class SongStreamSourceFromDirectory : SongStreamSource
{
    public IEnumerable<ISongStreamTarget> Targets { get; set; } = [];
    readonly string DirectoryPath;
    CancellationTokenSource? Cancellation;

    string? _CurrentlyPlaying;
    public override string? CurrentlyPlaying => _CurrentlyPlaying;

    string? NextToPlay;

    public SongStreamSourceFromDirectory(string directory) => DirectoryPath = directory;

    public void ForcePlayFile(string path)
    {
        NextToPlay = path;
        Cancellation?.Cancel();
    }

    public async Task Run()
    {
        Queue<string> getFiles() => new(Directory.GetFiles(DirectoryPath).OrderBy(_ => Guid.NewGuid()));

        while (true)
        {
            try
            {
                var bucket = new Queue<string>();

                while (true)
                {
                    if (bucket.Count == 0)
                        bucket = getFiles();

                    var path = NextToPlay ?? bucket.Dequeue();
                    NextToPlay = null;
                    _CurrentlyPlaying = path;

                    Console.WriteLine($"Playing {path}");
                    Cancellation = new();
                    await WriteSongTo(path, Targets, Cancellation.Token);

                    _CurrentlyPlaying = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine("Restarting?...");
            }
        }
    }
}

class DiscordTargets : IEnumerable<ISongStreamTarget>
{
    public Dictionary<ulong, ISongStreamTarget> Targets { get; } = [];
    readonly DiscordSocketClient Client;

    public DiscordTargets(DiscordSocketClient client) => Client = client;

    public async Task Start()
    {
        var toConnect = new Dictionary<ulong, ulong>() { [1053774759244083280] = 1386284245546307675 };
        foreach (var (g, v) in toConnect)
            await Connect(g, v);
    }
    async Task Connect(ulong guildId, ulong channelId)
    {
        var guild = Client.GetGuild(guildId);
        var channel = guild.GetVoiceChannel(channelId);

        Targets[channelId] = new VoiceChannelState(this, channel);
    }

    public IEnumerator<ISongStreamTarget> GetEnumerator() => ((IReadOnlyDictionary<ulong, ISongStreamTarget>) Targets).Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
class VoiceChannelState : ISongStreamTarget, IAsyncDisposable
{
    readonly DiscordTargets Targets;
    readonly SocketVoiceChannel Channel;
    AudioOutStream? AudioStream;

    public VoiceChannelState(DiscordTargets targets, SocketVoiceChannel channel)
    {
        Targets = targets;
        Channel = channel;
    }

    public async Task Write(ReadOnlyMemory<byte> data)
    {
        if (AudioStream is null)
            await Reconnect();

        if (AudioStream is null) return;

        try { await AudioStream.WriteAsync(data); }
        catch { await Reconnect(); }
    }
    public async Task Flush()
    {
        if (AudioStream is null)
            await Reconnect();

        if (AudioStream is null) return;

        try { await AudioStream.FlushAsync(); }
        catch { await Reconnect(); }
    }

    async Task Reconnect()
    {
        var audioClient = await Channel.ConnectAsync(selfDeaf: true);
        AudioStream = audioClient.CreatePCMStream(Discord.Audio.AudioApplication.Music, 128 * 1024);
    }

    public async ValueTask DisposeAsync()
    {
        Targets.Targets.Remove(Channel.Id);

        try { AudioStream?.Dispose(); }
        catch { }

        try { await Channel.DisconnectAsync(); }
        catch { }
    }
}

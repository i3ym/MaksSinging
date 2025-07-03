using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Discord;
using Discord.Audio;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;

using var discord = new DiscordSocketClient(new DiscordSocketConfig() { GatewayIntents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds });
discord.Log += async (log) =>
    Console.WriteLine($"[{DateTimeOffset.Now}] [{log.Severity}] {(object) log.Exception ?? log.Message}");

var playlist = new Playlist();
var player = new Player(playlist);


// var player = new SongStreamSourceFromDirectory("source");
var targets = new DiscordTargets(discord);
player.Targets = targets;

discord.Ready += async () => new Thread(async () => await player.Run()) { IsBackground = false }.Start();
discord.Ready += async () => new Thread(async () => await targets.Start()) { IsBackground = false }.Start();
discord.Ready += async () => new Thread(async () => await RunRpcSenderLoop()) { IsBackground = false }.Start();
discord.Ready += async () =>
{
    var commands = new[]
    {
        new SlashCommandBuilder()
            .WithName("mgplay")
            .WithDescription("makss gminag play")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("file")
                .WithDescription("from file")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("search", ApplicationCommandOptionType.String, "search string", isRequired: true)
                .AddOption("now", ApplicationCommandOptionType.Boolean, "is now?", isRequired: false)
            )
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("gop")
                .WithDescription("gop fm")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("now", ApplicationCommandOptionType.Boolean, "is now?", isRequired: false)
            ),
        new SlashCommandBuilder()
            .WithName("mgplay_old")
            .WithDescription("makss gminag play")
            .AddOption("search", ApplicationCommandOptionType.String, "search string", isRequired: true),
        new SlashCommandBuilder()
            .WithName("mgskip")
            .WithDescription("makss gminag skip"),
        new SlashCommandBuilder()
            .WithName("mginfo")
            .WithDescription("makss gminag paylist"),
    };

    try
    {
        foreach (var command in commands)
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
    if (command.CommandName == "mgplay_old")
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
        // player.ForcePlayFile(next);
        return;
    }

    if (command.CommandName == "mgplay")
    {
        var typ = command.Data.Options.First();
        if (typ.Name == "file")
        {
            var atStart = (bool) typ.Options.First(c => c.Name == "now").Value;

            var search = (string) typ.Options.First(c => c.Name == "search").Value;
            var files = Directory.GetFiles("source", $"*{search}*");
            if (files.Length == 0)
            {
                await command.RespondAsync("no");
                return;
            }

            var next = FileSongSource.GetFromFile(files[0]);
            playlist.Enqueue(next, atStart);
            await command.RespondAsync($"ok {next.Info}");
            return;
        }
        if (typ.Name == "gop")
        {
            var atStart = (bool) typ.Options.First(c => c.Name == "now").Value;

            playlist.Enqueue(GopFm.Create(), atStart);
            await command.RespondAsync($"ok gop");
            return;
        }

        await command.RespondAsync($"i disagree");
        return;
    }

    if (command.CommandName == "mgskip")
    {
        player.Skip();
        await command.RespondAsync("ok skipped");
        return;
    }

    if (command.CommandName == "mginfo")
    {
        var count = playlist.QueueEnumerable.Count;

        await command.RespondAsync($"""
            -: {playlist.Current?.Info.ToString() ?? "[nothing]"}
            {string.Join('\n', playlist.QueueEnumerable.Take(5).Select((c, i) => $"{i + 1}: {c.Info}"))}
            {(count > 5 ? $"... {count} more" : "")}
            """.TrimEnd());

        return;
    }
};

await discord.LoginAsync(Discord.TokenType.Bot, File.ReadAllText("discordkey"));
await discord.StartAsync();

await Task.Delay(-1);
// GC.KeepAlive(interactions);


async Task RunRpcSenderLoop()
{
    Task sendRpc(string text) =>
        discord.SetActivityAsync(new Game(Path.GetFileName(text).Split('[', StringSplitOptions.TrimEntries)[0], ActivityType.Listening));

    while (true)
    {
        if (player.Playlist.Current is not null)
            await sendRpc(player.Playlist.Current.Info.ToString());

        await Task.Delay(10_000);
    }
}

class SongInfo
{
    public string? Name { get; init; }

    public override string ToString() => $"{Name ?? "<unknown>"}";
}

interface ISongSource
{
    Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation);

    static async Task PlayThroughFFmpeg(IEnumerable<ISongStreamTarget> targets, string source, CancellationToken cancellation)
    {
        var ffmpeg = File.Exists("ffmpeg") ? "./ffmpeg" : "ffmpeg";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-hide_banner",
                "-i", source,
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
class FileSongSource : ISongSource
{
    public static IEnumerable<Song> GetAllFromDirectoryRandomlyOrdered(string directory) =>
        Directory.GetFiles(directory).Select(GetFromFile).OrderBy(_ => Guid.NewGuid());
    public static Song GetFromFile(string file) =>
        new Song(new SongInfo() { Name = Path.GetFileNameWithoutExtension(file).Split('[', StringSplitOptions.TrimEntries)[0] }, new FileSongSource(file));

    readonly string FilePath;

    public FileSongSource(string path) => FilePath = path;

    public Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation) =>
        ISongSource.PlayThroughFFmpeg(targets, FilePath, cancellation);
}
class GopFm : ISongSource
{
    public static Song Create() => new Song(new SongInfo() { Name = "Gop FM" }, new GopFm());

    public async Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation)
    {
        var url = "https://hls-01-radiorecord.hostingradio.ru/record-gop/112/playlist.m3u8";
        await ISongSource.PlayThroughFFmpeg(targets, url, cancellation);
    }
}

record Song(SongInfo Info, ISongSource Source);
class Playlist
{
    public IReadOnlyCollection<Song> QueueEnumerable => Queue;
    public Song? Current { get; private set; }
    readonly List<Song> Queue = [];

    public void Enqueue(Song song, bool atStart = false)
    {
        if (atStart) Queue.Insert(0, song);
        else Queue.Add(song);
    }

    public void Enqueue(bool atStart, params IEnumerable<Song> songs)
    {
        if (atStart) Queue.InsertRange(0, songs);
        else Queue.AddRange(songs);
    }

    public Song? Next()
    {
        if (Queue.Count == 0)
            return Current = null;

        var song = Queue[0];
        Queue.RemoveAt(0);
        return Current = song;
    }
}

class Player
{
    public IEnumerable<ISongStreamTarget> Targets { get; set; } = [];
    public Playlist Playlist { get; }
    CancellationTokenSource? Cancellation;

    public Player(Playlist playlist) => Playlist = playlist;

    public void Skip() => Cancellation?.Cancel();

    public async Task Run()
    {
        while (true)
        {
            try
            {
                var song = Playlist.Next();
                if (song is null)
                {
                    Playlist.Enqueue(false, FileSongSource.GetAllFromDirectoryRandomlyOrdered("source"));
                    song = Playlist.Next();

                    if (song is null)
                    {
                        await Task.Delay(1000);
                        continue;
                    }
                }

                Console.WriteLine($"Playing {song.Info}");
                Cancellation = new();
                await song.Source.Play(Targets, Cancellation.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}

interface ISongStreamTarget
{
    Task Write(ReadOnlyMemory<byte> data);
    Task Flush();
}

[Obsolete]
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

[Obsolete]
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

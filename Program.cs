using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Discord;
using Discord.Audio;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using var discord = new DiscordSocketClient(new DiscordSocketConfig() { GatewayIntents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds });
discord.Log += async (log) =>
    Console.WriteLine($"[{DateTimeOffset.Now}] [{log.Severity}] {(object) log.Exception ?? log.Message}");

var playlist = new Playlist();
var player = new Player(playlist);

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
    if (command.CommandName == "mgplay")
    {
        async Task enqueueRespond(Song song, bool atStart)
        {
            playlist.Enqueue(song, atStart);
            await command.RespondAsync($"added at position {(atStart ? 1 : playlist.Count)}: {song.Info}");
        }


        var typ = command.Data.Options.First();
        if (typ.Name == "file")
        {
            var atStart = (bool) (typ.Options.FirstOrDefault(c => c.Name == "now")?.Value ?? false);

            var search = (string) typ.Options.First(c => c.Name == "search").Value;
            var files = Directory.GetFiles("source", $"*{search}*");
            if (files.Length == 0)
            {
                await command.RespondAsync("no");
                return;
            }

            var next = FileSongSource.GetFromFile(files[0]);
            await enqueueRespond(next, atStart);
            return;
        }
        if (typ.Name == "gop")
        {
            var atStart = (bool) (typ.Options.FirstOrDefault(c => c.Name == "now")?.Value ?? false);
            await enqueueRespond(GopFm.Create(), atStart);
            return;
        }
        if (typ.Name == "maks")
        {
            playlist.Enqueue(FileSongSource.GetFromFile("source/maks.mp3"), true);
            await command.RespondAsync("ok maks");
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
        var embed1 = new EmbedBuilder()
            .WithTitle("current")
            .WithDescription($"""
                {playlist.Current?.Source.Progress?.ToString() ?? ""}
                {playlist.Current?.Info?.ToString() ?? "[waiting]"}
                """.Trim());
        var embed2 = new EmbedBuilder()
            .WithTitle("queue")
            .WithDescription($"""
                {string.Join('\n', playlist.QueueEnumerable.Take(5).Select((c, i) => $"{i + 1}: {c.Info}"))}
                {(playlist.QueueEnumerable.Count > 5 ? $"... {playlist.QueueEnumerable.Count} more" : "")}
                """.Trim())
            .WithFooter("maks_singing")
            .WithCurrentTimestamp();

        await command.RespondAsync(embeds: [embed1.Build(), embed2.Build()]);
        return;
    }
};

await discord.LoginAsync(Discord.TokenType.Bot, File.ReadAllText("discordkey").Trim());
await discord.StartAsync();

await Task.Delay(-1);

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


interface ISongProgress
{
    string ToString();
}
sealed class SongProgress : ISongProgress
{
    public TimeSpan Length { get; set; }
    public TimeSpan Progress { get; set; }

    string ToProgressBar(int length)
    {
        var progress = (int) ((Progress / Length) * length);
        return $"{new string('#', progress)}{new string('-', length - progress)}";
    }

    public override string ToString() =>
        $"{Progress.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)} `{ToProgressBar(20)}` {Length.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}";
}
sealed class SongProgressInfinite : ISongProgress
{
    public static readonly SongProgressInfinite Instance = new();

    SongProgressInfinite() { }

    public override string ToString() => $"[infinite]";
}

record ProgressSetter(Action<TimeSpan> SetProgress);

class SongInfo
{
    public string? Name { get; init; }

    public override string ToString() => $"{Name ?? "<unknown>"}";
}

interface ISongSource
{
    ISongProgress Progress { get; }

    Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation);

    const int DefaultSampleRate = 48000;
    const int DefaultChannelCount = 2;
    const int DefaultBps = 16;

    static async Task PlayFromPcmStream(IEnumerable<ISongStreamTarget> targets, Stream source, ProgressSetter? progressSetter, CancellationToken cancellation, int ar = DefaultSampleRate, int ac = DefaultChannelCount, int bits = DefaultBps, int bufferms = 100)
    {
        var bps = ar * ac * bits / 8;
        var div = 1000d / bufferms;
        if ((int) div != div)
            throw new InvalidOperationException("Can't have non-integer amount of bytes in the buffer");
        if ((int) (bps / div) != (bps / div))
            throw new InvalidOperationException("Can't have non-integer amount of bytes in the buffer");
        bps = (int) (bps / div);
        Console.WriteLine($"Playing stream {source} with {JsonConvert.SerializeObject(new { ar, ac, bits, bufferms })}");


        var progress = TimeSpan.Zero;
        var buffer = new byte[bps];
        while (true)
        {
            if (cancellation.IsCancellationRequested)
                break;

            await source.ReadExactlyAsync(buffer, cancellation);

            progress += TimeSpan.FromMilliseconds(bufferms);
            progressSetter?.SetProgress(progress);

            await Task.WhenAll(targets.Select(c => c.Write(buffer)));
            await Task.Delay(bufferms);
        }
    }
    static async Task PlayThroughFFmpeg(IEnumerable<ISongStreamTarget> targets, string source, ProgressSetter? progressSetter, CancellationToken cancellation)
    {
        var ffmpeg = File.Exists("ffmpeg") ? "./ffmpeg" : "ffmpeg";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-hide_banner",
                "-i", source,
                "-ar", $"{DefaultSampleRate}",
                "-ac", $"{DefaultChannelCount}",
                "-f", $"s{DefaultBps}le",
                "pipe:1",
            },
        };
        Console.WriteLine($"Launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        using var ffmpegOutput = proc.StandardOutput.BaseStream;
        cancellation.Register(proc.Kill);

        await PlayFromPcmStream(targets, ffmpegOutput, progressSetter, cancellation, DefaultSampleRate, DefaultChannelCount, DefaultBps);
        await proc.WaitForExitAsync(cancellation);
        await Task.WhenAll(targets.Select(c => c.Flush()));
    }
    static async Task<TimeSpan?> FFProbeGetDuration(string source)
    {
        var ffprobe = File.Exists("ffprobe") ? "./ffprobe" : "ffprobe";
        var psi = new ProcessStartInfo(ffprobe)
        {
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-hide_banner",
                "-show_streams",
                "-show_format",
                "-print_format", "json",
                "-v", "quiet",
                source,
            },
        };
        Console.WriteLine($"Launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        var json = await proc.StandardOutput.ReadToEndAsync();

        var duration = JObject.Parse(json)?["format"]?["duration"]?.Value<double>();
        if (duration is { } dur)
            return TimeSpan.FromSeconds(dur);

        return null;
    }
}
class FileSongSource : ISongSource
{
    public static IEnumerable<Song> GetAllFromDirectoryRandomlyOrdered(string directory) =>
        Directory.GetFiles(directory).Select(GetFromFile).OrderBy(_ => Guid.NewGuid());
    public static Song GetFromFile(string file) =>
        new Song(new SongInfo() { Name = Path.GetFileNameWithoutExtension(file).Split('[', StringSplitOptions.TrimEntries)[0] }, new FileSongSource(file));

    ISongProgress ISongSource.Progress => Progress;
    readonly SongProgress Progress = new();
    readonly string FilePath;

    public FileSongSource(string path) => FilePath = path;

    public Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation)
    {
        _ = ISongSource.FFProbeGetDuration(FilePath)
            .ContinueWith(result =>
            {
                if (result is { IsCompletedSuccessfully: true, Result: { } duration })
                    Progress.Length = duration;
            }, cancellation);

        var ps = new ProgressSetter(progress => Progress.Progress = progress);
        return ISongSource.PlayThroughFFmpeg(targets, FilePath, ps, cancellation);
    }
}
class GopFm : ISongSource
{
    public static Song Create() => new Song(new SongInfo() { Name = "Gop FM" }, new GopFm());

    ISongProgress ISongSource.Progress => SongProgressInfinite.Instance;

    public async Task Play(IEnumerable<ISongStreamTarget> targets, CancellationToken cancellation)
    {
        var url = "https://hls-01-radiorecord.hostingradio.ru/record-gop/112/playlist.m3u8";
        await ISongSource.PlayThroughFFmpeg(targets, url, null, cancellation);
    }
}

record Song(SongInfo Info, ISongSource Source);
class Playlist
{
    public int Count => Queue.Count;
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

        Targets[channelId] = new VoiceChannelState(channel);
    }

    public IEnumerator<ISongStreamTarget> GetEnumerator() => ((IReadOnlyDictionary<ulong, ISongStreamTarget>) Targets).Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
class VoiceChannelState : ISongStreamTarget
{
    readonly SocketVoiceChannel Channel;
    AudioOutStream? AudioStream;

    public VoiceChannelState(SocketVoiceChannel channel) => Channel = channel;

    public async Task Write(ReadOnlyMemory<byte> data)
    {
        try
        {
            if (AudioStream is null)
                await Reconnect();

            if (AudioStream is null)
                return;

            try { await AudioStream.WriteAsync(data); }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await Reconnect();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
    public async Task Flush()
    {
        try
        {
            if (AudioStream is null)
                await Reconnect();

            if (AudioStream is null) return;

            try { await AudioStream.FlushAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await Reconnect();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    async Task Reconnect()
    {
        var audioClient = await Channel.ConnectAsync(selfDeaf: true);
        AudioStream = audioClient.CreateDirectPCMStream(Discord.Audio.AudioApplication.Music, 128 * 1024);
        await audioClient.SetSpeakingAsync(true);
    }
}

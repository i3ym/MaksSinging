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
            .WithName("mgskip")
            .WithDescription("makss gminag skip"),
        new SlashCommandBuilder()
            .WithName("mgseek")
            .WithDescription("makss gminag seek")
            .AddOption("position", ApplicationCommandOptionType.String, "new time position", isRequired: true),
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

    if (command.CommandName == "mgseek")
    {
        if (player.Current is not ISeekableSongPlayState seekable)
        {
            await command.RespondAsync("no");
            return;
        }

        var positionstr = (string) command.Data.Options.First(c => c.Name == "position").Value;
        if (!TimeSpan.TryParse(positionstr, out var position))
        {
            await command.RespondAsync("not a time");
            return;
        }

        seekable.Seek(position);
        await command.RespondAsync($"ok seeked to {position}");
        return;
    }

    if (command.CommandName == "mginfo")
    {
        var embed1 = new EmbedBuilder()
            .WithTitle("current")
            .WithDescription($"""
                {player.Current?.GetProgress() ?? ""}
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


delegate void ProgressSetterDelegate(TimeSpan progress);
class SongInfo
{
    public string? Name { get; init; }

    public override string ToString() => $"{Name ?? "<unknown>"}";
}

static class FFmpegSongPlayer
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannelCount = 2;
    public const int DefaultBps = 16;

    static async Task PlayFromPcmStream(IEnumerable<ISongStreamTarget> targets, Stream source, ProgressSetterDelegate progressSetter, CancellationToken cancellation, int ar = DefaultSampleRate, int ac = DefaultChannelCount, int bits = DefaultBps, int bufferms = 100)
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
            progressSetter(progress);

            await Task.WhenAll(targets.Select(c => c.Write(buffer)));
            await Task.Delay(bufferms);
        }
    }
    static async Task Play(IEnumerable<ISongStreamTarget> targets, string source, ProgressSetterDelegate progressSetter, TimeSpan progress, CancellationToken cancellation)
    {
        var state = new PlayState();

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
                "-ss", $"{progress.TotalSeconds}",
                "pipe:1",
            },
        };
        Console.WriteLine($"Launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        using var ffmpegOutput = proc.StandardOutput.BaseStream;
        cancellation.Register(proc.Kill);

        await PlayFromPcmStream(targets, ffmpegOutput, p => progressSetter(progress + p), cancellation, DefaultSampleRate, DefaultChannelCount, DefaultBps);
        await proc.WaitForExitAsync(cancellation);

        // causes issues with /mgskip
        // await Task.WhenAll(targets.Select(c => c.Flush()));
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

    public static ISongPlayState PlayWithState(out Task task, IEnumerable<ISongStreamTarget> targets, string source)
    {
        var state = new PlayState();
        state.Run(out task, source, targets);

        return state;
    }


    class PlayState : ISeekableSongPlayState
    {
        public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
        bool DurationLoaded = false;

        public TimeSpan Progress { get; private set; } = TimeSpan.Zero;
        public CancellationTokenSource? Cancellation;

        public void Cancel() => Cancellation?.Cancel();
        public string GetProgress() => ProgressBar.Get(Duration, Progress);

        TimeSpan? NewProgress;
        public void Seek(TimeSpan time)
        {
            NewProgress = time;
            Cancellation?.Cancel();
        }

        public void Run(out Task task, string source, IEnumerable<ISongStreamTarget> targets)
        {
            var completion = new TaskCompletionSource();
            task = completion.Task;
            new Thread(async () => await start()) { IsBackground = true }.Start();


            async Task start()
            {
                try
                {
                    var newprogress = NewProgress;
                    NewProgress = null;

                    Cancellation = new();

                    if (!DurationLoaded)
                    {
                        DurationLoaded = true;
                        _ = FFProbeGetDuration(source)
                            .ContinueWith(result =>
                            {
                                if (result is { IsCompletedSuccessfully: true, Result: { } duration })
                                    Duration = duration;
                                else Duration = TimeSpan.Zero;
                            }, Cancellation.Token);
                    }

                    await Play(targets, source, new(p => Progress = p), newprogress ?? TimeSpan.Zero, Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // do nothing
                }

                if (NewProgress is { } np)
                {
                    await start();
                    return;
                }

                completion.SetResult();
            }
        }
    }
}
interface ISongSource
{
    ISongPlayState Play(out Task task, IEnumerable<ISongStreamTarget> targets);
}
class FileSongSource : ISongSource
{
    public static IEnumerable<Song> GetAllFromDirectoryRandomlyOrdered(string directory) =>
        Directory.GetFiles(directory).Select(GetFromFile).OrderBy(_ => Guid.NewGuid());
    public static Song GetFromFile(string file) =>
        new Song(new SongInfo() { Name = Path.GetFileNameWithoutExtension(file).Split('[', StringSplitOptions.TrimEntries)[0] }, new FileSongSource(file));

    readonly string FilePath;

    public FileSongSource(string path) => FilePath = path;

    public ISongPlayState Play(out Task task, IEnumerable<ISongStreamTarget> targets) =>
        FFmpegSongPlayer.PlayWithState(out task, targets, FilePath);
}
class GopFm : ISongSource
{
    public static Song Create() => new Song(new SongInfo() { Name = "Gop FM" }, new GopFm());

    public ISongPlayState Play(out Task task, IEnumerable<ISongStreamTarget> targets)
    {
        var url = "https://hls-01-radiorecord.hostingradio.ru/record-gop/112/playlist.m3u8";
        return FFmpegSongPlayer.PlayWithState(out task, targets, url);
    }
}

static class ProgressBar
{
    static string ToProgressBar(TimeSpan duration, TimeSpan progress, int charCount)
    {
        var filled = (int) ((progress / duration) * charCount);
        return $"{new string('#', filled)}{new string('-', charCount - filled)}";
    }

    public static string Get(TimeSpan duration, TimeSpan progress)
    {
        var charCount = 20;

        if (duration.TotalMinutes == 0)
            return $"{progress.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)} `{new string('~', charCount)}` ??:??:??";

        return $"{progress.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)} `{ToProgressBar(duration, progress, charCount)}` {duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}";
    }
}
interface ISongPlayState
{
    void Cancel();
    string GetProgress();
}
interface ISeekableSongPlayState : ISongPlayState
{
    TimeSpan Duration { get; }
    TimeSpan Progress { get; }

    void Seek(TimeSpan time);
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
    public ISongPlayState? Current { get; private set; }

    public Player(Playlist playlist) => Playlist = playlist;

    public void Skip() => Current?.Cancel();
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
                Current = song.Source.Play(out var task, Targets);

                await task;
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

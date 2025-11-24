using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace MaksSinging;

public interface ISongSource
{
    ISongPlayState Play(out Task task, IStreamTarget targets);
}
public interface ISongPlayState
{
    void Cancel();
    string GetProgress();
}

[SlashCommand("mgplay", "maks gaming play")]
public sealed class RadioPlayCommand(Radio Radio) : ApplicationCommandModule<ApplicationCommandContext>
{
    async Task<string> EnqueueRespond(Radio.Song song, bool atStart)
    {
        Radio.Queue.Enqueue(song, atStart);
        return $"added at position {(atStart ? 1 : Radio.Queue.Count)}: {song.Info}";
    }

    [SubSlashCommand("file", "maks gaming play file")]
    public async Task<string> PlayFile(string search, bool atStart = true)
    {
        var files = Directory.GetFiles("source", $"*{search}*", SearchOption.AllDirectories);
        if (files.Length == 0) return "no";

        var next = Radio.FileSongSource.GetFromFile(files[0]);
        return await EnqueueRespond(next, atStart);
    }

    [SubSlashCommand("maks", "maks gaming play maks")]
    public async Task<string> PlayMaks(bool atStart = true)
    {
        var file = Directory.GetFiles("source/maks").Shuffle().FirstOrDefault();
        if (file is null) return "no maks";

        Radio.Queue.Enqueue(Radio.FileSongSource.GetFromFile(file), atStart);
        return "ok maks";
    }

    [SubSlashCommand("gop", "maks gaming play gop fm")]
    public async Task<string> PlayGop(bool atStart = true) =>
        await EnqueueRespond(Radio.GopFmSongSource.Create(), atStart);
}
public sealed class RadioCommands(Radio Radio) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("mghelp", "maks gaming help")]
    public static async Task<string> Help() => """
        hi im global radio and you are not
        join the channel to start me
        uh idk what to say here have fun or something
        """;

    [SlashCommand("mginfo", "maks gaming info")]
    public async Task<InteractionMessageProperties> Info() => new()
    {
        Embeds = [
            new EmbedProperties()
            {
                Title = "current",
                Description = $"""
                    {Radio.ThePlayer.Current?.GetProgress() ?? ""}
                    {Radio.Queue.Current?.Info?.ToString() ?? "[waiting]"}
                    """.Trim(),
            },
            new EmbedProperties()
            {
                Title = "queue",
                Description = $"""
                    {string.Join('\n', Radio.Queue.QueueEnumerable.Take(5).Select((c, i) => $"{i + 1}: {c.Info}"))}
                    {(Radio.Queue.QueueEnumerable.Count > 5 ? $"... {Radio.Queue.QueueEnumerable.Count} more" : "")}
                    """.Trim(),
                Footer = new EmbedFooterProperties() { Text = "maks_singing" },
                Timestamp = DateTimeOffset.Now,
            },
        ],
    };

    [SlashCommand("mgc", "maks gaming connect")]
    public async Task<string> Connect()
    {
        if (Context.Guild is null)
            return "no";
        if (!Context.Guild.VoiceStates.TryGetValue(Context.User.Id, out var state))
            return "no";
        if (state.ChannelId is not { } channelId)
            return "no";

        await Radio.JoinVoiceChannel(Context.Guild.Id, channelId);
        return $"k";
    }

    [SlashCommand("mgd", "maks gaming disconnect")]
    public async Task<string> Disconnect()
    {
        await Radio.LeaveVoiceChannel(Context.Guild!.Id);
        return "k";
    }

    [SlashCommand("mgskip", "maks gaming skip")]
    public async Task<string> Skip()
    {
        var current = Radio.Queue.Current;
        if (current is null) return "no";

        Radio.ThePlayer.Skip();
        return $"k skipped {current.Info}";
    }

    public enum AddType { File }
}

public class Radio(GatewayClient Discord, IServiceProvider Di, ILogger<Radio> Logger) : IStreamTarget
{
    public Player ThePlayer { get; private set; } = null!;
    public Playlist Queue { get; } = new();

    readonly Dictionary<ulong, GuildRadio> Guilds = [];

    public async Task Start()
    {
        Logger.LogInformation("Starting radio service");
        var pl = new Player(Queue, this);
        ThePlayer = pl;

        pl.SongStarted += async song =>
        {
            await Discord.UpdatePresenceAsync(new PresenceProperties(UserStatusType.Online)
            {
                Activities = [
                    new UserActivityProperties(song.Info.ToString(), UserActivityType.Listening)
                ],
            });
        };

        Queue.Enqueue(false, FileSongSource.GetAllFromDirectoryRandomlyOrdered("source"));


        Discord.GuildCreate += async args =>
        {
            if (args.Guild is null) return;

            Guilds[args.Guild.Id] = new(Discord, args.Guild, Di.GetRequiredService<ILogger<GuildRadio>>());
            await Guilds[args.Guild.Id].Start(this);
        };
        Discord.GuildDelete += async args =>
        {
            Guilds.Remove(args.GuildId); // TODO: dispose
        };


        new Thread(async () => await pl.Run()) { IsBackground = true }.Start();
    }

    public async Task JoinVoiceChannel(ulong guildId, ulong channelId) =>
        await Discord.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, channelId) { SelfDeaf = true });
    public async Task LeaveVoiceChannel(ulong guildId) =>
        await Discord.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));

    async Task IStreamTarget.Write(ReadOnlyMemory<byte> data, CancellationToken cancellation) =>
        await Task.WhenAll([.. Guilds.Values.Select(s => s.Write(data, cancellation))]);


    public sealed class Player
    {
        public event Action<Song>? SongStarted;
        public ISongPlayState? Current { get; private set; }

        public Playlist Playlist { get; }
        readonly Radio Radio;

        public Player(Playlist playlist, Radio radio)
        {
            Playlist = playlist;
            Radio = radio;
        }

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
                    Current = song.Source.Play(out var task, Radio);
                    SongStarted?.Invoke(song);

                    await task;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
        }
    }

    public sealed class SongInfo
    {
        public string? Name { get; init; }

        public override string ToString() => $"{Name ?? "<unknown>"}";
    }
    public sealed record Song(SongInfo Info, ISongSource Source);
    public sealed class Playlist
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

    public sealed class FileSongSource : ISongSource
    {
        public static IEnumerable<Song> GetAllFromDirectoryRandomlyOrdered(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(GetFromFile).OrderBy(_ => Guid.NewGuid());
        public static Song GetFromFile(string file) =>
            new Song(new SongInfo() { Name = Path.GetFileNameWithoutExtension(file).Split('[', StringSplitOptions.TrimEntries)[0] }, new FileSongSource(file));

        readonly string FilePath;

        public FileSongSource(string path) => FilePath = path;

        public ISongPlayState Play(out Task task, IStreamTarget targets) =>
            FFmpegPlayer.PlayWithState(out task, targets, FilePath);
    }
    public sealed class GopFmSongSource : ISongSource
    {
        public static Song Create() => new Song(new SongInfo() { Name = "Gop FM" }, new GopFmSongSource());

        public ISongPlayState Play(out Task task, IStreamTarget target)
        {
            var url = "https://hls-01-radiorecord.hostingradio.ru/record-gop/112/playlist.m3u8";
            return FFmpegPlayer.PlayWithState(out task, target, url);
        }
    }

    sealed class GuildRadio(GatewayClient Discord, Guild Guild, ILogger<GuildRadio> Logger) : IStreamTarget
    {
        VoiceClient? Client;
        OpusEncodeStream? OpusStream;

        public async Task Start(Radio radio)
        {
            Logger.LogInformation("Initializing guild {}", new { name = Guild.Name, id = Guild.Id });

            var cid = 1386284245546307675ul; // awms radio channel
            var cidVoiceUsers = new HashSet<GuildUser>();

            var debounceToken = new CancellationTokenSource();
            string? endpoint = null;
            string? token = null;
            string? sessionid = null;
            ulong? channelid = null;

            Discord.VoiceServerUpdate += async args =>
            {
                if (args.GuildId != Guild.Id) return;
                endpoint = args.Endpoint;
                token = args.Token;

                await update();
            };
            Discord.VoiceStateUpdate += async args =>
            {
                if (args.GuildId != Guild.Id) return;
                Logger.LogInformation($"{args.GuildId} User {args.User?.Nickname} -> {args.ChannelId}");

                if (args.UserId == Discord.Id)
                {
                    sessionid = args.SessionId;
                    channelid = args.ChannelId;
                    await update();
                    return;
                }

                if (args.User is not null)
                {
                    if (args.ChannelId == cid)
                        cidVoiceUsers.Add(args.User);
                    else cidVoiceUsers.Remove(args.User);

                    if (cidVoiceUsers.Count == 0)
                    {
                        Logger.LogInformation($"Auto-leaving channel {channelid} as it's empty");
                        await radio.LeaveVoiceChannel(Guild.Id);
                    }
                    else
                    {
                        Logger.LogInformation($"Auto-joining channel {cid}");
                        await radio.JoinVoiceChannel(Guild.Id, cid);
                    }
                }
            };


            async Task update()
            {
                debounceToken.Cancel();
                debounceToken = new CancellationTokenSource();
                var token = debounceToken.Token;

                _ = Task.Delay(TimeSpan.FromSeconds(.5), token)
                    .ContinueWith(async t =>
                    {
                        if (t.IsCanceled) return;
                        await _update();
                    }, CancellationToken.None);
            }
            async Task _update()
            {
                if (sessionid is null || endpoint is null || token is null || channelid is null) return;
                Logger.LogInformation("Restarting voice with {}", new { guildId = Guild.Id, channelid, sessionid, endpoint, token });

                try { Client?.Abort(); }
                catch { }

                Client = new VoiceClient(Discord.Id, sessionid, endpoint, Guild.Id, token, new VoiceClientConfiguration());
                await Client.StartAsync();
                await Client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

                OpusStream = new OpusEncodeStream(Client.CreateOutputStream(), PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);
            }
        }
        public async Task Write(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            try
            {
                await (OpusStream?.WriteAsync(data, cancellation) ?? ValueTask.CompletedTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}

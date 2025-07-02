using System.Diagnostics;
using System.Globalization;
using Discord;
using Discord.WebSocket;

var ffmpeg = File.Exists("ffmpeg") ? "./ffmpeg" : "ffmpeg";
var currentFile = "current";

var streamList = new MultiStreamWriter();

using var discord = new DiscordSocketClient(new DiscordSocketConfig() { GatewayIntents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds });
discord.Log += async (log) =>
{
    Console.WriteLine($"[{DateTimeOffset.Now}] [{log.Severity}] {(object) log.Exception ?? log.Message}");
};

discord.Ready += async () => _ = Start("source");

await discord.LoginAsync(Discord.TokenType.Bot, File.ReadAllText("discordkey"));
await discord.StartAsync();

await Task.Delay(-1);


async Task Start(string directory)
{
    Queue<string> getFiles() => new(Directory.GetFiles(directory).OrderBy(_ => Guid.NewGuid()));

    using var guild = discord.GetGuild(1053774759244083280);
    var voiceChannel = guild.GetVoiceChannel(1386284245546307675);
    string? currentPlayingPath = null;

    Task sendRpc() => discord.SetActivityAsync(new Game(Path.GetFileName(currentPlayingPath).Split('[', StringSplitOptions.TrimEntries)[0], ActivityType.Listening));
    new Thread(async () =>
    {
        while (true)
        {
            if (currentPlayingPath is not null)
                await sendRpc();

            await Task.Delay(10_000);
        }
    })
    { IsBackground = true }.Start();

    DateTimeOffset? songStartedTime = null;

    while (true)
    {
        try
        {
            var bucket = new Queue<string>();
            var startingTimeSec = 0;
            if (File.Exists(currentFile))
            {
                var data = File.ReadAllLines(currentFile);
                startingTimeSec = int.Parse(data[1], CultureInfo.InvariantCulture);

                bucket.Enqueue(data[0]);
                File.Delete(currentFile);
                Console.WriteLine($"Starting {data[0]} at {TimeSpan.FromSeconds(startingTimeSec)}");
            }

            using var audioClient = await voiceChannel.ConnectAsync(selfDeaf: true);
            using var audioStream = audioClient.CreatePCMStream(Discord.Audio.AudioApplication.Music, 128 * 1024);

            while (true)
            {
                if (bucket.Count == 0)
                    bucket = getFiles();

                var path = bucket.Dequeue();
                currentPlayingPath = path;
                songStartedTime = DateTimeOffset.Now;

                _ = sendRpc();
                Console.WriteLine($"Playing {path}");
                await ConvertToPcm(path, audioStream, startingTimeSec);

                currentPlayingPath = null;
                startingTimeSec = 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Console.WriteLine("RECONNECTING...");

            if (currentPlayingPath is not null && songStartedTime is { } sst)
            {
                var diff = (int) (DateTimeOffset.Now - sst).TotalSeconds;
                File.WriteAllLines(currentFile, [currentPlayingPath, diff.ToString(CultureInfo.InvariantCulture)]);

                Console.WriteLine($"Will restart {currentPlayingPath} at {TimeSpan.FromSeconds(diff)}");
            }
        }
    }
}

async Task ConvertToPcm(string path, Stream target, int startingTimeSec = 0)
{
    var psi = new ProcessStartInfo(ffmpeg)
    {
        RedirectStandardOutput = true,
        ArgumentList =
        {
            "-hide_banner",
            "-i", path,
            "-ss", startingTimeSec.ToString(CultureInfo.InvariantCulture),
            "-ac", "2",
            "-ar", "48000",
            "-f", "s16le",
            "pipe:1",
        },
    };
    Console.WriteLine($"Launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

    using var proc = Process.Start(psi)!;
    using var ffmpegOutput = proc.StandardOutput.BaseStream;

    await ffmpegOutput.CopyToAsync(target);
    await proc.WaitForExitAsync();
    await target.FlushAsync();
}


class VoiceState
{
    public MultiStreamWriter? Writer { get; }

    public async Task Connect(SocketVoiceChannel channel)
    {

    }
}


[Obsolete]
class MultiStreamWriter : Stream
{
    readonly List<VoiceState> Streams = [];
    readonly Lock Lock = new();

    public void AddStream(VoiceState stream)
    {
        if (!Streams.Contains(stream))
            Streams.Add(stream);
    }
    public void RemoveStream(VoiceState stream) => Streams.Remove(stream);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        foreach (var stream in Streams)
            stream.Writer?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Streams.Select(c => c.Writer?.FlushAsync(cancellationToken)).Where(c => c is not null).ToArray()!);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        var streams = Streams.ToArray();
        foreach (var stream in streams)
            await stream.WriteAsync(bytes);
    }
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        lock (Lock)
        {
            foreach (var stream in Streams)
            {
                stream.Write(bytes);
            }
        }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || (offset + count) > buffer.Length)
            throw new ArgumentOutOfRangeException();

        lock (Lock)
        {
            foreach (var stream in Streams)
            {
                stream.Write(buffer, offset, count);
            }
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || (offset + count) > buffer.Length)
            throw new ArgumentOutOfRangeException();

        List<Task> tasks;
        lock (Lock)
        {
            tasks = new List<Task>();
            foreach (var stream in Streams)
            {
                tasks.Add(stream.WriteAsync(buffer, offset, count, cancellationToken));
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Streams.Clear();

        base.Dispose(disposing);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaksSinging;

public interface IStreamTarget
{
    Task Write(ReadOnlyMemory<byte> data, CancellationToken cancellation);
}
public static class FFmpegPlayer
{
    const int DefaultSampleRate = 48000;
    const int DefaultChannelCount = 2;
    const int DefaultBps = 16;

    public delegate void ProgressSetterDelegate(TimeSpan progress);
    public static async Task PlayFromUri(string source, IStreamTarget target, ProgressSetterDelegate progressSetter, CancellationToken cancellation)
    {
        var ffmpeg = "/bin/ffmpeg";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-hwaccel", "auto",
                "-hide_banner",
                "-re",
                "-i", source,
                "-ar", $"{DefaultSampleRate}",
                "-ac", $"{DefaultChannelCount}",
                "-f", $"s{DefaultBps}le",
                "-ss", $"{0}",
                "pipe:1",
            },
        };

        using var proc = Process.Start(psi)!;
        using var ffmpegOutput = proc.StandardOutput.BaseStream;

        await PlayFromPcmStream(ffmpegOutput, target, progressSetter, cancellation);
    }
    public static async Task PlayFromPcmStream(Stream sourcePcm, IStreamTarget target, ProgressSetterDelegate progressSetter, CancellationToken cancellation)
    {
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await sourcePcm.ReadAsync(buffer, cancellation);
            if (read == 0) break;

            await target.Write(buffer.AsMemory(0, read), cancellation);
            await Task.Delay(100, cancellation);
        }
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

        if (double.TryParse(JsonSerializer.Deserialize<JsonNode>(json)?["format"]?["duration"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var duration))
            return TimeSpan.FromSeconds(duration);

        return null;
    }

    public static ISongPlayState PlayWithState(out Task task, IStreamTarget target, string source)
    {
        var state = new PlayState();
        state.Run(out task, source, target);

        return state;
    }
    sealed class PlayState : ISongPlayState
    {
        public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
        public TimeSpan Progress { get; private set; } = TimeSpan.Zero;
        public CancellationTokenSource? Cancellation;

        public void Cancel() => Cancellation?.Cancel();
        public string GetProgress() => ProgressBar.Get(Duration, Progress);

        public void Run(out Task task, string source, IStreamTarget target)
        {
            var completion = new TaskCompletionSource();
            task = completion.Task;
            new Thread(async () => await start()) { IsBackground = true }.Start();


            async Task start()
            {
                try
                {
                    Cancellation = new();

                    _ = FFProbeGetDuration(source)
                        .ContinueWith(result =>
                        {
                            if (result is { IsCompletedSuccessfully: true, Result: { } duration })
                                Duration = duration;
                        }, Cancellation.Token);

                    await PlayFromUri(source, target, new(p => Progress = p), Cancellation.Token);
                }
                catch (OperationCanceledException) { }

                completion.SetResult();
            }
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
}

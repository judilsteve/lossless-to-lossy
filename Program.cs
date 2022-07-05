using CliWrap;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;

var sourceDir = args[0];
var destDir = args[1];

var artRegex = new Regex(@"[Ff]ront\.jpe?g", RegexOptions.Compiled);

async Task ProcessFile(string filePath, CancellationToken cancellationToken) {
    if(artRegex.IsMatch(Path.GetFileName(filePath))) {
        await ProcessArt(filePath, cancellationToken);
    } else {
        var extension = Path.GetExtension(filePath);
        if(!new[]{ ".m4a", ".flac" }.Contains(extension)) {
            await Console.Out.WriteLineAsync($"Skipping \"{filePath}\"");
            return;
        }
        await ProcessTrack(filePath, cancellationToken);
    }
}

async Task ProcessArt(string filePath, CancellationToken cancellationToken) {
    await Console.Out.WriteLineAsync($"Processing art \"{filePath}\"");

    var destFileName = "front.jpg";
    var containingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
    var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, containingDirectory), destFileName);
    if(File.Exists(dest)) {
        var destModified = File.GetLastWriteTimeUtc(dest);
        var modified = File.GetLastWriteTimeUtc(filePath);
        if(destModified >= modified) {
            await Console.Out.WriteLineAsync($"Unchanged: \"{filePath}\"");
            return;
        }
    } else {
        var destDirectory = Path.GetDirectoryName(dest);
        Directory.CreateDirectory(destDirectory!);
    }

    using var sourceStream = File.Open(filePath, FileMode.Open);
    using var destStream = File.Open(dest, FileMode.OpenOrCreate);
    await sourceStream.CopyToAsync(destStream);
}

async Task ProcessTrack(string filePath, CancellationToken cancellationToken) {
    await Console.Out.WriteLineAsync($"Processing track \"{filePath}\"");

    var destFileName = $"{Path.GetFileNameWithoutExtension(filePath)}.ogg";
    var containingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
    var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, containingDirectory), destFileName);
    if(File.Exists(dest)) {
        // Bail if older
        var destModified = File.GetLastWriteTimeUtc(dest);
        var modified = File.GetLastWriteTimeUtc(filePath);

        if(destModified >= modified) {
            await Console.Out.WriteLineAsync($"Unchanged: \"{filePath}\"");
            return;
        }
    } else {
        var destDirectory = Path.GetDirectoryName(dest);
        Directory.CreateDirectory(destDirectory!);
    }

    var infoProcess = Process.Start(new ProcessStartInfo{
        FileName = "mediainfo",
        Arguments = $"--Output=JSON \"{filePath}\"",
        RedirectStandardOutput = true,
    }) ?? throw new Exception("Failed to start mediainfo");
    var info = await JsonSerializer.DeserializeAsync<JsonDocument>(infoProcess.StandardOutput.BaseStream, options: null, cancellationToken);
    var trackArray = info!.RootElement
        .GetProperty("media")
        .GetProperty("track")
        .EnumerateArray();

    var audioInfo = trackArray.FirstOrDefault(
        e => e.GetProperty("@type").GetString() == "Audio");
    var sampleRate = int.Parse(audioInfo.GetProperty("SamplingRate").GetString()!);
    var channels = int.Parse(audioInfo.GetProperty("Channels").GetString()!);

    var channelMap = channels switch {
        2 or 1 => "",
        // https://superuser.com/questions/852400/properly-downmix-5-1-to-stereo-using-ffmpeg
        6 => "-af \"pan=stereo|FL=0.5*FC+0.707*FL+0.707*BL+0.5*LFE|FR=0.5*FC+0.707*FR+0.707*BR+0.5*LFE\"",
        _ => throw new Exception($"Don't know how to handle track with {channels} channels: \"{filePath}\"")
    };

    Command command;
    if(sampleRate > 48000) {
        var targetSampleRate = sampleRate % 44100 == 0 ? 44100 : 48000;
        await Console.Out.WriteLineAsync($"Resampling from {sampleRate} to {targetSampleRate}: \"{filePath}\"");
        command = Cli.Wrap("ffmpeg").WithArguments($"-i \"{filePath}\" {channelMap} -f wav -") |
            Cli.Wrap("sox").WithArguments($"-t wav - -t wav - rate -v {targetSampleRate}") |
            Cli.Wrap("ffmpeg").WithArguments($"-i - -i \"{filePath}\" -map 0:0 -map_metadata 1 -q:a 7 -c:a libvorbis -y \"{dest}\"");
    } else {
        await Console.Out.WriteLineAsync($"No resampling required (already {sampleRate}): \"{filePath}\"");
        command = Cli.Wrap("ffmpeg")
            .WithArguments($"-i \"{filePath}\" {channelMap} -q:a 7 -c:a libvorbis -y \"{dest}\"");
    }
    await command.ExecuteAsync(cancellationToken);
}

await Parallel.ForEachAsync(
    Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories),
    async (filePath, cancellationToken) => await ProcessFile(filePath, cancellationToken));

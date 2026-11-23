using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimationMaker.Core;

public sealed partial class ApplicationUpdates(HttpClient client, string cacheDirectory)
{
    private const string Repository = "Malasuerte94/animation-maker";
    private const long MaxSetupSize = 1024L * 1024 * 1024;
    private const string SetupSuffix = "-win-x64-setup.exe";

    // Release tags use major.minor.patch with an optional numbered beta. Stable sorts after every beta.
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.(0|[1-9][0-9]*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private static (Version Core, long Beta)? ParseVersion(string value)
    {
        var match = VersionPattern().Match(value);
        if (!match.Success || !Version.TryParse(string.Join(".", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value), out var core)) return null;
        if (!match.Groups[4].Success) return (core, long.MaxValue);
        return int.TryParse(match.Groups[4].Value, out int beta) ? (core, beta) : null;
    }

    public async Task<PreparedUpdate?> PrepareAsync(string currentVersion, CancellationToken ct)
    {
        var current = ParseVersion(currentVersion.Split('+')[0]) ?? throw new InvalidDataException("Unknown application version.");
        RemoveOldDownloads(current);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("AnimationMaker-Updater/1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var release = metadata.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || ParseVersion(tag[1..]) is not { } version || version.CompareTo(current) <= 0) return null;
        var filename = $"AnimationMaker-{tag[1..]}{SetupSuffix}";
        var uri = $"https://github.com/{Repository}/releases/download/{tag}/{filename}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != filename) continue;
            string? digest = asset.GetProperty("digest").GetString();
            long size = asset.GetProperty("size").GetInt64();
            if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("browser_download_url").GetString() != uri ||
                size is <= 0 or > MaxSetupSize || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid release installer metadata.");
            var update = new PreparedUpdate(tag, Path.Combine(cacheDirectory, filename), size, digest[7..]);
            Directory.CreateDirectory(cacheDirectory);
            if (File.Exists(update.InstallerPath) && update.IsVerified()) return update;
            await DownloadAsync(uri, update, ct).ConfigureAwait(false);
            return update;
        }
        return null;
    }

    private async Task DownloadAsync(string uri, PreparedUpdate update, CancellationToken ct)
    {
        string temporary = update.InstallerPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("Unexpected installer size.");
            await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long total = 0;
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
                    {
                        total += read;
                        if (total > update.Size) throw new InvalidDataException("Installer exceeds its published size.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    if (total != update.Size || !update.MatchesHash(hash.GetHashAndReset()))
                        throw new InvalidDataException("Installer checksum verification failed.");
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, update.InstallerPath, true);
        }
        finally { File.Delete(temporary); }
    }

    private void RemoveOldDownloads((Version Core, long Beta) current)
    {
        if (!Directory.Exists(cacheDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "AnimationMaker-*" + SetupSuffix))
        {
            var name = Path.GetFileName(file);
            var version = ParseVersion(name["AnimationMaker-".Length..^SetupSuffix.Length]);
            if (version is { } previous && previous.CompareTo(current) <= 0)
            {
                try { File.Delete(file); } catch (IOException) { /* A setup process can still hold its executable. */ }
            }
        }
    }
}

public sealed record PreparedUpdate(string Tag, string InstallerPath, long Size, string Sha256)
{
    internal bool MatchesHash(byte[] digest) => Convert.ToHexString(digest).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    private bool Verify(Stream file) => file.Length == Size && MatchesHash(SHA256.HashData(file));

    internal bool IsVerified()
    {
        using var file = File.OpenRead(InstallerPath);
        return Verify(file);
    }

    public ProcessStartInfo GetStartInfo(string appDirectory, bool portable, int parentPid, string sessionMutex)
    {
        var start = new ProcessStartInfo(InstallerPath) { UseShellExecute = false };
        foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/SP-", "/NORESTART", "/NOCLOSEAPPLICATIONS",
            "/NORESTARTAPPLICATIONS", "/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)),
            "/AMWAITPID=" + parentPid, "/AMSESSION=" + sessionMutex })
            start.ArgumentList.Add(arg);
        if (portable) start.ArgumentList.Add("/AMPORTABLE=1");
        return start;
    }

    public void Start(string appDirectory, bool portable, string sessionMutex)
    {
        using var file = File.Open(InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Verify(file)) throw new InvalidDataException("The cached installer failed verification.");
        using var process = Process.Start(GetStartInfo(appDirectory, portable, Environment.ProcessId, sessionMutex));
    }
}

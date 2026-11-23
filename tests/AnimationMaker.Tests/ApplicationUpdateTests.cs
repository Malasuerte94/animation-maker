using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AnimationMaker.Core;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class ApplicationUpdateTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("AnimationMakerUpdates-").FullName;
    private static readonly byte[] Setup = [1, 2, 3, 4, 5, 6, 7, 8];
    private sealed class Feed(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Release(string version, string? url = null, string? hash = null, bool prerelease = false)
    {
        var name = $"AnimationMaker-{version}-win-x64-setup.exe";
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            tag_name = "v" + version, draft = false, prerelease,
            assets = new[] { new { name, state = "uploaded", size = Setup.Length,
                digest = "sha256:" + (hash ?? Convert.ToHexString(SHA256.HashData(Setup)).ToLowerInvariant()),
                browser_download_url = url ?? $"https://github.com/Malasuerte94/animation-maker/releases/download/v{version}/{name}" } }
        }) };
    }

    [Theory]
    [InlineData("0.1.0-beta.10", "0.1.0-beta.9", false)]
    [InlineData("0.1.0-beta.5+commit", "0.1.0-beta.5", false)]
    [InlineData("0.1.0", "0.1.0-beta.20", false)]
    [InlineData("0.1.0-beta.1", "0.1.0-beta.3", true)]
    [InlineData("0.1.0-beta.1", "../download", false)]
    public async Task OnlyNewerPublishedVersionsAreDownloaded(string local, string remote, bool prerelease)
    {
        using var client = new HttpClient(new Feed(r =>
        {
            Assert.Equal("api.github.com", r.RequestUri!.Host);
            return Release(remote, prerelease: prerelease);
        }));
        Assert.Null(await new ApplicationUpdates(client, _cache).PrepareAsync(local, default));
    }

    [Theory]
    [InlineData("https://example.org/setup.exe", null)]
    [InlineData(null, "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(null, "missing")]
    public async Task InvalidAssetMetadataOrBytesNeverProduceAReadyUpdate(string? url, string? hash)
    {
        using var client = new HttpClient(new Feed(r => r.RequestUri!.Host == "api.github.com"
            ? Release("0.1.0", url, hash) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApplicationUpdates(client, _cache).PrepareAsync("0.1.0-beta.5", default));
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task InterruptedDownloadIsDiscardedAndTheNextAttemptCanFinish()
    {
        int attempts = 0;
        using var client = new HttpClient(new Feed(r =>
        {
            if (r.RequestUri!.Host == "api.github.com") return Release("0.1.0-beta.6");
            var content = new ByteArrayContent(++attempts == 1 ? Setup[..3] : Setup);
            content.Headers.ContentLength = Setup.Length;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var updater = new ApplicationUpdates(client, _cache);
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.PrepareAsync("0.1.0-beta.5", default));
        Assert.Empty(Directory.GetFiles(_cache));
        Assert.NotNull(await updater.PrepareAsync("0.1.0-beta.5", default));
        Assert.Single(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task VerifiedInstallerIsReusedAndCorruptionIsRepaired()
    {
        int downloads = 0;
        using var client = new HttpClient(new Feed(r =>
        {
            if (r.RequestUri!.Host == "api.github.com") return Release("0.1.0-beta.10");
            downloads++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) };
        }));
        var updater = new ApplicationUpdates(client, _cache);
        var update = await updater.PrepareAsync("0.1.0-beta.9", default);
        Assert.NotNull(update);
        Assert.Equal(Setup, await File.ReadAllBytesAsync(update.InstallerPath));
        Assert.Equal(update, await updater.PrepareAsync("0.1.0-beta.9", default));
        Assert.Equal(1, downloads);
        await File.WriteAllBytesAsync(update.InstallerPath, new byte[Setup.Length]);
        Assert.Throws<InvalidDataException>(() => update.Start(_cache, true, "test"));
        Assert.Equal(update, await updater.PrepareAsync("0.1.0-beta.9", default));
        Assert.Equal(2, downloads);
        var path = Path.Combine(_cache, "portable with spaces");
        var portable = update.GetStartInfo(path, true, 145, "session");
        Assert.Contains("/DIR=" + path, portable.ArgumentList);
        Assert.Contains("/AMWAITPID=145", portable.ArgumentList);
        Assert.Contains("/NOCLOSEAPPLICATIONS", portable.ArgumentList);
        Assert.Contains("/AMPORTABLE=1", portable.ArgumentList);
        Assert.DoesNotContain("/AMPORTABLE=1", update.GetStartInfo(path, false, 145, "session").ArgumentList);
        Assert.Null(await updater.PrepareAsync("0.1.0-beta.10", default));
        Assert.Empty(Directory.GetFiles(_cache));
    }

    public void Dispose() => Directory.Delete(_cache, true);
}

using System.IO.Compression;
using AnimationMaker.Core;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class DeltaRuntimePackageTests
{
    [Fact]
    public async Task PackageContainsOnlyReusableRuntimeTypesAndIntegrationInstructions()
    {
        string root = Path.Combine(Path.GetTempPath(), "AnimationMaker-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string[] files = ["delta-runtime.js", "delta-runtime.iife.js", "delta-runtime.d.ts", "package.json", "README.md", "LICENSE"];
        foreach (string file in files) await File.WriteAllTextAsync(Path.Combine(root, file), "contents of " + file);
        await File.WriteAllTextAsync(Path.Combine(root, "demo.html"), "not part of the plugin");
        await File.WriteAllTextAsync(Path.Combine(root, "delta.animation.json"), "not part of the plugin");
        string path = Path.Combine(root, "plugin.zip"); await DeltaRuntimePackage.SaveAsync(root, path);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(files.Order(), zip.Entries.Select(e => e.Name).Order());
        foreach (var entry in zip.Entries)
        {
            Assert.StartsWith("animation-maker-pixi-delta/", entry.FullName);
            using var text = new StreamReader(entry.Open()); Assert.Equal("contents of " + entry.Name, await text.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task MissingRuntimeOrCancellationPreservesExistingDownloadAndRemovesStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "AnimationMaker-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "plugin.zip"); await File.WriteAllTextAsync(path, "keep");
        await Assert.ThrowsAsync<FileNotFoundException>(() => DeltaRuntimePackage.SaveAsync(root, path));
        foreach (string file in new[] { "delta-runtime.js", "delta-runtime.iife.js", "delta-runtime.d.ts", "package.json", "README.md", "LICENSE" })
            await File.WriteAllTextAsync(Path.Combine(root, file), "fixture");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeltaRuntimePackage.SaveAsync(root, path, cts.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(root, "*.staging-*"));
    }
}

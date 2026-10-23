using System.IO.Compression;

namespace AnimationMaker.Core;

public static class DeltaRuntimePackage
{
    private static readonly string[] Files = ["delta-runtime.js", "delta-runtime.iife.js", "delta-runtime.d.ts", "package.json", "README.md", "LICENSE"];

    public static async Task SaveAsync(string runtimeDirectory, string destination, CancellationToken ct = default)
    {
        foreach (string name in Files)
            if (!File.Exists(Path.Combine(runtimeDirectory, name))) throw new FileNotFoundException("The PixiJS runtime package is incomplete: " + name);
        string temporary = Path.GetFullPath(destination) + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = File.Create(temporary))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (string name in Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry("animation-maker-pixi-delta/" + name, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var source = File.OpenRead(Path.Combine(runtimeDirectory, name));
                    await source.CopyToAsync(target, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

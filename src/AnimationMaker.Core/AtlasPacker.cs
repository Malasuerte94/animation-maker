namespace AnimationMaker.Core;

/// <summary>Search rectangular MaxRects bins rather than filling one maximum-size strip.</summary>
internal static class AtlasPacker
{
    public static List<AtlasPage> Pack(IReadOnlyCollection<SpriteRecord> sprites, int limit, int margin, CancellationToken ct,
        Func<List<AtlasPage>, int, long>? encodedSize = null, ExportProfiler? profiler = null)
    {
        if (sprites.Count == 0) return [];
        int maxWidth = sprites.Max(s => s.Bounds.Width + 2 * margin);
        int maxHeight = sprites.Max(s => s.Bounds.Height + 2 * margin);
        if (maxWidth > limit || maxHeight > limit) throw new InvalidDataException("A patch/frame exceeds the atlas size. Lower frame height or increase atlas size.");
        long area = sprites.Sum(s => (long)(s.Bounds.Width + 2 * margin) * (s.Bounds.Height + 2 * margin));
        double side = Math.Sqrt(Math.Min(area, (long)limit * limit));
        var widths = new SortedSet<int> { maxWidth, limit };
        foreach (double target in new[] { side * .75, side, side * 1.25, side * 1.5, (double)area / limit })
            widths.Add(Math.Clamp((int)Math.Min(limit, Math.Ceiling(target / 16) * 16), maxWidth, limit));
        var orders = new[] {
            sprites.OrderByDescending(s => Math.Max(s.Bounds.Width, s.Bounds.Height)).ThenByDescending(s => (long)s.Bounds.Width * s.Bounds.Height).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray(),
            sprites.OrderByDescending(s => (long)s.Bounds.Width * s.Bounds.Height).ThenByDescending(s => s.Bounds.Height).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray()
        };
        var cases = widths.SelectMany(width => orders.Select(order => (Width: width, Order: order))).ToArray();
        var layouts = new (List<AtlasPage> Pages, double Score)[cases.Length];
        long geometryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        Parallel.For(0, cases.Length, new ParallelOptions { MaxDegreeOfParallelism = sprites.Count >= 256 ? Math.Min(2, Environment.ProcessorCount) : 1, CancellationToken = ct },
            index =>
            {
                var (width, order) = cases[index];
                var pages = new List<AtlasPage>();
                foreach (var sprite in order)
                {
                    ct.ThrowIfCancellationRequested();
                    int w = sprite.Bounds.Width + 2 * margin, h = sprite.Bounds.Height + 2 * margin;
                    if (pages.Any(p => p.TryPlace(sprite, w, h))) continue;
                    var page = new AtlasPage(width, limit);
                    if (!page.TryPlace(sprite, w, h)) throw new InvalidDataException("Patch does not fit in the atlas.");
                    pages.Add(page);
                }
                // Area dominates; a small aspect penalty breaks near-ties in favour of balanced pages.
                double score = pages.Sum(p => (double)p.Width * p.Height + .02 * Math.Pow(p.Width - p.Height, 2));
                layouts[index] = (pages, score);
            });
        profiler?.AddWall("atlas_geometry", System.Diagnostics.Stopwatch.GetElapsedTime(geometryStarted).TotalMilliseconds);
        profiler?.Count("atlas_geometry_layouts", cases.Length);
        var ranked = layouts.OrderBy(p => p.Pages.Count).ThenBy(p => p.Score).ToArray();
        var best = ranked[0];
        if (encodedSize == null) return best.Pages;
        // Similar density can encode very differently. Measure the actual codec on the best layouts.
        long bestBytes = long.MaxValue; List<AtlasPage> selected = best.Pages;
        var finalists = ranked.Where(p => p.Pages.Count == best.Pages.Count && p.Score <= best.Score * 1.25).Take(4).ToArray();
        long largestImage = finalists.Max(p => p.Pages.Max(page => (long)page.Width * page.Height * 4));
        int workers = (int)Math.Clamp(256L * 1024 * 1024 / Math.Max(1, largestImage * 4), 1, Math.Min(2, Environment.ProcessorCount));
        var sizes = new long[finalists.Length];
        Parallel.For(0, finalists.Length, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            i => sizes[i] = encodedSize(finalists[i].Pages, i));
        for (int i = 0; i < finalists.Length; i++)
            if (sizes[i] < bestBytes) { bestBytes = sizes[i]; selected = finalists[i].Pages; }
        return selected;
    }
}

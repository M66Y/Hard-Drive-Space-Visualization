using System.Diagnostics;

namespace DiskAtlas;

// Opt-in, UI-thread-only instrumentation for owned validation windows. Normal
// operation keeps Current null. No paths, handles or arbitrary strings recorded.
internal sealed class ResizeWorkProbe
{
    internal enum Work { None, PositionChanging, PositionChanged, Size, NonClient, Erase,
        Wallpaper, RasterBuild, LayoutPresent, TreemapBuild, GridBuild, RootCover, ExposedCover }
    [ThreadStatic] private static ResizeWorkProbe? currentProbe;
    internal static ResizeWorkProbe? Current { get => currentProbe; set => currentProbe = value; }
    private readonly long[,] ticks = new long[13, 13], counts = new long[13, 13], maxima = new long[13, 13];
    private readonly List<(int Step, Size Size, Work Caller)> rasterKeys = [];
    private readonly long[,] wallpaperTicks = new long[8, 256], wallpaperCalls = new long[8, 256];
    private readonly long[] rootCache = new long[4];
    internal enum CacheResult { Hit, KeyOrPolicy, Outside, ManagedHit }
    internal static void Cache(CacheResult result) { if(Current is {} p)p.rootCache[(int)result]++; }
    private long deferredPanels;
    internal static void DeferredPanel() { if(Current is {} p)p.deferredPanels++; }
    private Work current;
    private int step;
    internal int Dropped { get; private set; }
    internal void BeginStep(int value) { if(value is < 0 or > 12)throw new ArgumentOutOfRangeException(nameof(value));step=value; }
    internal static Scope Measure(Work work) => Current is { } probe && work != Work.None ? new(probe,work) : default;
    internal static WallpaperScope MeasureWallpaper(Control control, int wash) => Current is { } probe
        ? new(probe, control switch { Form => 0, WallpaperTable => 1, WallpaperSplitContainer => 2,
            SplitterPanel => 3, WallpaperGrid => 4, TreemapControl => 5, _ => 6 }, Math.Clamp(wash,0,255)) : default;
    internal static void RasterMiss(Size size)
    {
        if(Current is not { } probe)return;
        if(probe.rasterKeys.Count<256)probe.rasterKeys.Add((probe.step,size,probe.current));else probe.Dropped++;
    }
    internal object Summary() => new
    {
        capacity_steps=12, capacity_raster_events=256, dropped=Dropped, inclusive_times=true,
        steps=Enumerable.Range(1,12).Select(s=>new { step=s, phases=Enumerable.Range(1,12).Where(w=>counts[s,w]>0).Select(w=>new
        { phase=((Work)w).ToString(), calls=counts[s,w], total_ms=ticks[s,w]*1000d/Stopwatch.Frequency, max_ms=maxima[s,w]*1000d/Stopwatch.Frequency }).ToArray() }).ToArray(),
        raster_events=rasterKeys.Where(k=>k.Step>0).Select(k=>new {step=k.Step,width=k.Size.Width,height=k.Size.Height,caller=k.Caller.ToString()}).ToArray(),
        wallpaper=Enumerable.Range(0,7).SelectMany(k=>Enumerable.Range(0,256).Where(a=>wallpaperCalls[k,a]>0)
            .Select(a=>new {kind=new[]{"Root","Table","Split","SplitPanel","Grid","Map","Other"}[k],wash=a,
                calls=wallpaperCalls[k,a],total_ms=wallpaperTicks[k,a]*1000d/Stopwatch.Frequency})).ToArray(),
        root_cache=Enumerable.Range(0,4).Select(i=>new{code=((CacheResult)i).ToString(),count=rootCache[i]}).ToArray(),
        deferred_panel_erases=deferredPanels
    };
    internal readonly struct WallpaperScope : IDisposable
    {
        private readonly ResizeWorkProbe? probe;
        private readonly int kind, wash;
        private readonly long started;
        internal WallpaperScope(ResizeWorkProbe probe, int kind, int wash)
        { this.probe=probe; this.kind=kind;this.wash=wash;started=Stopwatch.GetTimestamp(); }
        public void Dispose()
        { if(probe!=null){probe.wallpaperTicks[kind,wash]+=Stopwatch.GetTimestamp()-started;probe.wallpaperCalls[kind,wash]++;} }
    }
    internal readonly struct Scope : IDisposable
    {
        private readonly ResizeWorkProbe? probe;
        private readonly Work work, previous;
        private readonly int step;
        private readonly long started;
        internal Scope(ResizeWorkProbe probe, Work work)
        { this.probe=probe;this.work=work;previous=probe.current;step=probe.step;started=Stopwatch.GetTimestamp();probe.current=work; }
        public void Dispose()
        {
            if(probe==null)return;
            long elapsed=Stopwatch.GetTimestamp()-started;
            probe.ticks[step,(int)work]+=elapsed;probe.counts[step,(int)work]++;
            probe.maxima[step,(int)work]=Math.Max(probe.maxima[step,(int)work],elapsed);probe.current=previous;
        }
    }
}

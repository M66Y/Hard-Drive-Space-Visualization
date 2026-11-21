using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Owned STA-window lifecycle only. This does not exercise minimize/restore,
// monitor changes, desktop pixels or user input, and is not visual acceptance.
internal static class NativeResizeHostLifecycleTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(bool dpi96 = false)
    {
        int checks = 0;
        string failure = "None";
        bool cleanupDeferred = false;
        Action? requestClose = null;
        NativeResizeFrame? lastFrame = null;
        ResizeRootCoverageHook? hook = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new LiveResizePrototypeForm(ResizeNativeMode.Canvas) { ShowInTaskbar = false };
                form.EnableCoverageCandidate(true);
                hook = Field<ResizeRootCoverageHook>(form, "coverageHook") ?? throw new Failure("HostCoverageHookMissing");
                requestClose = () =>
                {
                    try
                    {
                        if (!form.IsDisposed && form.IsHandleCreated)
                            form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
                    }
                    catch (InvalidOperationException) { }
                };
                form.Shown += async (_, _) =>
                {
                    try { lastFrame = await CheckRecreation(form, hook, Check, deadline.Token); }
                    catch (Failure error) { failure = error.Code; }
                    catch (OperationCanceledException) { failure = "HostLifecycleDeadline"; }
                    catch (TargetInvocationException) { failure = "HostRecreateInvocationFailed"; }
                    catch { failure = "UnexpectedHostLifecycleFailure"; }
                    finally { if (!form.IsDisposed) form.Close(); }
                };
                Application.Run(form);
                if (failure == "None")
                {
                    Check(form.IsDisposed && form.BufferBytes == 0, "ClosedHostReleasesAllPixelStorage");
                    Check(lastFrame != null && lastFrame.IsDisposed && lastFrame.OwnedBytes == 0, "ClosedHostDisposesLatestNativeFrame");
                    Check(hook.Handle == IntPtr.Zero, "ClosedHostDetachesCoverageHook");
                    Check(Field<NativeResizeFrame>(form, "nativeFrame") == null && Field<object>(form, "frame") == null &&
                        Field<object>(form, "frameGraphics") == null, "ClosedHostRetainsNoNativeFrameAliases");
                }
            }
            catch (Failure error) { failure = error.Code; }
            catch { failure = "UnexpectedHostWorkerFailure"; }
        }) { IsBackground = true, Name = "NativeResizeHostLifecycle" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(34)))
        {
            deadline.Cancel(); requestClose?.Invoke();
            cleanupDeferred = !worker.Join(TimeSpan.FromSeconds(3));
            failure = "HostWorkerDeadline";
        }
        Console.WriteLine($"NATIVE_HOST lifecycle_checks={checks}; cleanup_deferred={cleanupDeferred.ToString().ToLowerInvariant()}; dpi96={dpi96}; visual_acceptance=separate");
        if (failure != "None") { Console.Error.WriteLine($"FAIL native resize host lifecycle: {failure}"); return 1; }
        Console.WriteLine("PASS native resize host lifecycle; owned window generations only; no minimize or monitor testing");
        return 0;

        void Check(bool condition, string code)
        { if (!condition) throw new Failure(code); checks++; }
    }

    private static async Task<NativeResizeFrame> CheckRecreation(LiveResizePrototypeForm form,
        ResizeRootCoverageHook hook, Action<bool, string> check, CancellationToken token)
    {
        await Wait(() => form.FrameCount > 0 && form.PresentedLayout?.PixelSize == form.ClientSize &&
            Field<NativeResizeFrame>(form, "nativeFrame") != null, token);
        check(form.UsesNativeFrame && form.IsHandleCreated && IsWindow(form.Handle), "DibHostHasLiveRootHandle");
        var recreate = typeof(Control).GetMethod("RecreateHandle", Hidden) ?? throw new Failure("RecreateHandleUnavailable");
        var invalidateEdges = typeof(LiveResizePrototypeForm).GetMethod("InvalidateAnimatedEdges", Hidden)
            ?? throw new Failure("AnimatedEdgeInvalidationUnavailable");
        var changed = typeof(LiveResizePrototypeForm).GetMethod("Changed", Hidden)
            ?? throw new Failure("HostStateChangeUnavailable");
        var generationField = typeof(ResizeRootCoverageHook).GetField("handleGeneration", Hidden)
            ?? throw new Failure("CoverageGenerationUnavailable");
        var seenFrames = new List<NativeResizeFrame>(3);
        NativeResizeFrame? latest = null;
        foreach (var mode in new[] { ResizeNativeMode.Canvas, ResizeNativeMode.All })
        {
            int beforeMode = form.FrameCount;
            form.SetMode(mode);
            await Wait(() => form.FrameCount > beforeMode && form.PresentedLayout?.PixelSize == form.ClientSize, token);
            var visibleTile = form.PresentedLayout!.Tiles.FirstOrDefault(tile => tile.RowIndex >= 0 &&
                tile.Bounds.Width > 16 && tile.Bounds.Height > 16) ?? throw new Failure("AnimatedTileFixtureUnavailable");
            form.SceneState.Selected = visibleTile.RowIndex;
            changed.Invoke(form, [false]);
            await Wait(() => form.PresentedLayout?.State.Selected == visibleTile.RowIndex, token);
            int childCount = mode == ResizeNativeMode.Canvas ? 0 : 4;
            check(form.Controls.Count == childCount, "HostModeHasExpectedNativeControls");
            var oldFrame = Field<NativeResizeFrame>(form, "nativeFrame") ?? throw new Failure("ActiveHostFrameMissing");
            check(!oldFrame.IsDisposed && oldFrame.OwnedBytes > 0, "OldGenerationOwnsLivePixelStorage");
            check(form.BufferBytes > 0 && form.BufferBytes <= LiveResizePrototypeForm.MaxBufferBytes, "HostCombinedFrameAndCacheBudgetBounded");
            IntPtr oldHandle = form.Handle;
            int oldFrameCount = form.FrameCount;
            long oldGeneration = (long)generationField.GetValue(hook)!;
            int destroyed = 0, created = 0;
            int createdEdgeInvalidations = 0;
            bool recreatedEdgeWasQueued = false;
            bool releasedAtDestroy = false, invalidatedAtDestroy = false, hookDetachedAtDestroy = false;
            EventHandler observeDestroyed = (_, _) =>
            {
                destroyed++;
                releasedAtDestroy = oldFrame.IsDisposed && oldFrame.OwnedBytes == 0 &&
                    Field<NativeResizeFrame>(form, "nativeFrame") == null && Field<object>(form, "frame") == null &&
                    Field<object>(form, "frameGraphics") == null;
                invalidatedAtDestroy = Field<object>(form, "pendingLayout") == null &&
                    Field<object>(form, "preparedLayout") == null && Field<object>(form, "animationDirty") == null &&
                    Field<object>(form, "renderedLayout") == null;
                hookDetachedAtDestroy = hook.Handle == IntPtr.Zero;
            };
            EventHandler observeCreated = (_, _) =>
            {
                created++;
                // Queue exactly one ordinary edge Invalidate in this callback;
                // do not Refresh/Update or synchronously paint the new handle.
                invalidateEdges.Invoke(form, null);
                createdEdgeInvalidations++;
                recreatedEdgeWasQueued = Field<Region>(form, "animationDirty") != null;
            };
            form.HandleDestroyed += observeDestroyed;
            form.HandleCreated += observeCreated;
            try { recreate.Invoke(form, null); }
            finally
            {
                form.HandleDestroyed -= observeDestroyed;
                form.HandleCreated -= observeCreated;
            }
            check(destroyed == 1 && created == 1, "RootHandleDestroyedAndRecreatedExactlyOnce");
            check(createdEdgeInvalidations == 1 && recreatedEdgeWasQueued, "RecreatedHandleQueuesOneAnimatedEdgeInvalidation");
            check(releasedAtDestroy, "HandleDestructionReleasesOldNativeStorageBeforeEvent");
            check(invalidatedAtDestroy, "HandleDestructionInvalidatesPreparedAndDirtyState");
            check(hookDetachedAtDestroy, "CoverageHookDetachesFromDestroyedRoot");
            check(oldFrame.IsDisposed && oldFrame.OwnedBytes == 0, "OldGenerationCannotRemainLiveAfterRecreation");
            check(form.IsHandleCreated && form.Handle != IntPtr.Zero && IsWindow(form.Handle), "RecreatedRootHandleIsLive");
            // Windows may recycle numeric HWND values after destruction. The
            // lifecycle events, frame identity and hook generation establish
            // that this is a new lifetime, without assuming numeric uniqueness.
            check(oldHandle != IntPtr.Zero && (long)generationField.GetValue(hook)! >= oldGeneration + 2,
                "CoverageGenerationAdvancesAcrossRootRecreation");
            check(ReferenceEquals(Field<ResizeRootCoverageHook>(form, "coverageHook"), hook) && hook.Handle == form.Handle,
                "ExistingCoverageOwnerAttachesToNewRootLifetime");
            await Wait(() => form.FrameCount > oldFrameCount && form.PresentedLayout?.PixelSize == form.ClientSize &&
                Field<NativeResizeFrame>(form, "nativeFrame") is { IsDisposed: false }, token);
            latest = Field<NativeResizeFrame>(form, "nativeFrame") ?? throw new Failure("RecreatedNativeFrameMissing");
            check(!ReferenceEquals(latest, oldFrame) && seenFrames.All(frame => !ReferenceEquals(frame, latest)),
                "RecreatedRootPublishesFreshNativeFrameObject");
            check(latest.Capacity.Width >= form.ClientSize.Width && latest.Capacity.Height >= form.ClientSize.Height &&
                latest.Dpi == form.DeviceDpi && latest.OwnedBytes > 0, "FreshFrameMatchesCurrentRootGeometryAndDpi");
            check(form.PresentedLayout!.PixelSize == form.ClientSize && form.FrameFailure == null,
                "FreshGenerationPublishesValidCurrentLayout");
            long nonOpaquePixels = NonOpaquePixels(latest.Bitmap, form.ClientSize);
            Console.WriteLine($"NATIVE_HOST recreated_client_nonopaque_pixels={nonOpaquePixels}; native_mode={mode}");
            check(nonOpaquePixels == 0, "RecreatedHostFrameIsOpaqueAcrossActualClient");
            check(form.BufferBytes <= LiveResizePrototypeForm.MaxBufferBytes, "RecreatedGenerationRespectsCombinedBudget");
            check(form.Controls.Count == childCount && form.Controls.Cast<Control>().All(control => !control.IsDisposed),
                "ManagedNativeControlsSurviveRootRecreation");
            seenFrames.Add(oldFrame);
        }
        check(seenFrames.Count == 2 && seenFrames.All(frame => frame.IsDisposed && frame.OwnedBytes == 0),
            "AllPreviousHostGenerationsRemainReleased");
        CheckPrintIsolation(form, check);
        CheckExposedPixels(form, check);
        return CheckSynchronousCachedRefill(form, check);
    }

    private static void CheckPrintIsolation(LiveResizePrototypeForm form, Action<bool, string> check)
    {
        var prepare = typeof(LiveResizePrototypeForm).GetMethod("PrepareNextGeometryFrame", Hidden)!;
        var print = typeof(LiveResizePrototypeForm).GetMethod("OnPrint", Hidden)!;
        prepare.Invoke(form, [form.ClientSize]);
        var prepared = Field<ResizeSceneLayout>(form, "preparedLayout");
        check(prepared != null, "PrintIsolationHasPreparedRootFrame");
        var presented = form.PresentedLayout;
        int rootFrames = form.FrameCount;
        object? committed = typeof(LiveResizePrototypeForm).GetField("preparedCommitted", Hidden)!.GetValue(form);
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        using var args = new PaintEventArgs(graphics, new Rectangle(0, 0, 64, 64));
        print.Invoke(form, [args]);
        check(form.FrameCount == rootFrames && ReferenceEquals(form.PresentedLayout, presented), "PrintingCannotCommitRootFrame");
        check(ReferenceEquals(prepared, Field<ResizeSceneLayout>(form, "preparedLayout")) &&
            Equals(committed, typeof(LiveResizePrototypeForm).GetField("preparedCommitted", Hidden)!.GetValue(form)),
            "PrintingCannotConsumePendingRootPresentation");
        check((int)typeof(LiveResizePrototypeForm).GetField("printDepth", Hidden)!.GetValue(form)! == 0,
            "PrintScopeRestoresRootPaintPurpose");
    }

    private static void CheckExposedPixels(LiveResizePrototypeForm form, Action<bool, string> check)
    {
        // Synchronous geometry changes, no Refresh/Update/DoEvents between the
        // HWND move and independent GDI readback. Natural later paint cannot
        // accidentally satisfy this witness as it did in the C49 baseline.
        for (int i = 0; i < 2; i++)
        {
            form.SetWindowSize(false);
            var oldGrid = form.Controls.OfType<DataGridView>().Single().Bounds;
            form.SetWindowSize(true);
            var grid = form.Controls.OfType<DataGridView>().Single().Bounds;
            var point = new Point(form.ClientSize.Width - 2, form.ClientSize.Height * 2 / 3);
            check(oldGrid.Contains(point) && !grid.Contains(point), "ResizeWitnessWasExposedByOldGrid");
            var frame = Field<NativeResizeFrame>(form, "nativeFrame")!;
            var layout = Field<ResizeSceneLayout>(form, "renderedLayout");
            check(layout?.PixelSize == form.ClientSize, "ExposedPixelUsesCurrentFrameSize");
            IntPtr dc = GetDC(form.Handle);
            try
            {
                check(dc != IntPtr.Zero && PtVisible(dc, point.X, point.Y), "ExposedWitnessBelongsToVisibleRoot");
                Color expected = frame.Bitmap.GetPixel(point.X, point.Y);
                uint rgb = (uint)(expected.R | expected.G << 8 | expected.B << 16);
                check(GetPixel(dc, point.X, point.Y) == rgb, "ExposedWitnessRestoredBeforeNextNaturalPaint");
            }
            finally { if (dc != IntPtr.Zero) ReleaseDC(form.Handle, dc); }
        }
    }

    private static NativeResizeFrame CheckSynchronousCachedRefill(LiveResizePrototypeForm form, Action<bool, string> check)
    {
        // No await, DoEvents, window invalidation or synchronous UI paint in
        // this fixture: its geometry/state/cache key cannot change mid-case.
        var compose = typeof(LiveResizePrototypeForm).GetMethod("ComposeScene", Hidden)
            ?? throw new Failure("HostComposeSceneUnavailable");
        var release = typeof(LiveResizePrototypeForm).GetMethod("ReleaseFrame", Hidden)
            ?? throw new Failure("HostReleaseFrameUnavailable");
        var ensure = typeof(LiveResizePrototypeForm).GetMethod("EnsureFrame", Hidden)
            ?? throw new Failure("HostEnsureFrameUnavailable");
        var layout = ResizeSceneLayout.Build(form.ClientSize, form.DeviceDpi, form.SceneState);
        var selectedTile = layout.Tiles.FirstOrDefault(tile => tile.RowIndex >= 0 &&
            tile.Bounds.Width > 16 && tile.Bounds.Height > 16) ?? throw new Failure("SynchronousEdgeFixtureUnavailable");
        compose.Invoke(form, [layout, null]);
        var cache = Field<ResizeSceneBaseCache>(form, "baseCache") ?? throw new Failure("HostStaticCacheUnavailable");
        check(cache.RetainedBytes > 0, "SynchronousRefillHasWarmStaticScene");
        long cacheHits = cache.Hits;
        var old = Field<NativeResizeFrame>(form, "nativeFrame") ?? throw new Failure("SynchronousOldFrameUnavailable");
        release.Invoke(form, null);
        check(old.IsDisposed && old.OwnedBytes == 0 && Field<object>(form, "renderedLayout") == null,
            "SynchronousReleaseInvalidatesOldFramePixels");
        ensure.Invoke(form, [form.ClientSize, form.DeviceDpi]);
        var fresh = Field<NativeResizeFrame>(form, "nativeFrame") ?? throw new Failure("SynchronousFreshFrameUnavailable");
        check(!ReferenceEquals(old, fresh) && !fresh.IsDisposed && Field<object>(form, "renderedLayout") == null,
            "SynchronousEnsureCreatesUnrenderedFreshDib");
        using var edge = new Region(selectedTile.Bounds);
        float inset = 8 * form.DeviceDpi / 96f;
        var inner = RectangleF.Inflate(selectedTile.Bounds, -inset, -inset);
        check(inner.Width > 0 && inner.Height > 0, "SynchronousEdgeRegionHasUnpaintedInterior");
        edge.Exclude(inner);
        compose.Invoke(form, [layout, edge]);
        check(cache.Hits > cacheHits, "SynchronousRefillDefinitelyHitsStaticCache");
        // Flush only the in-memory drawing commands before reading the DIB;
        // this neither invalidates nor repaints the actual window.
        fresh.Graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        long nonOpaquePixels = NonOpaquePixels(fresh.Bitmap, form.ClientSize);
        Console.WriteLine($"NATIVE_HOST synchronous_cache_refill_nonopaque_pixels={nonOpaquePixels}");
        check(nonOpaquePixels == 0, "EmptyDibWithEdgeClipReceivesOpaqueFullClientScene");
        check(ReferenceEquals(Field<ResizeSceneLayout>(form, "renderedLayout"), layout),
            "FullRefillPublishesValidPixelLayout");
        check(form.BufferBytes <= LiveResizePrototypeForm.MaxBufferBytes, "SynchronousRefillKeepsCombinedPixelBudget");
        return fresh;
    }

    private static T? Field<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, Hidden)?.GetValue(owner) as T;

    private static long NonOpaquePixels(Bitmap frame, Size clientSize)
    {
        var bounds = new Rectangle(Point.Empty, clientSize);
        var pixels = frame.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        long nonOpaque = 0;
        try
        {
            var row = new byte[clientSize.Width * 4];
            for (int y = 0; y < clientSize.Height; y++)
            {
                Marshal.Copy(pixels.Scan0 + y * pixels.Stride, row, 0, row.Length);
                for (int offset = 3; offset < row.Length; offset += 4)
                    if (row[offset] != byte.MaxValue) nonOpaque++;
            }
        }
        finally { frame.UnlockBits(pixels); }
        return nonOpaque;
    }

    private static async Task Wait(Func<bool> predicate, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new Failure("HostExpectedFrameDeadline");
            await Task.Delay(12, token);
        }
    }

    private sealed class Failure(string code) : Exception { internal string Code { get; } = code; }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtVisible(IntPtr dc, int x, int y);
}

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Synthetic drawing/state contracts and an owned window only. Passing these
// checks is not evidence that desktop resize frames are visually flicker-free.
internal static class ResizePrototypeTests
{
    internal static int Run(bool dpi96 = false)
    {
        int checks = 0;
        string failure = "None";
        bool cleanupDeferred = false;
        Action? requestClose = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(48));
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ModelAndDrawingChecks(Check);
                using var form = new LiveResizePrototypeForm(ResizeNativeMode.Canvas)
                {
                    ShowInTaskbar = false
                };
                requestClose = () =>
                {
                    try
                    {
                        if (form.IsHandleCreated && !form.IsDisposed)
                            form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
                    }
                    catch (InvalidOperationException) { }
                };
                form.Shown += async (_, _) =>
                {
                    try { await WindowChecks(form, Check, deadline.Token); }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch (OperationCanceledException) { failure = "WindowDeadline"; }
                    catch { failure = "UnexpectedWindowFailure"; }
                    finally { if (!form.IsDisposed) form.Close(); }
                };
                Application.Run(form);
                Check(form.IsDisposed, "OwnedWindowDisposed");
                Check(form.BufferBytes == 0, "OwnedBufferReleasedOnClose");
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "UnexpectedWorkerFailure"; }
        }) { IsBackground = true, Name = "ResizePrototypeChecks" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(53)))
        {
            deadline.Cancel();
            requestClose?.Invoke();
            cleanupDeferred = !worker.Join(TimeSpan.FromSeconds(2));
            failure = "WorkerDeadline";
        }
        Console.WriteLine($"RESIZE prototype-checks cleanup_deferred={cleanupDeferred.ToString().ToLowerInvariant()}; desktop_visual_acceptance=separate");
        if (failure != "None")
        {
            Console.Error.WriteLine($"FAIL resize prototype: {failure}; checks={checks}");
            return 1;
        }
        Console.WriteLine($"PASS resize prototype: {checks}; synthetic scene and owned window; no disk scan or system-setting changes");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            checks++;
        }
    }

    private static void ModelAndDrawingChecks(Action<bool, string> check)
    {
        Rejects(() => ResizeSceneLayout.Build(new Size(-1, 50), 96, new()), "NegativeGeometryRejected");
        Rejects(() => ResizeSceneLayout.Build(new Size(32769, 50), 96, new()), "UnboundedGeometryRejected");
        Rejects(() => ResizeSceneLayout.Build(new Size(500, 500), 0, new()), "InvalidDpiRejected");
        foreach (int dpi in new[] { 96, 144 })
        {
            int Scale(int value) => (int)Math.Round(value * dpi / 96d);
            var size = new Size(Scale(1440), Scale(900));
            var state = new ResizeSceneState
            {
                View = 1, Selected = 5, Hover = 6, Scroll = 0, Metric = 0,
                Search = "", Progress = .45, Revision = 17
            };
            var scene = ResizeSceneLayout.Build(size, dpi, state);
            check(scene.PixelSize == size && scene.Dpi == dpi && scene.Revision == 17, "SceneGeometryVersion");
            check(scene.Data.Count > 10 && scene.Data.Count <= 256, "SceneDataBounded");
            check(scene.Data.Select(row => row.Id).Distinct().Count() == scene.Data.Count, "SourceIdentifiersUnique");
            check(!ReferenceEquals(scene.State, state), "FrameOwnsStateSnapshot");
            check(state.View == 1 && state.Scroll == 0 && state.Revision == 17 && state.Search == "", "BuildDoesNotMutateInput");
            var client = new Rectangle(Point.Empty, size);
            foreach (Rectangle bounds in new[] { scene.Sidebar, scene.Search, scene.Strategy, scene.Metric,
                scene.MapPanel, scene.ListPanel, scene.MapBody, scene.ListBody, scene.ListHeader,
                scene.Details, scene.ProgressBounds })
                check(bounds.Width >= 0 && bounds.Height >= 0 && client.Contains(bounds), "SceneRegionsStayInClient");
            check(scene.Rows.Count > 0 && scene.Rows.Count <= scene.Data.Count, "VisibleRowsBounded");
            check(scene.Rows.All(row => scene.ListBody.IntersectsWith(row.Bounds) &&
                row.Bounds.Left >= scene.ListBody.Left && row.Bounds.Right <= scene.ListBody.Right &&
                row.Bounds.Top >= scene.ListBody.Top && row.Bounds.Bottom < scene.ListBody.Bottom + scene.RowHeight),
                "VisibleRowGeometryIntersectsOnlyList");
            check(scene.Rows.All(row => scene.Data.Any(data => data.Id == row.RowIndex)), "RowsUseSourceIdentity");
            check(scene.Tiles.All(tile => tile.Bounds.Width > 0 && tile.Bounds.Height > 0 && Contains(scene.MapBody, tile.Bounds)),
                "TilesClippedToMap");
            check(scene.Tiles.Where(tile => tile.RowIndex >= 0).All(tile => scene.Data.Any(row => row.Id == tile.RowIndex)),
                "TilesUseSourceIdentity");
            check(scene.Tiles.Sum(tile => tile.Item.Size) == scene.Data.Sum(row => row.Bytes(0)), "MapByteTotalsConserved");
            for (int i = 1; i < scene.Rows.Count; i++)
                check(scene.Rows[i - 1].Bounds.Bottom <= scene.Rows[i].Bounds.Top, "VisibleRowsDoNotOverlap");

            var minimum = ResizeSceneLayout.Build(new Size(Scale(1120), Scale(780)), dpi, state);
            check(minimum.Data.Select(row => row.Id).SequenceEqual(scene.Data.Select(row => row.Id)), "ResizePreservesSourceIdentities");
            check(minimum.State.Selected == scene.State.Selected && minimum.State.Hover == scene.State.Hover,
                "ResizePreservesSelectionIdentity");
            string originalName = scene.Data[0].Name;
            state.Search = "__no_such_resize_demo_entry__";
            state.Selected = 77;
            state.Scroll = int.MaxValue;
            state.Revision = 18;
            check(scene.State.Search == "" && scene.State.Selected == 5 && scene.State.Scroll == 0 && scene.Revision == 17,
                "PublishedLayoutUnaffectedByStateMutation");
            var empty = ResizeSceneLayout.Build(size, dpi, state);
            check(empty.Data.Count == 0 && empty.Rows.Count == 0 && empty.Tiles.Count == 0, "UnknownSearchIsEmpty");
            check(state.Scroll == int.MaxValue, "ClampingDoesNotMutateInput");
            state.Search = originalName;
            state.Scroll = 0;
            var found = ResizeSceneLayout.Build(size, dpi, state);
            check(found.Data.Any(row => row.Name == originalName), "SearchCanFindFullName");
            check(found.Data.All(row => row.Name.Contains(originalName, StringComparison.OrdinalIgnoreCase) ||
                row.Path.Contains(originalName, StringComparison.OrdinalIgnoreCase)), "SearchScopeConsistent");
            state.Search = "";
            state.Scroll = int.MaxValue;
            var end = ResizeSceneLayout.Build(size, dpi, state);
            check(end.Rows.Count > 0 && end.Rows.Count <= end.Data.Count && end.MaxScroll >= 0, "ExtremeScrollRemainsBounded");
            check(end.Rows.Any(row => row.RowIndex == end.Data[^1].Id), "ExtremeScrollReachesLastRow");
            state.Scroll = -999;
            var start = ResizeSceneLayout.Build(size, dpi, state);
            check(start.Rows.Count > 0 && start.Rows[0].RowIndex == start.Data[0].Id, "NegativeScrollClampsAtStart");

            using var wallpaper = SyntheticWallpaper();
            using var renderer = new ResizeSceneRenderer();
            using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            image.SetResolution(dpi, dpi);
            var sentinel = Color.FromArgb(17, 239, 173);
            var dirty = new Rectangle(0, 0, size.Width / 2, size.Height / 2);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(sentinel);
                graphics.SetClip(dirty);
                renderer.Render(graphics, scene, state, wallpaper, null, .5);
                check(graphics.ClipBounds == dirty, "RendererRestoresCallerClip");
            }
            check(image.GetPixel(size.Width - 2, size.Height - 2).ToArgb() == sentinel.ToArgb(), "RendererDoesNotEscapeDirtyClip");
            check(image.GetPixel(2, 2).ToArgb() != sentinel.ToArgb(), "RendererFillsDirtyBackground");

            // Selection/progress can change, but the same scene must continue
            // to render its own captured state, not the mutated caller state.
            using var first = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            first.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(first)) renderer.Render(graphics, scene, state, wallpaper, null, .5);
            using (var reference = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                reference.SetResolution(dpi, dpi);
                using (var graphics = Graphics.FromImage(reference))
                {
                    graphics.Clear(Theme.Background);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(wallpaper, Wallpaper.CoverBounds(wallpaper, size, Point.Empty));
                }
                // These gutters are outside all modules. Their pixels must
                // continue to use the full-window wallpaper transform.
                foreach (int x in new[] { scene.Sidebar.Right + Scale(8), size.Width - Scale(8) })
                foreach (int y in new[] { Scale(12), size.Height / 2, size.Height - Scale(12) })
                    check(first.GetPixel(x, y).ToArgb() == reference.GetPixel(x, y).ToArgb(), "WallpaperUsesUnifiedWindowCoordinates");
            }
            state.Selected = 0; state.Progress = .99; state.Search = "mutated";
            using var second = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            second.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(second)) renderer.Render(graphics, scene, state, wallpaper, null, .5);
            check(SamePixels(first, second), "RendererUsesFrameSnapshotOnly");

            foreach (int view in new[] { 0, 1, 2 })
            {
                var viewState = new ResizeSceneState { View = view, Metric = 1, Progress = .4, Revision = 100 + view };
                var layout = ResizeSceneLayout.Build(size, dpi, viewState);
                check(layout.Rows.All(row => layout.Data.Any(data => data.Id == row.RowIndex)), "ViewRowsPreserveIdentity");
                check(layout.Tiles.Sum(tile => tile.Item.Size) == layout.Data.Sum(row => row.Bytes(1)), "MetricTotalsConservedPerView");
            }
        }

        void Rejects(Action action, string code)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, code);
        }
    }

    private static async Task WindowChecks(LiveResizePrototypeForm form, Action<bool, string> check, CancellationToken token)
    {
        await Wait(() => form.FrameCount > 0 && form.PresentedLayout?.PixelSize == form.ClientSize, token);
        check(form.Controls.Count == 0, "CanvasHasNoManagedChildren");
        check(DescendantWindowCount(form.Handle) == 0, "CanvasHasSingleClientHwnd");
        check(form.BufferBytes > 0 && form.BufferBytes <= 128L * 1024 * 1024, "CanvasBufferBounded");
        var presentedFrame = typeof(LiveResizePrototypeForm).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(form) as Bitmap ?? throw new CheckFailure("PresentedFrameProbeUnavailable");
        var presentedBounds = new Rectangle(Point.Empty, form.ClientSize);
        long nonOpaquePixels = 0;
        var pixels = presentedFrame.LockBits(presentedBounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var scanline = new byte[presentedBounds.Width * 4];
            for (int y = 0; y < presentedBounds.Height; y++)
            {
                Marshal.Copy(pixels.Scan0 + y * pixels.Stride, scanline, 0, scanline.Length);
                for (int x = 3; x < scanline.Length; x += 4)
                    if (scanline[x] != byte.MaxValue) nonOpaquePixels++;
            }
        }
        finally { presentedFrame.UnlockBits(pixels); }
        Console.WriteLine($"RESIZE prototype-checks presented_alpha_non255_pixels={nonOpaquePixels}; synthetic_client_only=true");
        check(nonOpaquePixels == 0, "PresentedFrameOpaque");
        var ensureFrame = typeof(LiveResizePrototypeForm).GetMethod("EnsureFrame", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new CheckFailure("FrameBudgetProbeUnavailable");
        long originalBytes = form.BufferBytes;
        var originalLayout = form.PresentedLayout;
        int originalFrames = form.FrameCount;
        RejectFrame(new Size(8192, 8192), "FrameBudget", "OversizedBufferRejected");
        RejectFrame(Size.Empty, "FrameSize", "EmptyBufferRejected");
        check(form.BufferBytes == originalBytes && ReferenceEquals(form.PresentedLayout, originalLayout) && form.FrameCount == originalFrames,
            "RejectedFrameDoesNotPublishOrReleaseValidFrame");
        int frames = form.FrameCount;
        long revision = form.SceneState.Revision;
        foreach (var (mode, textCount, comboCount, gridCount) in new[]
        {
            (ResizeNativeMode.Text, 1, 0, 0), (ResizeNativeMode.Combo, 0, 2, 0),
            (ResizeNativeMode.Grid, 0, 0, 1), (ResizeNativeMode.All, 1, 2, 1)
        })
        {
            var previous = form.Controls.Cast<Control>().ToArray();
            frames = form.FrameCount;
            form.SetMode(mode);
            await Wait(() => form.FrameCount > frames && form.PresentedLayout?.PixelSize == form.ClientSize, token);
            check(form.Controls.OfType<TextBox>().Count() == textCount, "NativeTextModeCount");
            check(form.Controls.OfType<ComboBox>().Count() == comboCount, "NativeComboModeCount");
            check(form.Controls.OfType<DataGridView>().Count() == gridCount, "NativeGridModeCount");
            check(form.Controls.Count == textCount + comboCount + gridCount, "NativeModeHasOnlyExpectedChildren");
            check(previous.All(control => control.IsDisposed), "PreviousNativeChildrenDisposed");
            check(DescendantWindowCount(form.Handle) > 0, "MixedModeIsExplicitlyMultiHwnd");
            check(form.Controls.Cast<Control>().All(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                form.ClientRectangle.Contains(control.Bounds)), "NativeBoundsStayInClient");
            if (textCount > 0)
            {
                var text = form.Controls.OfType<TextBox>().Single();
                check(form.PresentedLayout!.Search.Contains(text.Bounds), "SearchUsesPresentedBounds");
                check(text.Focus() && text.Focused, "NativeTextCanReceiveFocus");
                text.Text = "__no_such_resize_demo_entry__";
                await Wait(() => form.PresentedLayout?.Data.Count == 0, token);
                check(form.SceneState.Search == text.Text, "NativeTextUpdatesScene");
                text.Text = "";
                await Wait(() => form.PresentedLayout?.Data.Count > 0, token);
            }
            if (comboCount > 0)
            {
                var metric = form.Controls.OfType<ComboBox>().Single(control => control.Name == "prototypeMetric");
                var strategy = form.Controls.OfType<ComboBox>().Single(control => control.Name == "prototypeStrategy");
                check(form.PresentedLayout!.Metric.Contains(metric.Bounds) &&
                    form.PresentedLayout.Strategy.Contains(strategy.Bounds), "CombosUsePresentedBounds");
                metric.SelectedIndex = 1;
                await Wait(() => form.PresentedLayout?.State.Metric == 1, token);
                check(form.SceneState.Metric == 1, "NativeMetricUpdatesScene");
                metric.SelectedIndex = 0;
                await Wait(() => form.PresentedLayout?.State.Metric == 0, token);
            }
            if (gridCount > 0)
            {
                var grid = form.Controls.OfType<DataGridView>().Single();
                check(grid.RowCount == form.PresentedLayout!.Data.Count, "NativeGridUsesSameFrameData");
                check(Rectangle.Union(form.PresentedLayout.ListHeader, form.PresentedLayout.ListBody).Contains(grid.Bounds),
                    "NativeGridUsesPresentedBounds");
                check(grid.RowCount > 10 && grid.Rows[2].Tag is int, "NativeGridHasStableRowIdentity");
                int id = (int)grid.Rows[2].Tag!;
                grid.ClearSelection(); grid.Rows[2].Selected = true;
                await Wait(() => form.PresentedLayout?.State.Selected == id, token);
                check(form.SceneState.Selected == id, "NativeGridSelectionUpdatesSceneIdentity");
                grid.FirstDisplayedScrollingRowIndex = 10;
                await Wait(() => grid.FirstDisplayedScrollingRowIndex > 0, token);
                check(grid.RowCount == form.PresentedLayout.Data.Count, "NativeScrollPreservesBoundedData");

                var tile = form.PresentedLayout.Tiles.FirstOrDefault(value => value.RowIndex >= 0 &&
                    value.RowIndex != form.SceneState.Selected) ?? throw new CheckFailure("SelectableTileFixtureUnavailable");
                int beforeClick = form.FrameCount;
                ClickOwnedForm(form, new Point((int)(tile.Bounds.Left + tile.Bounds.Width / 2),
                    (int)(tile.Bounds.Top + tile.Bounds.Height / 2)));
                await Wait(() => form.FrameCount > beforeClick && form.PresentedLayout?.State.Selected == tile.RowIndex, token);
                check(grid.SelectedRows.Cast<DataGridViewRow>().Any(row => row.Tag is int selected && selected == tile.RowIndex),
                    "CanvasTileSelectsCorrespondingNativeRow");

                form.SceneState.View = 1;
                ChangeOwnedForm(form, true);
                await Wait(() => form.PresentedLayout?.State.View == 1 && grid.RowCount == form.PresentedLayout.Data.Count, token);
                NativeNameFallbackChecks(form, grid, check);
                int beforePaint = form.FrameCount;
                var pending = typeof(LiveResizePrototypeForm).GetField("pendingLayout", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new CheckFailure("PendingFrameProbeUnavailable");
                pending.SetValue(form, null);
                grid.InvalidateCell(0, 0); form.Invalidate();
                await Wait(() => form.FrameCount > beforePaint && pending.GetValue(form) is ResizeSceneLayout, token);
                check(pending.GetValue(form) is ResizeSceneLayout cached && cached.PixelSize == form.ClientSize &&
                    cached.Revision == form.PresentedLayout!.Revision, "PaintCachesCurrentLayoutAfterProgressInvalidation");
                form.SceneState.View = 0;
                ChangeOwnedForm(form, true);
                await Wait(() => form.PresentedLayout?.State.View == 0, token);
            }
            var priorSize = form.ClientSize;
            frames = form.FrameCount;
            form.ClientSize = new Size(priorSize.Width + 19, priorSize.Height + 13);
            await Wait(() => form.FrameCount > frames && form.PresentedLayout?.PixelSize == form.ClientSize, token);
            check(form.PresentedLayout!.PixelSize == form.ClientSize, "ResizePublishesCurrentGeometry");
            check(form.Controls.Cast<Control>().All(control => form.ClientRectangle.Contains(control.Bounds)), "NativeBoundsFollowResize");
            form.ClientSize = priorSize;
            await Wait(() => form.PresentedLayout?.PixelSize == priorSize, token);
        }
        var last = form.Controls.Cast<Control>().ToArray();
        frames = form.FrameCount;
        form.SetMode(ResizeNativeMode.Canvas);
        await Wait(() => form.FrameCount > frames, token);
        check(last.All(control => control.IsDisposed), "AllNativeChildrenReleased");
        check(form.Controls.Count == 0 && DescendantWindowCount(form.Handle) == 0, "CanvasRestoresSingleClientHwnd");
        check(form.SceneState.Revision >= revision, "SceneRevisionNeverMovesBackwards");
        await BottomRowHitChecks(form, check, token);
        bool fullWindowDrag = false;
        bool queried = SystemParametersInfo(0x0026, 0, ref fullWindowDrag, 0);
        Console.WriteLine($"RESIZE prototype-checks drag_full_windows={(queried ? fullWindowDrag.ToString().ToLowerInvariant() : "unknown")}; queried_only=true; driven_sweep_is_not_mouse_drag=true");

        void RejectFrame(Size size, string expectedCode, string assertion)
        {
            bool rejected = false;
            try { ensureFrame.Invoke(form, [size, form.DeviceDpi]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException inner && inner.Message == expectedCode)
            { rejected = true; }
            check(rejected, assertion);
        }
    }

    private static async Task BottomRowHitChecks(LiveResizePrototypeForm form, Action<bool, string> check, CancellationToken token)
    {
        form.SceneState.View = 1;
        ChangeOwnedForm(form, false);
        await Wait(() => form.PresentedLayout?.State.View == 1, token);
        var layout = form.PresentedLayout!;
        var originalSize = form.ClientSize;
        // Leave one visible pixel of the final row, so its full logical bounds
        // extend beyond the viewport into the details area below it.
        int addition = (layout.RowHeight + 1 - layout.ListBody.Height % layout.RowHeight) % layout.RowHeight;
        form.ClientSize = new Size(originalSize.Width, originalSize.Height + addition);
        await Wait(() => form.PresentedLayout?.PixelSize == form.ClientSize, token);
        layout = form.PresentedLayout!;
        var partial = layout.Rows.LastOrDefault(row => row.Bounds.Bottom > layout.Details.Top + 2)
            ?? throw new CheckFailure("PartialRowHitFixtureUnavailable");
        var point = new Point(partial.Bounds.Left + Math.Max(1, partial.Bounds.Width / 2), layout.Details.Top + 1);
        check(partial.Bounds.Contains(point) && layout.Details.Contains(point) && !layout.ListBody.Contains(point),
            "PartialRowFixtureReallyOverlapsDetailsOutsideViewport");
        int selected = layout.Data.First(row => row.Id != partial.RowIndex).Id;
        form.SceneState.Selected = selected;
        ChangeOwnedForm(form, false);
        await Wait(() => form.PresentedLayout?.State.Selected == selected, token);
        ClickOwnedForm(form, point);
        check(form.SceneState.Selected == selected, "ClippedPartialRowCannotStealDetailsClick");
        form.ClientSize = originalSize;
        await Wait(() => form.PresentedLayout?.PixelSize == originalSize, token);
    }

    private static void NativeNameFallbackChecks(LiveResizePrototypeForm form, DataGridView grid, Action<bool, string> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = typeof(LiveResizePrototypeForm).GetField("pendingLayout", hidden)
            ?? throw new CheckFailure("PendingFrameProbeUnavailable");
        var callback = typeof(WallpaperGrid).GetMethod("OnCellPainting", hidden)
            ?? throw new CheckFailure("NativeCellPaintingProbeUnavailable");
        object? savedLayout = pending.GetValue(form);
        int row = Math.Max(0, grid.FirstDisplayedScrollingRowIndex);
        Rectangle cell = grid.GetCellDisplayRectangle(0, row, false);
        Rectangle clip = Rectangle.Intersect(grid.ClientRectangle,
            new Rectangle(cell.Left + cell.Width / 3, cell.Top, Math.Max(1, cell.Width / 3), cell.Height));
        check(!cell.IsEmpty && !clip.IsEmpty && form.PresentedLayout?.State.View == 1,
            "DetailedNativeCellFixtureVisible");
        try
        {
            using var normal = Paint(false);
            using var fallback = Paint(true);
            check(SamePixels(normal, fallback), "DetailedNativeNameUsesPresentedFrameWhenPendingIsNull");
        }
        finally { pending.SetValue(form, savedLayout); }

        Bitmap Paint(bool clearPending)
        {
            pending.SetValue(form, clearPending ? null : savedLayout);
            var image = new Bitmap(grid.Width, grid.Height, PixelFormat.Format32bppPArgb);
            image.SetResolution(grid.DeviceDpi, grid.DeviceDpi);
            var sentinel = Color.FromArgb(17, 239, 173);
            try
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(sentinel);
                graphics.SetClip(grid.ClientRectangle);
                var cellArgs = new DataGridViewCellPaintingEventArgs(grid, graphics, clip, cell, row, 0,
                    DataGridViewElementStates.None, grid[0, row].Value, grid[0, row].FormattedValue, "",
                    grid[0, row].InheritedStyle, grid.AdvancedCellBorderStyle, DataGridViewPaintParts.All);
                callback.Invoke(grid, [cellArgs]);
                check(cellArgs.Handled, clearPending ? "PendingNullStillUsesDetailedNameCallback" : "NormalDetailedNameCallbackHandled");
                var envelope = Rectangle.Intersect(grid.ClientRectangle, cell);
                bool outsideUnchanged = true;
                for (int y = envelope.Top; y < envelope.Bottom && outsideUnchanged; y++)
                for (int x = envelope.Left; x < envelope.Right; x++)
                    if (!clip.Contains(x, y) && image.GetPixel(x, y).ToArgb() != sentinel.ToArgb())
                    { outsideUnchanged = false; break; }
                check(outsideUnchanged, "DetailedNameCallbackPreservesDirtyClipDuringProgressInvalidation");
                return image;
            }
            catch { image.Dispose(); throw; }
        }
    }

    private static void ClickOwnedForm(LiveResizePrototypeForm form, Point point)
    {
        var method = typeof(LiveResizePrototypeForm).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new CheckFailure("OwnedClickProbeUnavailable");
        method.Invoke(form, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
    }

    private static void ChangeOwnedForm(LiveResizePrototypeForm form, bool rebindGrid)
    {
        var method = typeof(LiveResizePrototypeForm).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new CheckFailure("OwnedStateProbeUnavailable");
        method.Invoke(form, [rebindGrid]);
    }

    private static async Task Wait(Func<bool> predicate, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(6)) throw new CheckFailure("ExpectedFrameDeadline");
            await Task.Delay(12, token);
        }
    }

    private static bool Contains(Rectangle parent, RectangleF child) => child.Left >= parent.Left - .1f &&
        child.Top >= parent.Top - .1f && child.Right <= parent.Right + .1f && child.Bottom <= parent.Bottom + .1f;

    private static Bitmap SyntheticWallpaper()
    {
        var bitmap = new Bitmap(91, 63);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(35, 68, 108));
        using var warm = new SolidBrush(Color.FromArgb(242, 158, 87));
        graphics.FillRectangle(warm, 31, 0, 29, 63);
        using var pale = new SolidBrush(Color.FromArgb(248, 231, 210));
        graphics.FillRectangle(pale, 0, 28, 91, 13);
        return bitmap;
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        var bounds = new Rectangle(Point.Empty, first.Size);
        var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowA = new byte[first.Width * 4]; var rowB = new byte[first.Width * 4];
            for (int y = 0; y < first.Height; y++)
            {
                Marshal.Copy(a.Scan0 + y * a.Stride, rowA, 0, rowA.Length);
                Marshal.Copy(b.Scan0 + y * b.Stride, rowB, 0, rowB.Length);
                if (!rowA.AsSpan().SequenceEqual(rowB)) return false;
            }
            return true;
        }
        finally { first.UnlockBits(a); second.UnlockBits(b); }
    }

    private static int DescendantWindowCount(IntPtr owner)
    {
        int count = 0;
        EnumChildWindows(owner, (_, _) => { count++; return true; }, IntPtr.Zero);
        return count;
    }

    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr owner, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
}

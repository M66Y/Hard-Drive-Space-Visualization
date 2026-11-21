using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Explicit native-frame contracts only. All targets are owned memory DIBs;
// these checks do not open windows, capture the desktop or infer frame rates.
internal static class NativeResizeFrameTests
{
    private const int Sentinel = 0x17495b;

    internal static int Run()
    {
        int checks = 0;
        long comparedPixels = 0;
        try
        {
            foreach (int dpi in new[] { 96, 144 }) Cases(dpi);
            HandleCycles();
            Console.WriteLine($"PASS native resize frame: {checks}; pixels_compared={comparedPixels}; zero_tolerance=true; scene_comparisons=2; memory_only=true");
            return 0;
        }
        catch (Failure failure)
        { Console.Error.WriteLine($"FAIL native resize frame: {failure.Code}; checks={checks}"); return 1; }
        catch
        { Console.Error.WriteLine($"FAIL native resize frame: UnexpectedMemoryFrameFailure; checks={checks}"); return 1; }

        void Check(bool condition, string code)
        { if (!condition) throw new Failure(code); checks++; }

        void CompareRgb(MemoryTarget target, int[] source, Func<int, int, bool> copied, string code)
        {
            int[] actual = target.ReadRgb();
            long differences = 0;
            for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                int index = y * target.Width + x;
                int expected = copied(x, y) ? source[index] & 0x00ffffff : Sentinel;
                if (actual[index] != expected) differences++;
                comparedPixels++;
            }
            if (differences != 0) Console.WriteLine($"NATIVE_FRAME mismatch_code={code}; rgb_mismatch_pixels={differences}");
            Check(differences == 0, code);
        }

        void CompareRgba(Bitmap actual, int[] expected, string code)
        {
            int[] observed = ReadRgba(actual);
            long differences = observed.Zip(expected).LongCount(pair => pair.First != pair.Second);
            comparedPixels += observed.Length;
            if (differences != 0) Console.WriteLine($"NATIVE_FRAME mismatch_code={code}; rgba_mismatch_pixels={differences}");
            Check(observed.Length == expected.Length && differences == 0, code);
        }

        void TranslatedCopies(NativeResizeFrame frame, int[] pattern, Size size)
        {
            var area = new Rectangle(17, 13, 151, 99);
            foreach (var viewport in new[] { new Point(-31, -19), new Point(13, 7) })
            foreach (int managedMode in new[] { 0, 1, 2 }) // native / infinite / finite
            {
                var destination = new Point(20 - viewport.X, 18 - viewport.Y);
                using var target = new MemoryTarget(size);
                using var native = new OwnedRegion(0, 0, 154, 120);
                using var hole = new OwnedRegion(80, 40, 99, 60);
                using var after = new OwnedRegion(0, 0, 0, 0);
                CombineRgn(native.Handle, native.Handle, hole.Handle, 4);
                Check(SelectClipRgn(target.Dc, native.Handle) != 0, "MappedNativeClipInstalled");
                Check(SetViewportOrgEx(target.Dc, viewport.X, viewport.Y, IntPtr.Zero), "ExplicitViewportInstalled");
                int saved = SaveDC(target.Dc);
                Check(saved > 0 && RestoreDC(target.Dc, saved), "MappedStackBaseline");
                var finite = new Rectangle(destination.X + 5, destination.Y + 3, 138, 88);
                var finiteHole = new Rectangle(destination.X + 23, destination.Y + 27, 13, 19);
                if (managedMode == 0)
                    Check(frame.TryCopyTo(target.Dc, area, destination, viewport), "ExplicitNativeMappingAccepted");
                else
                {
                    using var graphics = Graphics.FromHdc(target.Dc);
                    graphics.PageUnit = GraphicsUnit.Pixel;
                    graphics.ResetClip();
                    if (managedMode == 2)
                    {
                        using var region = new Region(finite); region.Exclude(finiteHole);
                        graphics.SetClip(region, CombineMode.Replace);
                    }
                    using var clipBefore = graphics.Clip;
                    Check(frame.TryCopyTo(graphics, area, destination, viewport), "ExplicitManagedMappingAccepted");
                    using var clipAfter = graphics.Clip;
                    Check(clipBefore.Equals(clipAfter, graphics), "MappedManagedClipUnchanged");
                }
                Check(GetViewportOrgEx(target.Dc, out var current) && current.X == viewport.X && current.Y == viewport.Y,
                    "MappedViewportUnchanged");
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle), "MappedNativeClipUnchanged");
                int next = SaveDC(target.Dc);
                Check(next == saved && RestoreDC(target.Dc, next), "MappedCopyBalancesDcState");
                int[] actual = target.ReadRgb();
                long differences = 0;
                for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    var logical = new Point(x - viewport.X, y - viewport.Y);
                    bool copied = new Rectangle(20, 18, area.Width, area.Height).Contains(x, y) &&
                        x < 154 && y < 120 && !new Rectangle(80, 40, 19, 20).Contains(x, y) &&
                        (managedMode != 2 || finite.Contains(logical) && !finiteHole.Contains(logical));
                    int expected = copied ? pattern[(area.Y + y - 18) * size.Width + area.X + x - 20] & 0xffffff : Sentinel;
                    if (actual[y * size.Width + x] != expected) differences++;
                    comparedPixels++;
                }
                if (differences != 0) Console.WriteLine($"NATIVE_FRAME mapped_mode={managedMode}; rgb_mismatch_pixels={differences}");
                Check(differences == 0, "ExplicitMappingEveryPixelMatchesSourceAndBothClips");
                Check(!frame.TryCopyTo(target.Dc, area, destination, Point.Empty) &&
                    frame.LastCopyFailure == NativeResizeFrame.CopyFailure.TargetOrigin, "UndeclaredTranslationStillRejected");
                Check(!frame.TryCopyTo(target.Dc, area, new Point(int.MaxValue, 0), viewport), "MappedDestinationOverflowRejected");
                Check(actual.SequenceEqual(target.ReadRgb()), "RejectedMappedCopyDoesNotWrite");
            }
        }

        void Cases(int dpi)
        {
            var size = new Size(192, 128);
            var full = new Rectangle(Point.Empty, size);
            var area = new Rectangle(17, 13, size.Width - 41, size.Height - 29);
            int[] pattern = Pattern(size);
            using var source = PatternImage(size, dpi, pattern);
            using var frame = new NativeResizeFrame(size, dpi);
            Check(frame.Capacity == size && frame.Dpi == dpi, "NativeCapacityAndDpiMatchRequest");
            Check(frame.OwnedBytes == (long)size.Width * size.Height * 4 && !frame.IsDisposed, "NativeFrameOwnershipIsExplicit");
            Check(Math.Abs(frame.Bitmap.HorizontalResolution - dpi) < .1 && Math.Abs(frame.Graphics.DpiX - dpi) < .1,
                "AliasBitmapAndGraphicsHaveSameDpi");
            frame.Graphics.CompositingMode = CompositingMode.SourceCopy;
            frame.Graphics.DrawImageUnscaled(source, Point.Empty);
            using (var target = new MemoryTarget(size))
            {
                // Copy immediately after drawing, before LockBits/GetPixel can
                // incidentally synchronize a GDI+ source snapshot.
                Check(frame.TryCopyTo(target.Dc, full), "ImmediateNativeCopyAccepted");
                CompareRgb(target, pattern, (_, _) => true, "NativeCopyReadsThePersistentlySelectedDib");
                CompareRgba(frame.Bitmap, pattern, "GraphicsWritesAliasRgbaExactly");
                target.Reset();
                Check(frame.TryCopyTo(target.Dc, area), "NativePartialCopyAccepted");
                CompareRgb(target, pattern, (x, y) => area.Contains(x, y), "NativePartialCopyUsesIdenticalPixelCoordinates");
                frame.Bitmap.SetPixel(3, 4, Color.FromArgb(255, 231, 119, 63));
                pattern[4 * size.Width + 3] = unchecked((int)0xffe7773f);
                target.Reset();
                Check(frame.TryCopyTo(target.Dc, full), "AliasMutationCopyAccepted");
                CompareRgb(target, pattern, (_, _) => true, "BitmapAliasMutationIsVisibleToNativeSource");
                using var ink = new SolidBrush(Color.FromArgb(255, 66, 163, 207));
                var update = new Rectangle(7, 9, 13, 11);
                frame.Graphics.FillRectangle(ink, update);
                for (int y = update.Top; y < update.Bottom; y++)
                for (int x = update.Left; x < update.Right; x++) pattern[y * size.Width + x] = unchecked((int)0xff42a3cf);
                target.Reset();
                Check(frame.TryCopyTo(target.Dc, full), "UpdatedGraphicsCopyAccepted");
                CompareRgb(target, pattern, (_, _) => true, "LaterGraphicsWritesUseSameNativeSource");
                CompareRgba(frame.Bitmap, pattern, "UpdatedAliasKeepsEveryRgbAndAlphaByte");
            }

            using (var target = new MemoryTarget(size))
            using (var native = new OwnedRegion(0, 0, size.Width / 2, size.Height))
            using (var bottom = new OwnedRegion(0, size.Height * 2 / 3, size.Width, size.Height))
            using (var hole = new OwnedRegion(21, 27, 43, 56))
            using (var after = new OwnedRegion(0, 0, 0, 0))
            {
                CombineRgn(native.Handle, native.Handle, bottom.Handle, 2);
                CombineRgn(native.Handle, native.Handle, hole.Handle, 4);
                Check(SelectClipRgn(target.Dc, native.Handle) != 0, "ExternalComplexNativeClipInstalled");
                int stack = SaveDC(target.Dc); Check(stack > 0 && RestoreDC(target.Dc, stack), "NativeStackBaselineReadable");
                Check(frame.TryCopyTo(target.Dc, full), "ComplexNativeCopyAccepted");
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle), "NativeClipPreservedExactly");
                int next = SaveDC(target.Dc); Check(next == stack && RestoreDC(target.Dc, next), "NativeCopyBalancesDcState");
                CompareRgb(target, pattern, (x, y) => (x < size.Width / 2 || y >= size.Height * 2 / 3) &&
                    !new Rectangle(21, 27, 22, 29).Contains(x, y), "NativeCopyPreservesComplexClipAndHole");
            }

            using (var target = new MemoryTarget(size))
            using (var native = new OwnedRegion(0, 0, size.Width / 2, size.Height))
            using (var bottom = new OwnedRegion(0, size.Height * 2 / 3, size.Width, size.Height))
            using (var hole = new OwnedRegion(21, 27, 43, 56))
            using (var after = new OwnedRegion(0, 0, 0, 0))
            {
                CombineRgn(native.Handle, native.Handle, bottom.Handle, 2);
                CombineRgn(native.Handle, native.Handle, hole.Handle, 4);
                Check(SelectClipRgn(target.Dc, native.Handle) != 0, "InfiniteManagedNativeClipInstalled");
                using var graphics = Graphics.FromHdc(target.Dc);
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.ResetClip();
                using var beforeManaged = graphics.Clip;
                Check(beforeManaged.IsInfinite(graphics), "InfiniteManagedClipFixtureIsReallyInfinite");
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle),
                    "ManagedResetDoesNotReplaceExternalNativeClip");
                int stack = SaveDC(target.Dc);
                Check(stack > 0 && RestoreDC(target.Dc, stack), "InfiniteManagedNativeStackBaselineReadable");
                Check(frame.TryCopyTo(graphics, area), "InfiniteManagedClipCopyAccepted");
                using (var afterManaged = graphics.Clip)
                    Check(afterManaged.IsInfinite(graphics) && afterManaged.Equals(beforeManaged, graphics),
                        "InfiniteManagedClipPreservedExactly");
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle),
                    "InfiniteManagedCopyPreservesComplexNativeClip");
                int next = SaveDC(target.Dc);
                Check(next == stack && RestoreDC(target.Dc, next), "InfiniteManagedCopyBalancesNativeStateStack");
                CompareRgb(target, pattern, (x, y) => area.Contains(x, y) &&
                    (x < size.Width / 2 || y >= size.Height * 2 / 3) && !new Rectangle(21, 27, 22, 29).Contains(x, y),
                    "InfiniteManagedClipUsesOnlyExistingNativeMaskAtEveryPixel");
            }

            using (var target = new MemoryTarget(size))
            using (var native = new OwnedRegion(0, 0, size.Width / 2, size.Height))
            using (var bottom = new OwnedRegion(0, size.Height * 2 / 3, size.Width, size.Height))
            using (var after = new OwnedRegion(0, 0, 0, 0))
            {
                CombineRgn(native.Handle, native.Handle, bottom.Handle, 2);
                Check(SelectClipRgn(target.Dc, native.Handle) != 0, "ManagedTestNativeClipInstalled");
                using var graphics = Graphics.FromHdc(target.Dc);
                graphics.PageUnit = GraphicsUnit.Pixel;
                using var managed = new Region(new Rectangle(31, 19, size.Width - 42, size.Height - 30));
                managed.Exclude(new Rectangle(48, 41, 37, 23));
                graphics.SetClip(managed, System.Drawing.Drawing2D.CombineMode.Replace);
                using var beforeManaged = graphics.Clip;
                Check(frame.TryCopyTo(graphics, area), "ManagedAndNativeCopyAccepted");
                using (var afterManaged = graphics.Clip)
                    Check(afterManaged.Equals(beforeManaged, graphics), "ManagedClipPreservedExactly");
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle), "ManagedCopyRestoresNativeClip");
                using (var matrix = graphics.Transform)
                    Check(matrix.IsIdentity && graphics.PageScale == 1 && graphics.PageUnit == GraphicsUnit.Pixel,
                        "ManagedCopyPreservesCoordinateState");
                // HRGN uses integer pixel rectangles; IsVisible(x+.5,y+.5)
                // has different GDI+ raster boundary semantics for these edges.
                CompareRgb(target, pattern, (x, y) => area.Contains(x, y) &&
                    (x < size.Width / 2 || y >= size.Height * 2 / 3) &&
                    new Rectangle(31, 19, size.Width - 42, size.Height - 30).Contains(x, y) &&
                    !new Rectangle(48, 41, 37, 23).Contains(x, y),
                    "ManagedAndNativeClipsIntersectWithoutPixelLoss");
                for (int i = 0; i < 4; i++) Check(frame.TryCopyTo(graphics, area), "RepeatedCopyWarmupAccepted");
                using var process = Process.GetCurrentProcess();
                uint resources = GetGuiResources(process.Handle, 0);
                Check(resources > 0, "LiveFrameGdiResourceBaselineReadable");
                for (int i = 0; i < 64; i++) Check(frame.TryCopyTo(graphics, area), "RepeatedNativeCopyAccepted");
                Check(GetGuiResources(process.Handle, 0) <= resources, "RepeatedNativeCopiesDoNotLeakGdiObjects");
                using var pen = new Pen(Color.White);
                graphics.DrawLine(pen, 35, 22, 36, 23);
                Check(GetClipRgn(target.Dc, after.Handle) == 1 && EqualRgn(native.Handle, after.Handle), "CallerDcRemainsUsableAfterCopies");
            }

            using (var target = new MemoryTarget(size))
            {
                foreach (var bad in new[] { Rectangle.Empty, new Rectangle(-1, 0, 10, 10), new Rectangle(0, -1, 10, 10),
                    new Rectangle(size.Width - 1, 0, 2, 1), new Rectangle(0, size.Height - 1, 1, 2),
                    new Rectangle(1, 1, int.MaxValue, int.MaxValue) })
                    Check(!frame.TryCopyTo(target.Dc, bad), "InvalidNativeSourceAreaRejected");
                Check(!frame.TryCopyTo(IntPtr.Zero, full), "MissingNativeTargetRejected");
                Check(!frame.TryCopyTo(frame.Graphics, full), "SameSourceGraphicsTargetRejected");
                using (var graphics = Graphics.FromHdc(target.Dc))
                {
                    graphics.TranslateTransform(4, 2);
                    Check(!frame.TryCopyTo(graphics, full), "TransformedManagedTargetRejected");
                    graphics.ResetTransform(); graphics.PageScale = 2;
                    Check(!frame.TryCopyTo(graphics, full), "ScaledManagedTargetRejected");
                    graphics.PageScale = 1; graphics.PageUnit = GraphicsUnit.Inch;
                    Check(!frame.TryCopyTo(graphics, full), "NonPixelManagedTargetRejected");
                    graphics.PageUnit = GraphicsUnit.Pixel;
                }
                int saved = SaveDC(target.Dc);
                try
                {
                    SetViewportOrgEx(target.Dc, 3, 4, IntPtr.Zero);
                    Check(!frame.TryCopyTo(target.Dc, full), "TranslatedNativeTargetRejected");
                    Check(GetViewportOrgEx(target.Dc, out var origin) && origin.X == 3 && origin.Y == 4,
                        "RejectedTargetKeepsNativeOrigin");
                }
                finally { RestoreDC(target.Dc, saved); }
                saved = SaveDC(target.Dc);
                try
                {
                    SetMapMode(target.Dc, 8);
                    Check(!frame.TryCopyTo(target.Dc, full), "NonTextNativeMapRejected");
                }
                finally { RestoreDC(target.Dc, saved); }
                bool wrongCopy = true, wrongDisposeRejected = false, unexpected = false;
                var foreign = new Thread(() =>
                {
                    try
                    {
                        wrongCopy = frame.TryCopyTo(target.Dc, full);
                        try { frame.Dispose(); }
                        catch (InvalidOperationException) { wrongDisposeRejected = true; }
                    }
                    catch { unexpected = true; }
                }) { IsBackground = true };
                foreign.Start();
                Check(foreign.Join(TimeSpan.FromSeconds(5)), "ForeignFrameOperationTerminates");
                Check(!unexpected && !wrongCopy && wrongDisposeRejected && !frame.IsDisposed, "NativeOwnershipRejectsForeignThread");
                CompareRgb(target, pattern, (_, _) => false, "RejectedRequestsNeverWriteCallerTarget");
            }

            ConstructorChecks(dpi);
            TranslatedCopies(frame, pattern, size);
            CompleteScene(dpi);
            using (var target = new MemoryTarget(size))
            {
                frame.Dispose(); frame.Dispose();
                Check(frame.IsDisposed && frame.OwnedBytes == 0, "DisposedFrameReleasesOwnedBytes");
                Check(!frame.TryCopyTo(target.Dc, full), "DisposedFrameCannotCopy");
                bool bitmapRejected = false, graphicsRejected = false;
                try { _ = frame.Bitmap; } catch (ObjectDisposedException) { bitmapRejected = true; }
                try { _ = frame.Graphics; } catch (ObjectDisposedException) { graphicsRejected = true; }
                Check(bitmapRejected && graphicsRejected, "DisposedAliasAccessRejected");
                target.Reset();
                CompareRgb(target, pattern, (_, _) => false, "FrameDoesNotOwnExternalTarget");
                Check(source.GetPixel(2, 3).ToArgb() == Pattern(size)[3 * size.Width + 2], "FrameDoesNotOwnExternalSourceImage");
            }
        }

        void ConstructorChecks(int dpi)
        {
            foreach (var size in new[] { Size.Empty, new Size(-1, 12), new Size(12, -1) })
                RejectCtor(() => new NativeResizeFrame(size, dpi), typeof(ArgumentOutOfRangeException), "InvalidCapacityRejected");
            RejectCtor(() => new NativeResizeFrame(new Size(8, 8), 0), typeof(ArgumentOutOfRangeException), "InvalidNativeDpiRejected");
            RejectCtor(() => new NativeResizeFrame(new Size(8, 8), dpi, 0), typeof(ArgumentOutOfRangeException), "InvalidNativeBudgetRejected");
            RejectCtor(() => new NativeResizeFrame(new Size(64, 32), dpi, 64 * 32 * 4 - 1), typeof(InvalidOperationException), "NativeBudgetEnforcedBeforeAllocation");
            using var exact = new NativeResizeFrame(new Size(64, 32), dpi, 64 * 32 * 4);
            Check(exact.OwnedBytes == 64 * 32 * 4, "ExactNativeBudgetAccepted");
        }

        void RejectCtor(Func<NativeResizeFrame> create, Type expected, string code)
        {
            bool rejected = false;
            try { using var unexpected = create(); }
            catch (Exception error) when (error.GetType() == expected) { rejected = true; }
            Check(rejected, code);
        }

        void CompleteScene(int dpi)
        {
            int Scale(int value) => (int)Math.Round(value * dpi / 96d);
            var size = new Size(Scale(1120), Scale(780));
            var state = new ResizeSceneState { View = dpi == 96 ? 0 : 1, Selected = 5, Hover = 6,
                Scroll = 3, Metric = 1, Progress = .63, Revision = 29 };
            var layout = ResizeSceneLayout.Build(size, dpi, state);
            using var wallpaper = Wallpaper.Load();
            using var renderer = new ResizeSceneRenderer();
            using var reference = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            reference.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(reference)) renderer.Render(graphics, layout, state, wallpaper, null, 1.25);
            int[] expected = ReadRgba(reference);
            using var native = new NativeResizeFrame(size, dpi);
            renderer.Render(native.Graphics, layout, state, wallpaper, null, 1.25);
            using var target = new MemoryTarget(size);
            Check(native.TryCopyTo(target.Dc, new Rectangle(Point.Empty, size)), "CompleteNativeSceneCopyAccepted");
            CompareRgba(native.Bitmap, expected, "CompleteSceneRgbaMatchesLegacyBitmapExactly");
            CompareRgb(target, expected, (_, _) => true, "CompleteSceneNativeCopyMatchesEveryLegacyRgbPixel");
        }

        void HandleCycles()
        {
            using var anchor = new MemoryTarget(new Size(8, 8));
            using var process = Process.GetCurrentProcess();
            for (int i = 0; i < 4; i++) { using var warm = new NativeResizeFrame(new Size(16, 16), 96); }
            uint baseline = GetGuiResources(process.Handle, 0);
            Check(baseline > 0, "FrameHandleBaselineReadable");
            for (int i = 0; i < 64; i++)
            {
                using var frame = new NativeResizeFrame(new Size(16, 16), i % 2 == 0 ? 96 : 144);
                frame.Graphics.Clear(Color.FromArgb(255, 25, 73, 108));
                Check(frame.TryCopyTo(anchor.Dc, new Rectangle(0, 0, 8, 8)), "NativeHandleCycleCopyAccepted");
                frame.Dispose();
                Check(frame.IsDisposed && frame.OwnedBytes == 0, "NativeHandleCycleDisposed");
                RejectCtor(() => new NativeResizeFrame(new Size(16, 16), 96, 1023), typeof(InvalidOperationException), "FailedConstructorDoesNotCreateLiveFrame");
            }
            Check(GetGuiResources(process.Handle, 0) <= baseline, "NativeFrameAndFailedConstructorCyclesDoNotLeakGdiHandles");
        }
    }

    private static int[] Pattern(Size size) => Enumerable.Range(0, size.Width * size.Height).Select(index =>
    {
        int x = index % size.Width, y = index / size.Width;
        return unchecked((int)0xff000000) | ((x * 11 + y * 7) & 255) << 16 |
            ((x * 3 + y * 17) & 255) << 8 | ((x * 19 + y * 5) & 255);
    }).ToArray();

    private static Bitmap PatternImage(Size size, int dpi, int[] values)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb); image.SetResolution(dpi, dpi);
        var bits = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        { for (int y = 0; y < size.Height; y++) Marshal.Copy(values, y * size.Width, bits.Scan0 + y * bits.Stride, size.Width); }
        finally { image.UnlockBits(bits); }
        return image;
    }

    private static int[] ReadRgba(Bitmap image)
    {
        var values = new int[image.Width * image.Height];
        var bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        { for (int y = 0; y < image.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, values, y * image.Width, image.Width); }
        finally { image.UnlockBits(bits); }
        return values;
    }

    private sealed class MemoryTarget : IDisposable
    {
        internal int Width { get; }
        internal int Height { get; }
        internal IntPtr Dc { get; private set; }
        private IntPtr bitmap, previous, pixels;
        internal MemoryTarget(Size size)
        {
            Width = size.Width; Height = size.Height; Dc = CreateCompatibleDC(IntPtr.Zero);
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = Width, Height = -Height,
                Planes = 1, BitCount = 32, Compression = 0 } };
            bitmap = CreateDIBSection(Dc, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (Dc == IntPtr.Zero || bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
            { Dispose(); throw new Failure("TargetMemoryDibCreationFailed"); }
            previous = SelectObject(Dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            { Dispose(); throw new Failure("TargetMemoryDibSelectionFailed"); }
            Reset();
        }
        internal void Reset()
        {
            GdiFlush(); var row = Enumerable.Repeat(unchecked((int)0xff000000) | Sentinel, Width).ToArray();
            for (int y = 0; y < Height; y++) Marshal.Copy(row, 0, pixels + y * Width * 4, Width);
        }
        internal int[] ReadRgb()
        {
            GdiFlush(); var values = new int[Width * Height]; Marshal.Copy(pixels, values, 0, values.Length);
            for (int i = 0; i < values.Length; i++) values[i] &= 0x00ffffff;
            return values;
        }
        public void Dispose()
        {
            if (Dc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(Dc, previous);
            if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); bitmap = IntPtr.Zero; }
            if (Dc != IntPtr.Zero) { DeleteDC(Dc); Dc = IntPtr.Zero; }
            pixels = previous = IntPtr.Zero;
        }
    }
    private sealed class OwnedRegion : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        internal OwnedRegion(int left, int top, int right, int bottom)
        { Handle = CreateRectRgn(left, top, right, bottom); if (Handle == IntPtr.Zero) throw new Failure("TargetClipRegionCreationFailed"); }
        public void Dispose() { if (Handle != IntPtr.Zero) { DeleteObject(Handle); Handle = IntPtr.Zero; } }
    }
    private sealed class Failure(string code) : Exception { internal string Code { get; } = code; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter; internal uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreDC(IntPtr dc, int state);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetViewportOrgEx(IntPtr dc, out NativePoint point);
    [DllImport("gdi32.dll")] private static extern int SetMapMode(IntPtr dc, int mode);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}

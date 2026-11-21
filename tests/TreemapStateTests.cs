using System.Diagnostics;
using System.Reflection;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiskAtlas;

internal static class TreemapStateTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static int Run(bool dpi96 = false)
    {
        Exception? failure = null;
        int passed = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new Form { ClientSize = new Size(900, 520), ShowInTaskbar = false };
                using var map = new TreemapControl { Dock = DockStyle.Fill, ShowNested = false, AnimationsEnabled = false };
                form.Controls.Add(map); form.Show(); Application.DoEvents();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                string lastPixelDifference = "";
                List<MapTile> Tiles() => (List<MapTile>)typeof(TreemapControl).GetField("tiles", flags)!.GetValue(map)!;
                void Check(bool condition, string label)
                {
                    if (!condition) throw new InvalidOperationException(label + lastPixelDifference);
                    passed++; Console.WriteLine("PASS treemap state " + label);
                }
                void Input(string method, Point point) => typeof(TreemapControl).GetMethod(method, flags)!.Invoke(map,
                    [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
                void Leave() => typeof(TreemapControl).GetMethod("OnMouseLeave", flags)!.Invoke(map, [EventArgs.Empty]);
                Point Center(MapTile tile) => Point.Round(new PointF(tile.Bounds.X + tile.Bounds.Width / 2, tile.Bounds.Y + tile.Bounds.Height / 2));
                void Pump(int milliseconds)
                {
                    var watch = Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(4); }
                }
                Bitmap Capture()
                {
                    var image = new Bitmap(map.Width, map.Height); image.SetResolution(map.DeviceDpi, map.DeviceDpi);
                    map.DrawToBitmap(image, map.ClientRectangle); return image;
                }
                bool SamePixels(Bitmap a, Bitmap b, Rectangle bounds)
                {
                    lastPixelDifference = "";
                    bounds.Intersect(new Rectangle(0, 0, a.Width, a.Height));
                    if (bounds.Width <= 0 || bounds.Height <= 0) return true;
                    var left = a.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        var right = b.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            var leftRow = new byte[bounds.Width * 4]; var rightRow = new byte[leftRow.Length];
                            for (int row = 0; row < bounds.Height; row++)
                            {
                                Marshal.Copy(IntPtr.Add(left.Scan0, row * left.Stride), leftRow, 0, leftRow.Length);
                                Marshal.Copy(IntPtr.Add(right.Scan0, row * right.Stride), rightRow, 0, rightRow.Length);
                                if (!leftRow.AsSpan().SequenceEqual(rightRow))
                                {
                                    int offset = Enumerable.Range(0, bounds.Width).First(pixel =>
                                        !leftRow.AsSpan(pixel * 4, 4).SequenceEqual(rightRow.AsSpan(pixel * 4, 4))) * 4;
                                    int x = bounds.Left + offset / 4, y = bounds.Top + row;
                                    lastPixelDifference = $"; owned pixel {x},{y}; composed BGRA {string.Join(',', leftRow.Skip(offset).Take(4))}; full BGRA {string.Join(',', rightRow.Skip(offset).Take(4))}";
                                    return false;
                                }
                            }
                            return true;
                        }
                        finally { b.UnlockBits(right); }
                    }
                    finally { a.UnlockBits(left); }
                }
                int ClearlyChangedPixels(Bitmap before, Bitmap after, Rectangle bounds)
                {
                    bounds.Intersect(new Rectangle(Point.Empty, before.Size));
                    int count = 0;
                    for (int y = bounds.Top; y < bounds.Bottom; y++)
                        for (int x = bounds.Left; x < bounds.Right; x++)
                        {
                            var old = before.GetPixel(x, y); var next = after.GetPixel(x, y);
                            // A one-level channel change passed the old tests
                            // while the state remained invisible in the app.
                            if (Math.Abs(next.R - old.R) + Math.Abs(next.G - old.G) + Math.Abs(next.B - old.B) >= 45) count++;
                        }
                    return count;
                }
                map.SetItems([new("长文件名-中文-01", 600, null, "文件夹"), new("Pictures", 300, null, "图片"),
                    new("Archive", 90, null, "压缩包"), new("tiny", 10, null, "其他")]);
                using var baseline = Capture();
                var tiles = Tiles().ToArray();
                var originalBounds = tiles.Select(tile => tile.Bounds).ToArray();
                var first = tiles[0]; var second = tiles[1];
                float scale = map.DeviceDpi / 96f;
                CheckPreviewGlow(Check, scale);
                int builds = map.StaticLayerBuilds;
                map.SelectItem(first.Item);
                using var selected = Capture();
                var glow = TreemapControl.GlowBounds(first.Bounds, scale);
                Check(!glow.IsEmpty && first.Bounds.Contains(glow), "selection glow follows a contour inside the selected tile");
                Check(!SamePixels(baseline, selected, Rectangle.Ceiling(glow)), "selection retains a visible complete soft contour");
                Rectangle Interior(MapTile tile) => Rectangle.FromLTRB((int)Math.Ceiling(tile.Bounds.Left + 8 * scale),
                    (int)Math.Ceiling(tile.Bounds.Top + 8 * scale), (int)Math.Floor(tile.Bounds.Right - 8 * scale), (int)Math.Floor(tile.Bounds.Bottom - 8 * scale));
                var labelAndBody = Interior(first);
                Check(SamePixels(baseline, selected, labelAndBody), "selection leaves interior pixels and cached labels unchanged");
                foreach (var edge in new[] {
                    new RectangleF(glow.Left + glow.Width / 2 - 4 * scale, glow.Top - scale, 8 * scale, 3 * scale),
                    new RectangleF(glow.Left + glow.Width / 2 - 4 * scale, glow.Bottom - 2 * scale, 8 * scale, 3 * scale),
                    new RectangleF(glow.Left - scale, glow.Top + glow.Height / 2 - 4 * scale, 3 * scale, 8 * scale),
                    new RectangleF(glow.Right - 2 * scale, glow.Top + glow.Height / 2 - 4 * scale, 3 * scale, 8 * scale) })
                    Check(ClearlyChangedPixels(baseline, selected, Rectangle.Ceiling(edge)) >= 2,
                        "static soft contour has perceptible colour separation on each complete edge");
                Check(SamePixels(baseline, selected, Rectangle.Ceiling(second.Bounds)), "selection does not repaint neighbouring tiles");
                Input("OnMouseMove", Center(second));
                using var hovered = Capture();
                var hoverBody = Interior(second);
                Check(SamePixels(baseline, hovered, hoverBody), "hover leaves interior labels and data unchanged");
                Leave(); using var afterLeave = Capture();
                Check(SamePixels(selected, afterLeave, map.ClientRectangle), "leaving hover preserves persistent selection");
                Check(map.StaticLayerBuilds == builds && originalBounds.SequenceEqual(Tiles().Select(tile => tile.Bounds)),
                    "state changes reuse the static layer and preserve areas");
                using var focusSink = new Button { Text = "Focus", Bounds = new Rectangle(0, 0, 60, 24), TabIndex = 1 };
                form.Controls.Add(focusSink);
                map.TabIndex = 0;
                map.Focus(); Input("OnMouseDown", Center(first));
                Check(map.Focused && !map.KeyboardFocusVisible, "mouse acquisition keeps keyboard focus cues out of selection state");
                using var wallpaper = new Bitmap(48, 32);
                using (var paper = Graphics.FromImage(wallpaper))
                {
                    paper.Clear(Color.FromArgb(22, 31, 40));
                    using var pale = new SolidBrush(Color.FromArgb(250, 224, 190));
                    paper.FillRectangle(pale, 0, 0, 21, 32);
                }
                form.BackgroundImage = wallpaper;
                void PaintRegion(Bitmap image, Rectangle dirty)
                {
                    using var graphics = Graphics.FromImage(image);
                    graphics.SetClip(dirty, CombineMode.Intersect);
                    using var paint = new PaintEventArgs(graphics, dirty);
                    typeof(TreemapControl).GetMethod("OnPaint", flags)!.Invoke(map, [paint]);
                }
                Bitmap FullFrame()
                {
                    var image = new Bitmap(map.Width, map.Height, PixelFormat.Format32bppArgb);
                    image.SetResolution(map.DeviceDpi, map.DeviceDpi);
                    PaintRegion(image, map.ClientRectangle); return image;
                }
                Rectangle Dirty(MapTile tile) => Rectangle.Intersect(map.ClientRectangle,
                    Rectangle.Ceiling(RectangleF.Inflate(tile.Bounds, 2 * scale, 2 * scale)));
                map.SelectItem(null); Leave();
                using var composed = FullFrame();
                var opaqueCache = (Bitmap)typeof(TreemapControl).GetField("staticLayer", flags)!.GetValue(map)!;
                Check(new[] { Point.Empty, new Point(map.Width - 1, 0), new Point(0, map.Height - 1), new Point(map.Width - 1, map.Height - 1) }
                    .All(point => opaqueCache.GetPixel(point.X, point.Y).A == 255), "retained wallpaper cache has opaque edges");
                map.SelectItem(first.Item); PaintRegion(composed, Dirty(first));
                using (var full = FullFrame()) Check(SamePixels(composed, full, map.ClientRectangle),
                    "partial selection paint composes the same pixels as a full frame");
                map.SelectItem(second.Item); PaintRegion(composed, Dirty(first)); PaintRegion(composed, Dirty(second));
                using (var full = FullFrame()) Check(SamePixels(composed, full, map.ClientRectangle),
                    "separate old and new selection regions erase state without cache seams");
                Rectangle[] edgeRegions = [new(0, 0, 1, map.Height), new(0, 0, map.Width, 1),
                    new(map.Width - 1, 0, 1, map.Height), new(0, map.Height - 1, map.Width, 1),
                    new(0, 0, Math.Max(1, (int)Math.Floor(first.Bounds.Left)), map.Height),
                    new((int)Math.Floor(second.Bounds.Left), (int)Math.Floor(second.Bounds.Top), 3,
                        Math.Max(1, (int)Math.Floor(second.Bounds.Height)))];
                for (int edge = 0; edge < edgeRegions.Length; edge++)
                {
                    PaintRegion(composed, edgeRegions[edge]);
                    using var full = FullFrame();
                    Check(SamePixels(composed, full, map.ClientRectangle),
                        "edge and background partial paint remains pixel-identical to full composition " + edge);
                }
                builds = map.StaticLayerBuilds;
                focusSink.Focus(); Application.DoEvents();
                // Clear UISF_HIDEFOCUS on this owned fixture, as keyboard
                // navigation does; no desktop input or external window is used.
                SendMessage(form.Handle, 0x0127, new IntPtr(0x00010002), IntPtr.Zero);
                bool reachedByTab = form.SelectNextControl(focusSink, true, true, true, true);
                Application.DoEvents();
                Check(reachedByTab && map.Focused && map.KeyboardFocusVisible,
                    "keyboard navigation restores a visible independent focus cue");
                Input("OnMouseDown", Center(second));
                Check(!map.KeyboardFocusVisible, "pointer selection suppresses the keyboard cue again");
                foreach (float width in new[] { 4f, 5f, 7f, 12f, 24f, 80f })
                    foreach (float height in new[] { 4f, 6f, 12f, 40f })
                    {
                        var bounds = new RectangleF(17.25f, 23.5f, width * scale, height * scale);
                        var small = TreemapControl.GlowBounds(bounds, scale);
                        Check(small.IsEmpty || RectangleF.Inflate(bounds, -2 * scale, -2 * scale).Contains(small),
                            "narrow glow stays within its visible tile " + width + "x" + height);
                    }
                map.SelectItem(first.Item);
                map.AnimationsEnabled = true;
                map.SelectItem(second.Item);
                Check(map.MotionRunning && map.OrbitRunning, "list selection starts one continuous edge orbit");
                Pump(20);
                var clock = (Stopwatch)typeof(TreemapControl).GetField("motionClock", flags)!.GetValue(map)!;
                MapItem? State(string name) => (MapItem?)typeof(TreemapControl).GetField(name, flags)!.GetValue(map);
                int ActiveOutlines() => (State("motionItem") == null ? 0 : 1) + (State("secondaryMotionItem") == null ? 0 : 1);
                long elapsed = clock.ElapsedTicks;
                map.SelectItem(second.Item);
                Check(clock.ElapsedTicks >= elapsed, "selecting the same object does not restart the orbit");
                using var orbitStart = Capture();
                Pump(110);
                using var orbitLater = Capture();
                Check(!SamePixels(orbitStart, orbitLater, Rectangle.Ceiling(second.Bounds)), "the light band moves along the selected edge");
                Check(ClearlyChangedPixels(orbitStart, orbitLater, Rectangle.Ceiling(second.Bounds)) >= 8,
                    "the moving band changes more than imperceptible channel levels");
                Check(SamePixels(orbitStart, orbitLater, Interior(second)), "moving light never repaints interior text or data");
                Check(SamePixels(orbitStart, orbitLater, Rectangle.Ceiling(first.Bounds)), "moving light never changes a neighbouring tile");
                elapsed = clock.ElapsedTicks;
                Input("OnMouseMove", Center(first));
                Check(ActiveOutlines() == 2 && State("motionItem") == second.Item && State("secondaryMotionItem") == first.Item && clock.ElapsedTicks >= elapsed,
                    "a different hover joins the selected outline without resetting their shared phase");
                using var dualStart = Capture(); Pump(110); using var dualLater = Capture();
                Check(ClearlyChangedPixels(dualStart, dualLater, Rectangle.Ceiling(first.Bounds)) >= 8 &&
                    ClearlyChangedPixels(dualStart, dualLater, Rectangle.Ceiling(second.Bounds)) >= 8,
                    "both selected and hovered tiles visibly travel during the same interval");
                Check(SamePixels(dualStart, dualLater, Interior(first)) && SamePixels(dualStart, dualLater, Interior(second)) &&
                    SamePixels(dualStart, dualLater, Rectangle.Ceiling(tiles[2].Bounds)),
                    "dual motion preserves both bodies and an unrelated neighbouring tile exactly");
                Pump(TreemapControl.MotionDurationMilliseconds + 80);
                Check(map.MotionRunning && map.OrbitRunning && clock.IsRunning && ActiveOutlines() == 2,
                    "both outlines continue beyond the former hover timeout");
                Leave();
                Check(map.OrbitRunning && clock.ElapsedTicks >= elapsed && ActiveOutlines() == 1 && State("motionItem") == second.Item,
                    "leaving hover preserves only the same selected orbit and phase");
                Input("OnMouseMove", Center(second));
                Check(ActiveOutlines() == 1 && clock.ElapsedTicks >= elapsed,
                    "hovering the selected tile merges both states into one moving outline");
                Leave();
                map.SelectItem(null);
                Check(!map.MotionRunning && !clock.IsRunning && ActiveOutlines() == 0, "clearing the last active state stops the continuous orbit");
                Input("OnMouseMove", Center(first));
                Check(map.MotionRunning && map.OrbitRunning && ActiveOutlines() == 1, "unselected hover starts a continuous edge orbit");
                Pump(TreemapControl.MotionDurationMilliseconds + 80);
                Check(map.MotionRunning && clock.IsRunning, "unselected hover continues beyond the former short prompt");
                elapsed = clock.ElapsedTicks;
                map.SelectItem(second.Item); map.SelectItem(null);
                Check(map.MotionRunning && State("motionItem") == first.Item && ActiveOutlines() == 1 && clock.ElapsedTicks >= elapsed,
                    "clearing selection leaves the current hover orbit and phase running");
                Leave();
                Check(!map.MotionRunning && !clock.IsRunning && ActiveOutlines() == 0,
                    "leaving the last hovered tile stops the only timer and clock");
                map.SelectItem(second.Item);
                Pump(20); elapsed = clock.ElapsedTicks;
                for (int i = 0; i < 48; i++) Input("OnMouseMove", Center(tiles[i % 3]));
                using var rapid = Capture();
                Check(State("motionItem") == second.Item && clock.ElapsedTicks >= elapsed && ActiveOutlines() <= 2 &&
                    map.StaticLayerBuilds == builds && originalBounds.SequenceEqual(Tiles().Select(tile => tile.Bounds)),
                    "rapid hover preserves selected phase and two active bounds without rebuilding layouts or labels");
                var contours = (System.Collections.IDictionary)typeof(TreemapControl).GetField("glowContours", flags)!.GetValue(map)!;
                Check(contours.Count <= 2, "contour resources are bounded to the selected and hovered tiles");
                map.AnimationsEnabled = false;
                Check(!map.MotionRunning && !clock.IsRunning && ActiveOutlines() == 0 && State("hovered") == null,
                    "reduced motion immediately settles both outlines and clears stale hover");
                MapItem? hit = null; map.ItemSelected += item => hit = item;
                Input("OnMouseClick", Center(first));
                Check(hit == first.Item && !map.MotionRunning, "reduced motion preserves click identity and the static selected state");
                map.AnimationsEnabled = true; map.SelectItem(second.Item); Input("OnMouseMove", Center(first)); map.Enabled = false;
                Check(!map.MotionRunning && !clock.IsRunning && State("hovered") == null, "disabling the control stops both orbits and clears hover");
                map.Enabled = true;
                Check(map.OrbitRunning && ActiveOutlines() == 1 && State("motionItem") == second.Item && State("hovered") == null,
                    "enabling restores only the valid selection without reviving old hover");
                map.SelectItem(first.Item); Input("OnMouseMove", Center(second)); map.Hide();
                Check(!map.MotionRunning && !clock.IsRunning && map.StaticLayerBytes == 0, "hiding stops motion and releases the static layer");
                map.Show(); using var restored = Capture();
                Check(map.OrbitRunning && ActiveOutlines() == 1 && State("hovered") == null, "showing resumes only the still-valid selected orbit");
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    Input("OnMouseMove", Center(second)); map.Enabled = false; map.Hide(); map.Enabled = true;
                    Check(!map.MotionRunning && !clock.IsRunning && ActiveOutlines() == 0 && State("hovered") == null,
                        "enabling while hidden does not restart or retain a hover generation " + cycle);
                    map.Show(); using var cycleFrame = Capture();
                    Check(map.OrbitRunning && ActiveOutlines() == 1 && State("motionItem") == first.Item && State("hovered") == null,
                        "repeated restore keeps one selection and no expired hover " + cycle);
                }
                Input("OnMouseMove", Center(second));
                form.WindowState = FormWindowState.Minimized; Pump(90);
                Check(!map.MotionRunning && !clock.IsRunning && State("hovered") == null, "minimising the owner stops the orbit timer and clears hover");
                Console.WriteLine("OBSERVE glow restore setter entering");
                form.WindowState = FormWindowState.Normal;
                Console.WriteLine("OBSERVE glow restore setter returned; entering event drain");
                Application.DoEvents();
                Console.WriteLine("OBSERVE glow restore event drain returned");
                using var unminimised = Capture();
                Check(map.OrbitRunning && ActiveOutlines() == 1 && State("hovered") == null, "restoring the owner resumes a valid selection without stale hover");
                map.SetItems([new("Replacement", 1, null, "其他")]);
                Check(!map.MotionRunning && !clock.IsRunning && contours.Count == 0, "a new source clears selection motion and contour generation");
                map.SelectItem(Tiles()[0].Item);
                map.Dispose();
                Check(!map.MotionRunning && !clock.IsRunning && map.StaticLayerBytes == 0, "disposal stops motion and releases its graphics resources");
                form.BackgroundImage = null; form.Close();

                // Exercise the real map -> MainForm -> grid -> map callback,
                // using synthetic rows only, without starting a scanner.
                using var product = new MainForm(null);
                product.Show(); Application.DoEvents();
                T Product<T>(string field) => (T)typeof(MainForm).GetField(field, flags)!.GetValue(product)!;
                var productMap = Product<TreemapControl>("map");
                var rows = new List<MapItem> { new("Selected folder", 600, null, "文件夹"), new("Selected pictures", 300, null, "图片") };
                typeof(MainForm).GetMethod("PopulateGrid", flags)!.Invoke(product, [rows]);
                productMap.SetItems(rows); Application.DoEvents();
                var productTiles = (List<MapTile>)typeof(TreemapControl).GetField("tiles", flags)!.GetValue(productMap)!;
                var productPoint = Center(productTiles[0]);
                typeof(TreemapControl).GetMethod("OnMouseMove", flags)!.Invoke(productMap,
                    [new MouseEventArgs(MouseButtons.None, 0, productPoint.X, productPoint.Y, 0)]);
                typeof(TreemapControl).GetMethod("OnMouseClick", flags)!.Invoke(productMap,
                    [new MouseEventArgs(MouseButtons.Left, 1, productPoint.X, productPoint.Y, 0)]);
                Pump(100);
                Check(Product<MapItem>("selection") == rows[0] &&
                    ReferenceEquals(typeof(TreemapControl).GetField("selected", flags)!.GetValue(productMap), rows[0]) && productMap.OrbitRunning,
                    "real MainForm click and grid synchronisation preserve the selected orbit");
                var productGrid = Product<DataGridView>("grid");
                productGrid.ClearSelection(); productGrid.Rows[1].Selected = true; Pump(100);
                Check(Product<MapItem>("selection") == rows[1] &&
                    ReferenceEquals(typeof(TreemapControl).GetField("selected", flags)!.GetValue(productMap), rows[1]) && productMap.OrbitRunning,
                    "real MainForm list selection retains the matching tile and orbit");
                Product<CheckBox>("reduceMotion").Checked = true;
                Check(!productMap.MotionRunning && ReferenceEquals(typeof(TreemapControl).GetField("selected", flags)!.GetValue(productMap), rows[1]),
                    "real MainForm reduced motion preserves selection while stopping its timer");
                product.Close();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Treemap state test timed out.");
        if (failure != null) throw new InvalidOperationException("Treemap state test failed.", failure);
        Console.WriteLine("PASS treemap state checks: " + passed);
        return 0;
    }

    private static void CheckPreviewGlow(Action<bool, string> check, float scale)
    {
        // Render fixed phases of the production contour itself. A changing
        // bitmap alone cannot distinguish the approved long, soft edge band
        // from the former short white peak or a hard red outline.
        var contourType = typeof(TreemapControl).GetNestedType("GlowContour", BindingFlags.NonPublic)!;
        var bounds = new RectangleF(16 * scale, 16 * scale, 256 * scale, 176 * scale);
        using var contour = (IDisposable)Activator.CreateInstance(contourType, bounds, scale, Theme.DefaultAccent.ToArgb())!;
        var path = (GraphicsPath)contourType.GetProperty("Path")!.GetValue(contour)!;
        var drawBase = contourType.GetMethod("DrawBase")!;
        var drawMoving = contourType.GetMethod("DrawMovingHighlight")!;
        Bitmap Render(float? phase = null, bool reduced = false)
        {
            var image = new Bitmap((int)Math.Ceiling(288 * scale), (int)Math.Ceiling(208 * scale), PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (phase.HasValue) drawMoving.Invoke(contour, [graphics, phase.Value]);
            else drawBase.Invoke(contour, [graphics, reduced]);
            return image;
        }
        Color StrongestNear(Bitmap image, PointF point)
        {
            Color strongest = Color.Transparent;
            int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var sample = image.GetPixel(x + dx, y + dy);
                    if (sample.A > strongest.A) strongest = sample;
                }
            return strongest;
        }
        using var flattened = (GraphicsPath)path.Clone();
        flattened.Flatten(null, .05f * scale);
        var points = flattened.PathPoints;
        var lengths = points.Select((point, index) =>
        {
            var next = points[(index + 1) % points.Length];
            return MathF.Sqrt(MathF.Pow(next.X - point.X, 2) + MathF.Pow(next.Y - point.Y, 2));
        }).ToArray();
        float perimeter = lengths.Sum();
        PointF At(float fraction)
        {
            float remaining = fraction * perimeter;
            for (int index = 0; index < points.Length; index++)
            {
                float length = lengths[index];
                if (length <= 0) continue;
                if (remaining > length) { remaining -= length; continue; }
                var start = points[index]; var end = points[(index + 1) % points.Length];
                return new PointF(start.X + (end.X - start.X) * remaining / length,
                    start.Y + (end.Y - start.Y) * remaining / length);
            }
            return points[0];
        }
        var line = path.GetBounds();
        var edgeCentres = new[] { new PointF(line.Left + line.Width / 2, line.Top),
            new PointF(line.Right, line.Top + line.Height / 2), new PointF(line.Left + line.Width / 2, line.Bottom),
            new PointF(line.Left, line.Top + line.Height / 2) };
        using var baseImage = Render();
        var cream = StrongestNear(baseImage, edgeCentres[0]);
        check(cream.A is >= 85 and <= 125 && cream.R >= 245 && cream.G >= 220 && cream.B >= 185,
            "rendered complete base is the preview's translucent cream rather than a hard red stroke");
        check(Math.Abs(line.Left - bounds.Left - .75f * scale) < .02f * scale &&
            Math.Abs(path.PathPoints[0].Y - bounds.Top - 8 * scale) < .02f * scale,
            "inset edge and tile fill retain the same rounded corner centres");
        var images = new Bitmap[8];
        try
        {
            const int samples = 720;
            var centres = new double[images.Length];
            for (int phase = 0; phase < images.Length; phase++)
            {
                var image = images[phase] = Render(phase / (float)images.Length);
                int core = 0, halo = 0; double x = 0, y = 0;
                for (int index = 0; index < samples; index++)
                {
                    int alpha = StrongestNear(image, At(index / (float)samples)).A;
                    if (alpha >= 20) halo++;
                    if (alpha < 200) continue;
                    core++; double angle = index * 2 * Math.PI / samples;
                    x += Math.Cos(angle); y += Math.Sin(angle);
                }
                double coreFraction = core / (double)samples, haloFraction = halo / (double)samples;
                check(coreFraction is >= .21 and <= .28 && haloFraction is >= .27 and <= .34 && haloFraction >= coreFraction + .025,
                    "rendered 24% light band and wider 29% halo retain preview proportions at phase " + phase);
                centres[phase] = (Math.Atan2(y, x) / (2 * Math.PI) + 1) % 1;
            }
            double CircularDistance(double left, double right)
            {
                double distance = Math.Abs((left - right + 1) % 1);
                return Math.Min(distance, 1 - distance);
            }
            check(CircularDistance(centres[0], centres[4] + .5) < .025 &&
                (CircularDistance(centres[0], centres[2] + .25) < .025 || CircularDistance(centres[0], centres[2] - .25) < .025),
                "rendered light travels by the requested distance along the closed perimeter");
            check(edgeCentres.All(point => images.Any(image => StrongestNear(image, point).A >= 200)),
                "the travelling band reaches all four physical edges");
            var top = edgeCentres[0];
            var litTop = images.First(image => StrongestNear(image, top).A >= 200);
            int AlphaAt(float distance) => litTop.GetPixel((int)Math.Round(top.X), (int)Math.Round(top.Y + distance * scale)).A;
            int inner = AlphaAt(1.5f), middle = AlphaAt(2.5f), outer = AlphaAt(3.5f);
            check(inner > middle && middle > outer && outer >= 8 && AlphaAt(5.5f) == 0,
                "rendered warm halo fades across its soft edge instead of ending at a hard outline");
            using var reduced = Render(reduced: true);
            check(Enumerable.Range(0, samples).All(index => StrongestNear(reduced, At(index / (float)samples)).A >= 175),
                "reduced motion retains a complete half-opacity light band and halo");
        }
        finally { foreach (var image in images) image?.Dispose(); }
    }
}

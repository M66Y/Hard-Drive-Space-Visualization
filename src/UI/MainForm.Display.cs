namespace DiskAtlas;

internal sealed partial class MainForm
{
    internal DisplayPreferences DisplayOptions { get; private set; } = DisplayPreferences.Default;
    private string displaySettingsPath = DisplayPreferencesStore.PathForUser;
    private long largestMinimum;
    private string? largestExtension;
    private readonly ActionButton largestFilter = new("筛选") { Dock = DockStyle.Fill, Subtle = true, Visible = false };
    private ColumnStyle? largestFilterColumn;
    private CancellationTokenSource? filterChoicesCancellation;

    private async Task EditLargestFilter()
    {
        if (mode != 1 || current == null || showingPreview || scanCancellation != null) return;
        var directory = current; var resultOwner = scan;
        filterChoicesCancellation?.Cancel();
        using var source = new CancellationTokenSource(); filterChoicesCancellation = source;
        largestFilter.Enabled = false;
        try
        {
            var extensions = await Task.Run(() => LargestFileFilterDialog.Extensions(directory, source.Token), source.Token);
            if (IsDisposed || Disposing || source.IsCancellationRequested || !ReferenceEquals(scan, resultOwner) || !ReferenceEquals(current, directory) || mode != 1) return;
            using var dialog = new LargestFileFilterDialog(largestMinimum, typeFilter, extensions);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (IsDisposed || Disposing || !ReferenceEquals(scan, resultOwner) || !ReferenceEquals(current, directory) || mode != 1) return;
            largestMinimum = dialog.MinimumLogicalSize; typeFilter = largestExtension = dialog.Extension;
            UpdateView(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OutOfMemoryException)
        { if (!IsDisposed && !Disposing) status.Text = "无法准备筛选选项，请稍后重试。"; }
        finally
        {
            if (ReferenceEquals(filterChoicesCancellation, source)) filterChoicesCancellation = null;
            if (!IsDisposed && !Disposing) largestFilter.Enabled = true;
        }
    }

    private void UpdateLargestFilterEntry()
    {
        bool visible = mode == 1 && !showingPreview;
        largestFilter.Visible = visible;
        largestFilter.Enabled = scanCancellation == null && filterChoicesCancellation == null;
        largestFilter.Text = largestMinimum > 0 || typeFilter != null ? "筛选 · 已启用" : "筛选";
        if (largestFilterColumn != null) largestFilterColumn.Width = visible ? 114 * DeviceDpi / 96f : 0;
    }

    internal void ApplyDisplayOptions(DisplayPreferences value)
    {
        if (!value.IsValid) throw new InvalidDataException();
        if (IsDisposed || Disposing || value == DisplayOptions) return;
        var previous = DisplayOptions;
        using var hold = layoutPresentation?.BeginGeometry(holdRedraw: true);
        DisplayOptions = value;
        if (previous.Density != value.Density) ApplyReadingDensity();
        // The original image/crop and raw raster remain owned and unchanged.
        // Display-aware cache keys retire only their opaque composites.
        if (compositionReady) PublishCompositionBackdrop(dibCoverage?.RootFrame(ClientSize));
        layoutPresentation?.ResizeSurfaces();
        Invalidate(true);
    }

    private void ApplyReadingDensity()
    {
        if (contentLayoutRoot == null) return;
        float scale = DeviceDpi / 96f;
        bool compact = DisplayOptions.Density == ReadingDensity.Compact;
        float[] heights = compact ? [44, 46, 68, 46] : [50, 52, 80, 52];
        contentLayoutRoot.SuspendLayout();
        try
        {
            for (int i = 0; i < heights.Length; i++) contentLayoutRoot.RowStyles[i].Height = heights[i] * scale;
            contentLayoutRoot.RowStyles[5].Height = (compact ? 58 : 66) * scale;
            contentLayoutRoot.RowStyles[6].Height = (compact ? 42 : 48) * scale;
            contentLayoutRoot.Padding = new Padding((int)(28 * scale), (int)((compact ? 14 : 18) * scale), (int)(28 * scale), (int)((compact ? 10 : 12) * scale));
            ((WallpaperGrid)grid).PerformBufferedScroll(() =>
            {
                grid.RowTemplate.Height = FileListPresentation.RowHeight(DeviceDpi, mode == 1, compact);
                foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
            });
        }
        finally { contentLayoutRoot.ResumeLayout(true); }
    }

    private async Task RestoreSavedDisplay()
    {
        try
        {
            var saved = await Task.Run(() => DisplayPreferencesStore.Load(displaySettingsPath));
            if (!IsDisposed && !Disposing) ApplyDisplayOptions(saved);
        }
        catch (Exception error) when (DisplayPreferencesStore.IsReadError(error))
        {
            if (!IsDisposed && !Disposing) status.Text = "显示设置无法读取，已使用显示默认值。";
        }
    }
}

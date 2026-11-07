using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DiskAtlas;

internal enum DiagnosticKind
{
    Started, Stopped, ScanStarted, PhaseChanged, ScanCompleted, ScanIncomplete,
    ScanCancelled, ScanFailed, BackgroundReadFailed, BackgroundImportFailed,
    BackgroundSaveFailed, BackgroundResetFailed, UnhandledError, QueueOverflow
}
internal enum DiagnosticLevel { Information, Warning, Error }
internal enum ErrorCategory { Memory, Access, IO, Native, Argument, Cancelled, Operation, Unexpected }
public enum RefreshReason
{
    FirstScan, Forced, RootChanged, ContinuousJournal, MissingCheckpoint, JournalUnavailable,
    IncompleteBaseline, RootIdentityChanged, MissingIdentity, JournalFailure, VerificationFailure, VolumeChanged
}

// Only controlled identifiers and numbers may cross this boundary. Never add arbitrary text here.
internal sealed record DiagnosticRecord
{
    public int Schema { get; init; } = 1;
    public DateTimeOffset TimeUtc { get; init; }
    public string? Build { get; init; } = typeof(DiagnosticRecord).Assembly.GetName().Version?.ToString();
    public Guid Session { get; init; }
    public long? Scan { get; init; }
    public DiagnosticKind Kind { get; init; }
    public ScanMode? Mode { get; init; }
    public ScanPhase? Phase { get; init; }
    public RefreshMethod? Method { get; init; }
    public RefreshReason? Reason { get; init; }
    public JournalState? Journal { get; init; }
    public int? NativeError { get; init; }
    public int? Workers { get; init; }
    public int? PeakWorkers { get; init; }
    public long? Files { get; init; }
    public long? Directories { get; init; }
    public long? Skipped { get; init; }
    public long? UnknownAllocation { get; init; }
    public long? VerifiedFiles { get; init; }
    public long? VerifiedDirectories { get; init; }
    public long? ReadDirectories { get; init; }
    public long? ReusedFiles { get; init; }
    public long? Dropped { get; init; }
    public double? Milliseconds { get; init; }
    public ErrorCategory? Category { get; init; }
    public int? ErrorCode { get; init; }

    [JsonIgnore] public DiagnosticLevel Level => Kind switch
    {
        DiagnosticKind.ScanCompleted when Reason is RefreshReason.JournalUnavailable or RefreshReason.MissingCheckpoint or
            RefreshReason.IncompleteBaseline or RefreshReason.RootIdentityChanged or RefreshReason.MissingIdentity or
            RefreshReason.JournalFailure or RefreshReason.VerificationFailure or RefreshReason.VolumeChanged => DiagnosticLevel.Warning,
        DiagnosticKind.ScanIncomplete or DiagnosticKind.QueueOverflow => DiagnosticLevel.Warning,
        DiagnosticKind.ScanFailed or DiagnosticKind.BackgroundReadFailed or DiagnosticKind.BackgroundImportFailed or
        DiagnosticKind.BackgroundSaveFailed or DiagnosticKind.BackgroundResetFailed or DiagnosticKind.UnhandledError => DiagnosticLevel.Error,
        _ => DiagnosticLevel.Information
    };

    public string Summary()
    {
        var lines = new List<string> { $"{Label} · {TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}" };
        if (Scan.HasValue) lines.Add($"扫描序号：{Scan}");
        if (Mode is { } mode) lines.Add("策略：" + (mode switch { ScanMode.LowImpact => "低占用", ScanMode.HighSpeed => "高速", _ => "自动" }));
        if (Phase is { } phase) lines.Add("阶段：" + ScanProgressControl.PhaseName(phase));
        if (Method is { } method) lines.Add("刷新方式：" + RefreshPresentation.MethodName(method));
        if (Reason is { } reason) lines.Add("原因：" + (reason switch
        {
            RefreshReason.FirstScan => "首次完整扫描", RefreshReason.Forced => "用户强制完整扫描", RefreshReason.RootChanged => "扫描目录切换",
            RefreshReason.ContinuousJournal => "变化日志连续，重扫变化目录", RefreshReason.MissingCheckpoint => "上次无有效游标，核验全部元信息",
            RefreshReason.JournalUnavailable => "日志不可用，核验全部元信息", RefreshReason.IncompleteBaseline => "旧结果不完整，回退完整扫描",
            RefreshReason.RootIdentityChanged => "根目录身份无法确认，回退完整扫描", RefreshReason.MissingIdentity => "旧条目缺少身份，回退完整扫描",
            RefreshReason.JournalFailure => "日志读取或连续性检查失败，回退完整扫描", RefreshReason.VolumeChanged => "卷发生变化，回退完整扫描",
            _ => "刷新核验未完成，回退完整扫描"
        }));
        void Count(string label, long? value) { if (value.HasValue) lines.Add($"{label}：{value:N0}"); }
        Count("文件", Files); Count("子目录", Directories); Count("跳过项", Skipped); Count("占用未知", UnknownAllocation);
        Count("并发上限", Workers); Count("并发峰值", PeakWorkers); Count("重扫目录", ReadDirectories);
        Count("核验文件", VerifiedFiles); Count("核验目录", VerifiedDirectories); Count("复用文件", ReusedFiles); Count("队列遗漏", Dropped);
        if (Milliseconds.HasValue) lines.Add($"耗时：{Milliseconds:N1} ms");
        if (Category.HasValue) lines.Add($"错误类别：{Category}；错误码：0x{ErrorCode.GetValueOrDefault():X8}");
        if (NativeError is { } native && native != 0) lines.Add($"系统错误码：{native}");
        return string.Join("\n", lines);
    }
    [JsonIgnore] public string Label => Kind switch
    {
        DiagnosticKind.Started => "程序启动", DiagnosticKind.Stopped => "正常退出",
        DiagnosticKind.ScanStarted => "开始扫描", DiagnosticKind.PhaseChanged => "扫描阶段变化",
        DiagnosticKind.ScanCompleted => "扫描完成", DiagnosticKind.ScanIncomplete => "扫描部分完成",
        DiagnosticKind.ScanCancelled => "扫描取消", DiagnosticKind.ScanFailed => "扫描失败",
        DiagnosticKind.BackgroundReadFailed => "背景读取失败", DiagnosticKind.BackgroundImportFailed => "背景导入失败",
        DiagnosticKind.BackgroundSaveFailed => "背景保存失败", DiagnosticKind.BackgroundResetFailed => "背景恢复失败",
        DiagnosticKind.UnhandledError => "程序异常", DiagnosticKind.QueueOverflow => "诊断队列遗漏", _ => "未知"
    };

    public bool IsValid() => Schema == 1 && TimeUtc.Offset == TimeSpan.Zero && TimeUtc.Year is >= 2020 and < 9999 &&
        (Build == null || Build.Length <= 32 && Regex.IsMatch(Build, @"\A\d{1,5}(\.\d{1,5}){2,3}\z", RegexOptions.CultureInvariant)) &&
        Enum.IsDefined(Kind) && Valid(Mode) && Valid(Phase) && Valid(Method) && Valid(Reason) && Valid(Journal) && Valid(Category) &&
        new long?[] { Scan, Workers, PeakWorkers, Files, Directories, Skipped, UnknownAllocation, VerifiedFiles,
            VerifiedDirectories, ReadDirectories, ReusedFiles, Dropped }.All(n => !n.HasValue || n.Value >= 0) &&
        (!Milliseconds.HasValue || double.IsFinite(Milliseconds.Value) && Milliseconds.Value >= 0);
    private static bool Valid<T>(T? value) where T : struct, Enum => !value.HasValue || Enum.IsDefined(value.Value);
}

internal static class DiagnosticCodec
{
    internal const int MaxRecordBytes = 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Encode(DiagnosticRecord record)
    {
        if (!record.IsValid()) throw new InvalidDataException();
        string json = JsonSerializer.Serialize(record, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxRecordBytes) throw new InvalidDataException();
        return json;
    }

    public static DiagnosticRecord? Decode(string line)
    {
        if (line.Length > MaxRecordBytes || Encoding.UTF8.GetByteCount(line) > MaxRecordBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!keys.Add(property.Name)) return null;
            if (!new[] { "schema", "timeUtc", "build", "session", "kind" }.All(keys.Contains)) return null;
            var value = JsonSerializer.Deserialize<DiagnosticRecord>(line, Json);
            return value?.IsValid() == true ? value : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    public static string Preview(IEnumerable<DiagnosticRecord> selection, long dropped, int skipped, bool truncated, bool storageIncomplete = false)
    {
        // Regenerate each field; never export raw log lines or user-authored notes.
        var pretty = new JsonSerializerOptions(Json) { WriteIndented = true };
        var lines = selection.Select(record => { _ = Encode(record); return record.Summary() + "\n\n" + JsonSerializer.Serialize(record, pretty); }).ToArray();
        return "AsukaLens local diagnostics / 本地诊断\nSchema=1\n" +
            $"Records={lines.Length}\nQueueDropped={Math.Max(0, dropped)}\nUnreadableOrRejected={Math.Max(0, skipped)}\n" +
            $"ViewLimited={truncated}\nStorageIncomplete={storageIncomplete}\n\n" + string.Join("\n\n---\n\n", lines) + "\n";
    }

    public static ErrorCategory Classify(Exception error) => error switch
    {
        OutOfMemoryException => ErrorCategory.Memory, UnauthorizedAccessException => ErrorCategory.Access,
        IOException => ErrorCategory.IO, System.ComponentModel.Win32Exception => ErrorCategory.Native,
        ArgumentException => ErrorCategory.Argument, OperationCanceledException => ErrorCategory.Cancelled,
        InvalidOperationException => ErrorCategory.Operation, _ => ErrorCategory.Unexpected
    };

    public static DiagnosticRecord? ReadLegacy(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 512) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator < 1 || !fields.TryAdd(line[..separator], line[(separator + 1)..].TrimEnd('\r'))) return null;
        }
        if (fields.Count != 5 || fields.GetValueOrDefault("App") != "AsukaLens" || fields.GetValueOrDefault("Schema") != "1" ||
            !DateTimeOffset.TryParseExact(fields.GetValueOrDefault("TimeUtc"), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) || time.Offset != TimeSpan.Zero ||
            !Enum.TryParse<ErrorCategory>(fields.GetValueOrDefault("Category"), out var category) || !Enum.IsDefined(category) ||
            fields.GetValueOrDefault("Category") != category.ToString() ||
            fields.GetValueOrDefault("Code") is not { Length: 10 } code || !code.StartsWith("0x", StringComparison.Ordinal) ||
            !int.TryParse(code.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int error)) return null;
        var result = new DiagnosticRecord { TimeUtc = time, Build = null, Kind = DiagnosticKind.UnhandledError, Category = category, ErrorCode = error };
        return result.IsValid() ? result : null;
    }
}

using System.Globalization;
using System.Text;

namespace DiskAtlas;

internal sealed record ErrorReportResult(string Category, string Code, bool Saved, string? Path)
{
    public string UserMessage => $"程序遇到问题。错误类别：{Category}，代码：{Code}。\n\n" +
        (Saved ? $"不含文件路径、文件名和堆栈的诊断已保存到：\n{Path}" : "本次诊断未能写入本地存储。") +
        "\n\n可以重新打开软件后重试。";
}

// A single bounded, local report. Never serialize the exception or its message/data/stack.
internal static class ErrorReport
{
    public static ErrorReportResult Write(Exception error, string? directory = null)
    {
        string category = error switch
        {
            OutOfMemoryException => "Memory",
            UnauthorizedAccessException => "Access",
            IOException => "IO",
            System.ComponentModel.Win32Exception => "Native",
            ArgumentException => "Argument",
            OperationCanceledException => "Cancelled",
            InvalidOperationException => "Operation",
            _ => "Unexpected"
        };
        string code = "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);
        string? pending = null;
        try
        {
            directory ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsukaLens");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "last-error.log");
            pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string report = "App=AsukaLens\nSchema=1\nTimeUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                "\nCategory=" + category + "\nCode=" + code + "\n";
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(report));
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
            return new(category, code, true, path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException or OutOfMemoryException)
        {
            return new(category, code, false, null);
        }
        finally
        {
            if (pending != null)
            {
                try { File.Delete(pending); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }
}

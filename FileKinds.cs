namespace DiskAtlas;

public static class FileKinds
{
    public static string Category(string extension) => extension switch
    {
        "文件夹" => "文件夹",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" => "视频",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" or ".tif" or ".tiff" or ".psd" or ".raw" or ".ico" => "图片",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".wma" => "音频",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" => "压缩包",
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".csv" or ".epub" => "文档",
        ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".html" or ".css" or ".json" or ".cpp" or ".c" or ".h" or ".java" or ".rs" or ".go" or ".sql" or ".xml" or ".yaml" or ".yml" => "代码",
        _ => "其他"
    };
}

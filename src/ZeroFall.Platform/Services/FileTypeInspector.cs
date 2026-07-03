using System;
using System.Collections.Generic;
using System.IO;
using HeyRed.Mime;

namespace ZeroFall.Platform.Services;

public interface IFileTypeInspector
{
    FileTypeProbeResult Probe(string filePath);
    bool IsTextFile(string filePath);
}

public sealed record FileTypeProbeResult(
    string? MimeType,
    string? Description,
    string? Extension,
    bool IsText);

public sealed class FileTypeInspector : IFileTypeInspector
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private static readonly HashSet<string> TextMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/xml",
        "application/javascript",
        "application/typescript",
        "application/x-sh",
        "application/x-python",
        "application/sql",
        "application/yaml",
        "application/toml",
        "application/xhtml+xml",
        "application/csv",
        "application/x-httpd-php",
        "application/x-ruby",
        "application/ecmascript",
        "application/x-yaml"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".dat", ".obj", ".o", ".a", ".lib",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".avif",
        ".zip", ".gz", ".7z", ".rar", ".tar", ".bz2", ".xz", ".zst",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".db", ".sqlite", ".sqlite3", ".mdb",
        ".mp3", ".mp4", ".avi", ".mkv", ".wav", ".flac", ".webm",
        ".class", ".jar", ".wasm",
        ".pyc", ".pyo",
    };

    static FileTypeInspector()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (InitLock)
        {
            if (_initialized)
                return;

            var magicPath = ResolveMagicFilePath();
            if (magicPath != null)
                MimeGuesser.MagicFilePath = magicPath;

            _initialized = true;
        }
    }

    public FileTypeProbeResult Probe(string filePath)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new FileTypeProbeResult(null, null, Path.GetExtension(filePath), false);

        var ext = Path.GetExtension(filePath);
        if (new FileInfo(filePath).Length == 0)
            return ProbeEmptyFile(ext);

        try
        {
            var fileType = MimeGuesser.GuessFileType(filePath);
            var mime = string.IsNullOrWhiteSpace(fileType.MimeType) ? "application/octet-stream" : fileType.MimeType;
            ext = NormalizeExtension(fileType.Extension, filePath);

            return new FileTypeProbeResult(mime, mime, ext, IsTextMime(mime));
        }
        catch
        {
            return new FileTypeProbeResult("application/octet-stream", null, ext, false);
        }
    }

    public bool IsTextFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        if (new FileInfo(filePath).Length == 0)
            return ProbeEmptyFile(Path.GetExtension(filePath)).IsText;

        return Probe(filePath).IsText;
    }

    /// <summary>0 字节文件无 magic 特征，MimeGuesser 常误判为二进制；按扩展名回退为文本。</summary>
    private static FileTypeProbeResult ProbeEmptyFile(string? ext)
    {
        var isText = !IsKnownBinaryExtension(ext);
        var mime = isText ? "text/plain" : "application/octet-stream";
        return new FileTypeProbeResult(mime, mime, ext, isText);
    }

    private static bool IsKnownBinaryExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && BinaryExtensions.Contains(ext);

    private static string? ResolveMagicFilePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var rid = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsLinux() ? "linux-x64"
            : OperatingSystem.IsMacOS() ? "osx-x64"
            : "win-x64";

        var candidates = new[]
        {
            Path.Combine(baseDir, "magic.mgc"),
            Path.Combine(baseDir, "runtimes", rid, "native", "magic.mgc"),
            Path.Combine(baseDir, "runtimes", "win-x64", "native", "magic.mgc")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? NormalizeExtension(string? guessedExt, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(guessedExt))
            return guessedExt.StartsWith('.') ? guessedExt : "." + guessedExt;

        return Path.GetExtension(filePath);
    }

    private static bool IsTextMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime))
            return false;

        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return TextMimeTypes.Contains(mime);
    }
}

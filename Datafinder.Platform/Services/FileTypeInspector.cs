using System;
using System.Collections.Generic;
using System.IO;
using HeyRed.Mime;

namespace Datafinder.Platform.Services;

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

        try
        {
            var fileType = MimeGuesser.GuessFileType(filePath);
            var mime = string.IsNullOrWhiteSpace(fileType.MimeType) ? "application/octet-stream" : fileType.MimeType;
            var ext = NormalizeExtension(fileType.Extension, filePath);

            return new FileTypeProbeResult(mime, mime, ext, IsTextMime(mime));
        }
        catch
        {
            var ext = Path.GetExtension(filePath);
            return new FileTypeProbeResult("application/octet-stream", null, ext, false);
        }
    }

    public bool IsTextFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        return Probe(filePath).IsText;
    }

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

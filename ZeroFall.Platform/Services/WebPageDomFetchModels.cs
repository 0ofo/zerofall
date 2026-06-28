namespace ZeroFall.Platform.Services;

public sealed class WebPageDomFetchResult
{
    public required string Url { get; init; }
    public bool Success { get; init; }
    public string? FinalUrl { get; init; }
    public string? Html { get; init; }
    public string? Error { get; init; }
}

namespace SvnFlux.Http;

public sealed class SvnHttpOptions {
    public string SpecialResourceSegment { get; set; } = "!svn";
    public long MaximumXmlRequestSize { get; set; } = 1024 * 1024;
    public Action<SvnHttpTrace>? Trace { get; set; }
}

public readonly record struct SvnHttpTrace(string Method, string Path, int StatusCode, string? Detail = null);

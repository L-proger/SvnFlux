namespace SvnFlux.Http;

public sealed class SvnHttpOptions {
    public string SpecialResourceSegment { get; set; } = "!svn";
    public long MaximumXmlRequestSize { get; set; } = 1024 * 1024;
    public long MaximumPutSize { get; set; } = 1024L * 1024 * 1024;
    public int MaximumActiveTransactions { get; set; } = 128;
    public TimeSpan TransactionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public Action<SvnHttpTrace>? Trace { get; set; }
}

public readonly record struct SvnHttpTrace(string Method, string Path, int StatusCode, string? Detail = null);

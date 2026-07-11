namespace SvnFlux.Core;

public sealed record SvnRevisionProperties(
    string? Author,
    DateTimeOffset? Date,
    string? LogMessage,
    SvnPropertyCollection CustomProperties)
{
    public static SvnRevisionProperties Empty { get; } = new(null, null, null, SvnPropertyCollection.Empty);
}

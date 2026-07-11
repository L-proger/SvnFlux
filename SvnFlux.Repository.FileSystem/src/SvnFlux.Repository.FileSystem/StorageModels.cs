using System.Text.Json;
using System.Text.Json.Serialization;
using SvnFlux.Core;

namespace SvnFlux.Repository.FileSystem;

internal static class StorageModels
{
    public const string FormatName = "SvnFlux.Repository.FileSystem";
    public const int FormatVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Dictionary<string, string> SerializeProperties(SvnPropertyCollection properties) =>
        properties.ToDictionary(property => property.Name, property => Convert.ToBase64String(property.Value.Span), StringComparer.Ordinal);

    public static SvnPropertyCollection DeserializeProperties(IReadOnlyDictionary<string, string>? properties) =>
        properties is null
            ? SvnPropertyCollection.Empty
            : new SvnPropertyCollection(properties.Select(property => new SvnProperty(property.Key, Convert.FromBase64String(property.Value))));
}

internal sealed record FormatDocument(string Name, int Version, string LinkMode);
internal sealed record PendingPublicationDocument(string TransactionName, long Revision);

internal sealed class ManifestDocument
{
    public int Version { get; init; } = StorageModels.FormatVersion;
    public List<NodeDocument> Nodes { get; init; } = [];
}

internal sealed class NodeDocument
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public long? BodyRevision { get; set; }
    public string? BodyPath { get; set; }
    public long LastChangedRevision { get; set; }
    public string? CopyFromPath { get; set; }
    public long? CopyFromRevision { get; set; }
    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    public NodeDocument Clone() => new()
    {
        Path = Path,
        Kind = Kind,
        BodyRevision = BodyRevision,
        BodyPath = BodyPath,
        LastChangedRevision = LastChangedRevision,
        CopyFromPath = CopyFromPath,
        CopyFromRevision = CopyFromRevision,
        Properties = new Dictionary<string, string>(Properties, StringComparer.Ordinal)
    };
}

internal sealed class RevisionPropertiesDocument
{
    public string? Author { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string? LogMessage { get; init; }
    public Dictionary<string, string> CustomProperties { get; init; } = new(StringComparer.Ordinal);

    public static RevisionPropertiesDocument FromCore(SvnRevisionProperties properties) => new()
    {
        Author = properties.Author,
        Date = properties.Date,
        LogMessage = properties.LogMessage,
        CustomProperties = StorageModels.SerializeProperties(properties.CustomProperties)
    };

    public SvnRevisionProperties ToCore() => new(
        Author,
        Date,
        LogMessage,
        StorageModels.DeserializeProperties(CustomProperties));
}

internal sealed class ChangesDocument
{
    public List<ChangeDocument> Changes { get; init; } = [];
}

internal sealed class ChangeDocument
{
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required string Kind { get; init; }
    public bool TextModified { get; init; }
    public bool PropertiesModified { get; init; }
    public string? CopyFromPath { get; init; }
    public long? CopyFromRevision { get; init; }
}

internal sealed class LockDocument {
    public required string Token { get; init; }
    public required string Path { get; init; }
    public required string Owner { get; init; }
    public string? Comment { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? Expires { get; init; }
}

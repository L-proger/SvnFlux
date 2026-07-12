using SvnFlux.Core;

namespace SvnFlux.Http;

internal enum SvnHttpResourceKind { Public, Me, Revision, RevisionRoot, Transaction, TransactionRoot }

internal readonly record struct SvnHttpResource(SvnHttpResourceKind Kind, SvnRevision? Revision, SvnRepositoryPath Path, string? TransactionId = null) {
    public static bool TryParse(string? value, string specialSegment, out SvnHttpResource resource) {
        value ??= "";
        if (value.Contains("%2f", StringComparison.OrdinalIgnoreCase) || value.Contains("%5c", StringComparison.OrdinalIgnoreCase)) { resource = default; return false; }
        value = value.Trim('/');
        if (value.Length == 0) { resource = new(SvnHttpResourceKind.Public, null, new("")); return true; }
        var parts = value.Split('/');
        if (parts[0] != specialSegment) {
            try { resource = new(SvnHttpResourceKind.Public, null, new(Uri.UnescapeDataString(value))); return true; }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException) { resource = default; return false; }
        }
        if (parts.Length == 2 && parts[1] == "me") { resource = new(SvnHttpResourceKind.Me, null, new("")); return true; }
        if (parts.Length == 3 && parts[1] == "rev" && long.TryParse(parts[2], out var revision) && revision >= 0) {
            resource = new(SvnHttpResourceKind.Revision, new(revision), new("")); return true;
        }
        if (parts.Length >= 3 && parts[1] == "rvr" && long.TryParse(parts[2], out revision) && revision >= 0) {
            try { resource = new(SvnHttpResourceKind.RevisionRoot, new(revision), new(Uri.UnescapeDataString(string.Join('/', parts.Skip(3))))); return true; }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException) { resource = default; return false; }
        }
        if (parts.Length == 3 && parts[1] == "txn" && IsTransactionId(parts[2])) {
            resource = new(SvnHttpResourceKind.Transaction, null, new(""), parts[2]); return true;
        }
        if (parts.Length >= 3 && parts[1] == "txr" && IsTransactionId(parts[2])) {
            try { resource = new(SvnHttpResourceKind.TransactionRoot, null, new(Uri.UnescapeDataString(string.Join('/', parts.Skip(3)))), parts[2]); return true; }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException) { resource = default; return false; }
        }
        resource = default;
        return false;
    }

    private static bool IsTransactionId(string value) =>
        value.Length == 32 && value.All(character => char.IsAsciiHexDigit(character));
}

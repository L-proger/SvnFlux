using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal sealed class SvnHttpProtocolException(int statusCode, int errorCode, string message) : Exception(message) {
    public int StatusCode { get; } = statusCode;
    public int ErrorCode { get; } = errorCode;
}

internal static class SvnHttpErrors {
    internal static bool TryMap(Exception exception, out int statusCode, out int errorCode) {
        (statusCode, errorCode) = exception switch {
            SvnHttpProtocolException value => (value.StatusCode, value.ErrorCode),
            BadHttpRequestException value => (value.StatusCode, 175002),
            SvnPathNotFoundException => (StatusCodes.Status404NotFound, 160013),
            SvnInvalidRevisionException => (StatusCodes.Status404NotFound, 160006),
            SvnNodeKindMismatchException => (StatusCodes.Status409Conflict, 160017),
            SvnOutOfDateException => (StatusCodes.Status409Conflict, 160028),
            SvnRevisionPropertyConflictException => (StatusCodes.Status412PreconditionFailed, 160049),
            SvnRepositoryBusyException => (StatusCodes.Status503ServiceUnavailable, 165005),
            SvnLockException value => (StatusCodes.Status423Locked, LockCode(value.Message)),
            SvnHttpTransactionNotFoundException => (StatusCodes.Status404NotFound, 160031),
            SvnHttpTransactionStateException => (StatusCodes.Status409Conflict, 160012),
            XmlException => (StatusCodes.Status400BadRequest, 130003),
            InvalidDataException => (StatusCodes.Status400BadRequest, 140001),
            EndOfStreamException => (StatusCodes.Status400BadRequest, 140001),
            NotSupportedException => (StatusCodes.Status400BadRequest, 140002),
            _ => default
        };
        return statusCode != 0;
    }

    private static int LockCode(string message) {
        if (message.Contains("already locked", StringComparison.OrdinalIgnoreCase)) return 160035;
        if (message.Contains("not locked", StringComparison.OrdinalIgnoreCase)) return 160040;
        if (message.Contains("token", StringComparison.OrdinalIgnoreCase)) return 160037;
        return 160038;
    }
}

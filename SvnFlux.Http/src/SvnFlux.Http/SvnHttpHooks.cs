using SvnFlux.Core;

namespace SvnFlux.Http;

public interface ISvnHttpHook {
    ValueTask BeforeCommitAsync(SvnHttpCommitHookContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask AfterCommitAsync(SvnHttpCommitHookContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask BeforeLockAsync(SvnHttpLockHookContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask AfterLockAsync(SvnHttpLockHookContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public sealed record SvnHttpCommitHookContext(ISvnWritableRepository Repository, string TransactionId, SvnCommitRequest Request, SvnRevision? CommittedRevision = null);
public sealed record SvnHttpLockHookContext(ISvnWritableRepository Repository, SvnLockRequest Request, SvnLock? Lock = null);
public sealed class SvnHttpHookRejectedException(string message) : Exception(message);

internal static class SvnHttpHookPipeline {
    internal static async ValueTask BeforeCommitAsync(IEnumerable<ISvnHttpHook> hooks, SvnHttpCommitHookContext context, CancellationToken token) {
        foreach (var hook in hooks) await hook.BeforeCommitAsync(context, token).ConfigureAwait(false);
    }

    internal static async ValueTask AfterCommitAsync(IEnumerable<ISvnHttpHook> hooks, SvnHttpCommitHookContext context, Action<Exception>? error) {
        foreach (var hook in hooks) {
            try { await hook.AfterCommitAsync(context).ConfigureAwait(false); }
            catch (Exception exception) { error?.Invoke(exception); }
        }
    }

    internal static async ValueTask BeforeLockAsync(IEnumerable<ISvnHttpHook> hooks, SvnHttpLockHookContext context, CancellationToken token) {
        foreach (var hook in hooks) await hook.BeforeLockAsync(context, token).ConfigureAwait(false);
    }

    internal static async ValueTask AfterLockAsync(IEnumerable<ISvnHttpHook> hooks, SvnHttpLockHookContext context, Action<Exception>? error) {
        foreach (var hook in hooks) {
            try { await hook.AfterLockAsync(context).ConfigureAwait(false); }
            catch (Exception exception) { error?.Invoke(exception); }
        }
    }
}

using System.Net;
using System.Net.Sockets;
using SvnFlux.RaSvn.Wire;

namespace SvnFlux.RaSvn.Server;

public sealed class SvnServer : IAsyncDisposable
{
    private readonly SvnServerOptions _options;
    private readonly ISvnRepositoryResolver _repositoryResolver;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _sessionSlots;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _sessions = [];

    public SvnServer(ISvnRepositoryResolver repositoryResolver, SvnServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryResolver);
        _repositoryResolver = repositoryResolver;
        _options = options ?? new SvnServerOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumConcurrentSessions);
        if (_options.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port is outside the valid TCP range.");
        }

        _listener = new TcpListener(_options.Address, _options.Port);
        _sessionSlots = new SemaphoreSlim(_options.MaximumConcurrentSessions);
    }

    public IPEndPoint? LocalEndpoint => _listener.LocalEndpoint as IPEndPoint;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        _listener.Start();
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                await _sessionSlots.WaitAsync(linked.Token).ConfigureAwait(false);
                var task = HandleClientAsync(client, linked.Token);
                lock (_sessions)
                {
                    _sessions.Add(task);
                }

                _ = task.ContinueWith(completed =>
                {
                    lock (_sessions)
                    {
                        _sessions.Remove(completed);
                    }

                    _sessionSlots.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            Task[] sessions;
            lock (_sessions)
            {
                sessions = [.. _sessions];
            }

            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
    }

    public void Stop() => _stop.Cancel();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _sessionSlots.Dispose();
        _stop.Dispose();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var session = new SvnServerSession(client.GetStream(), _repositoryResolver, _options);
            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or SvnWireProtocolException)
            {
                // Malformed or disconnected clients only terminate their own isolated session.
                _options.DiagnosticLog?.Invoke($"Session ended: {exception.Message}");
            }
            catch (Exception exception) {
                _options.DiagnosticLog?.Invoke($"Session failed: {exception}");
            }
        }
    }
}

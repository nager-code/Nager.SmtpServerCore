using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nager.SmtpServerCore
{
    internal sealed class SmtpSessionManager : IDisposable
    {
        readonly SmtpServer _smtpServer;
        readonly ConcurrentDictionary<Guid, SmtpSessionHandle> _sessions = new ConcurrentDictionary<Guid, SmtpSessionHandle>();
        readonly SemaphoreSlim _sessionLimitSemaphore;
        bool _disposed = false;

        internal SmtpSessionManager(SmtpServer smtpServer, int maxConcurrentSessions)
        {
            _sessionLimitSemaphore = new SemaphoreSlim(maxConcurrentSessions);
            _smtpServer = smtpServer;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _sessionLimitSemaphore.Dispose();
        }

        internal void Run(SmtpSessionContext sessionContext, CancellationToken cancellationToken)
        {
            var handle = new SmtpSessionHandle(new SmtpSession(sessionContext), sessionContext);
            Add(handle);

            handle.CompletionTask = RunAsync(handle, cancellationToken).ContinueWith(task =>
            {
                Remove(handle);
            });
        }

        async Task RunAsync(SmtpSessionHandle handle, CancellationToken cancellationToken)
        {
            using var sessionTimeoutCancellationTokenSource = new CancellationTokenSource(handle.SessionContext.EndpointDefinition.SessionTimeout);

            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionTimeoutCancellationTokenSource.Token);

            var sessionSlotAcquired = false;

            try
            {
                // waiting for a free slot is part of the session, when the wait is cancelled (server shutdown, listener
                // cancellation or session timeout) the connection has to be cleaned up like any other cancelled session
                await _sessionLimitSemaphore.WaitAsync(linkedTokenSource.Token).ConfigureAwait(false);
                sessionSlotAcquired = true;

                _smtpServer.OnSessionCreated(new SessionEventArgs(handle.SessionContext));

                await UpgradeAsync(handle, linkedTokenSource.Token);

                linkedTokenSource.Token.ThrowIfCancellationRequested();

                await handle.Session.RunAsync(linkedTokenSource.Token);

                _smtpServer.OnSessionCompleted(new SessionEventArgs(handle.SessionContext));
            }
            catch (OperationCanceledException)
            {
                _smtpServer.OnSessionCancelled(new SessionEventArgs(handle.SessionContext));
            }
            catch (Exception ex)
            {
                _smtpServer.OnSessionFaulted(new SessionFaultedEventArgs(handle.SessionContext, ex));
            }
            finally
            {
                await handle.SessionContext.Pipe.Input.CompleteAsync().ConfigureAwait(false);
                
                handle.SessionContext.Pipe.Dispose();

                if (sessionSlotAcquired)
                {
                    _sessionLimitSemaphore.Release();
                }
            }
        }

        async Task UpgradeAsync(SmtpSessionHandle handle, CancellationToken cancellationToken)
        {
            var endpoint = handle.SessionContext.EndpointDefinition;

            if (endpoint.IsSecure && endpoint.CertificateFactory != null)
            {
                var serverCertificate = endpoint.CertificateFactory.GetServerCertificate(handle.SessionContext);

                await handle.SessionContext.Pipe.UpgradeAsync(serverCertificate, endpoint.SupportedSslProtocols, cancellationToken).ConfigureAwait(false);
            }
        }

        internal Task WaitAsync()
        {
            var tasks = _sessions.Values.Select(session => session.CompletionTask).ToList().AsReadOnly();
            return Task.WhenAll(tasks);
        }

        void Add(SmtpSessionHandle handle)
        {
            _sessions.TryAdd(handle.SessionContext.SessionId, handle);
        }

        void Remove(SmtpSessionHandle handle)
        {
            _sessions.TryRemove(handle.SessionContext.SessionId, out _);
        }

        class SmtpSessionHandle
        {
            public SmtpSessionHandle(SmtpSession session, SmtpSessionContext sessionContext)
            {
                Session = session;
                SessionContext = sessionContext;
            }

            public SmtpSession Session { get; }
            
            public SmtpSessionContext SessionContext { get; }

            public Task CompletionTask { get; set; }
        }
    }
}

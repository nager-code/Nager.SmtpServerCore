using System;
using System.Net;
using System.Net.Sockets;

namespace Nager.SmtpServerCore.Net
{
    /// <summary>
    /// Endpoint Listener Factory
    /// </summary>
    public class EndpointListenerFactory : IEndpointListenerFactory
    {
        /// <summary>
        /// Default Endpoint Listener Factory
        /// </summary>
        internal static readonly IEndpointListenerFactory Default = new EndpointListenerFactory();

        /// <summary>
        /// Raised when an endpoint has been started.
        /// </summary>
        public event EventHandler<EndpointEventArgs> EndpointStarted;

        /// <summary>
        /// Raised when an endpoint has been stopped.
        /// </summary>
        public event EventHandler<EndpointEventArgs> EndpointStopped;

        /// <inheritdoc />
        public virtual IEndpointListener CreateListener(IEndpointDefinition endpointDefinition)
        {
            var listenEndpoint = ResolveListenEndpoint(endpointDefinition.Endpoint, Socket.OSSupportsIPv6);

            var tcpListener = new TcpListener(listenEndpoint);

            if (listenEndpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // DualMode can only be enabled on an IPv6 socket, setting it on an IPv4 socket throws a NotSupportedException.
                tcpListener.Server.DualMode = true;
            }

            tcpListener.Start();

            var endpointEventArgs = new EndpointEventArgs(endpointDefinition, tcpListener.LocalEndpoint);
            OnEndpointStarted(endpointEventArgs);

            return new EndpointListener(tcpListener, () => OnEndpointStopped(endpointEventArgs));
        }

        /// <summary>
        /// Resolves the endpoint that the listener should bind to.
        /// </summary>
        /// <param name="endpoint">The endpoint from the endpoint definition.</param>
        /// <param name="osSupportsIPv6">true if the operating system supports IPv6, false if not.</param>
        /// <returns>The IPv6 endpoint when IPv6 is available, otherwise the equivalent IPv4 endpoint for the wildcard and loopback addresses.</returns>
        internal static IPEndPoint ResolveListenEndpoint(IPEndPoint endpoint, bool osSupportsIPv6)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (endpoint.AddressFamily != AddressFamily.InterNetworkV6 || osSupportsIPv6)
            {
                return endpoint;
            }

            if (endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                return new IPEndPoint(IPAddress.Any, endpoint.Port);
            }

            if (endpoint.Address.Equals(IPAddress.IPv6Loopback))
            {
                return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
            }

            if (endpoint.Address.IsIPv4MappedToIPv6)
            {
                return new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
            }

            return endpoint;
        }

        /// <summary>
        /// Raises the EndPointStarted Event.
        /// </summary>
        /// <param name="args">The event data.</param>
        protected virtual void OnEndpointStarted(EndpointEventArgs args)
        {
            EndpointStarted?.Invoke(this, args);
        }

        /// <summary>
        /// Raises the EndPointStopped Event.
        /// </summary>
        /// <param name="args">The event data.</param>
        protected virtual void OnEndpointStopped(EndpointEventArgs args)
        {
            EndpointStopped?.Invoke(this, args);
        }
    }
}

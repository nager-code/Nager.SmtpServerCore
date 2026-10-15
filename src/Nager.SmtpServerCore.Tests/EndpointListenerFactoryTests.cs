using Nager.SmtpServerCore.Net;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class EndpointListenerFactoryTests
    {
        [Fact]
        public void CanCreateListenerForIPv4Endpoint()
        {
            // arrange
            var endpointDefinition = new EndpointDefinitionBuilder()
                .Endpoint(new IPEndPoint(IPAddress.Loopback, 0))
                .Build();

            var factory = new EndpointListenerFactory();

            // act
            using var listener = factory.CreateListener(endpointDefinition);

            // assert
            Assert.NotNull(listener);
        }

        [Fact]
        public void CanCreateListenerForDefaultEndpoint()
        {
            // arrange
            var endpointDefinition = new EndpointDefinitionBuilder()
                .Port(0)
                .Build();

            var factory = new EndpointListenerFactory();

            // act
            using var listener = factory.CreateListener(endpointDefinition);

            // assert
            Assert.NotNull(listener);
        }

        [Fact]
        public void ResolvesIPv6AnyToIPv4AnyWhenIPv6IsNotSupported()
        {
            // arrange
            var endpoint = new IPEndPoint(IPAddress.IPv6Any, 25);

            // act
            var resolved = EndpointListenerFactory.ResolveListenEndpoint(endpoint, osSupportsIPv6: false);

            // assert
            Assert.Equal(AddressFamily.InterNetwork, resolved.AddressFamily);
            Assert.Equal(IPAddress.Any, resolved.Address);
            Assert.Equal(25, resolved.Port);
        }

        [Fact]
        public void ResolvesIPv6LoopbackToIPv4LoopbackWhenIPv6IsNotSupported()
        {
            // arrange
            var endpoint = new IPEndPoint(IPAddress.IPv6Loopback, 25);

            // act
            var resolved = EndpointListenerFactory.ResolveListenEndpoint(endpoint, osSupportsIPv6: false);

            // assert
            Assert.Equal(IPAddress.Loopback, resolved.Address);
            Assert.Equal(25, resolved.Port);
        }

        [Fact]
        public void KeepsIPv6EndpointWhenIPv6IsSupported()
        {
            // arrange
            var endpoint = new IPEndPoint(IPAddress.IPv6Any, 25);

            // act
            var resolved = EndpointListenerFactory.ResolveListenEndpoint(endpoint, osSupportsIPv6: true);

            // assert
            Assert.Same(endpoint, resolved);
        }

        [Fact]
        public void KeepsIPv4EndpointUntouched()
        {
            // arrange
            var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 25);

            // act
            var resolved = EndpointListenerFactory.ResolveListenEndpoint(endpoint, osSupportsIPv6: false);

            // assert
            Assert.Same(endpoint, resolved);
        }
    }
}

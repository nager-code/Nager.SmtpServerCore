using Nager.SmtpServerCore.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nager.SmtpServerCore.Protocol
{
    /// <summary>
    /// Start Tls Command
    /// </summary>
    public sealed class StartTlsCommand : SmtpCommand
    {
        /// <summary>
        /// Smtp Start Tls Command
        /// </summary>
        public const string Command = "STARTTLS";

        /// <summary>
        /// Constructor.
        /// </summary>
        public StartTlsCommand() : base(Command) { }

        /// <summary>
        /// Execute the command.
        /// </summary>
        /// <param name="context">The execution context to operate on.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Returns true if the command executed successfully such that the transition to the next state should occur, false 
        /// if the current state is to be maintained.</returns>
        internal override async Task<bool> ExecuteAsync(SmtpSessionContext context, CancellationToken cancellationToken)
        {
            await context.Pipe.Output.WriteReplyAsync(SmtpResponse.ServiceReady, cancellationToken).ConfigureAwait(false);

            var certificate = context.EndpointDefinition.CertificateFactory.GetServerCertificate(context);

            var protocols = context.EndpointDefinition.SupportedSslProtocols;

            await context.Pipe.UpgradeAsync(certificate, protocols, cancellationToken).ConfigureAwait(false);

            // RFC 3207 4.2: anything that was obtained from the client before the TLS negotiation must be discarded,
            // the client has to start over with EHLO
            context.Authentication = AuthenticationContext.Unauthenticated;
            context.Transaction.Reset();

            return true;
        }
    }
}

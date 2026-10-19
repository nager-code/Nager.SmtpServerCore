using Nager.SmtpServerCore.Authentication;
using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using System;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nager.SmtpServerCore.Protocol
{
    /// <summary>
    /// Auth Command
    /// </summary>
    public sealed class AuthCommand : SmtpCommand
    {
        /// <summary>
        /// Smtp Auth Command
        /// </summary>
        public const string Command = "AUTH";

        const int MaxStackAllocLength = 1024;

        string _user;
        string _password;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="method">The authentication method.</param>
        /// <param name="parameter">The authentication parameter.</param>
        public AuthCommand(AuthenticationMethod method, string parameter) : base(Command)
        {
            Method = method;
            Parameter = parameter;
        }

        /// <summary>
        /// Execute the command.
        /// </summary>
        /// <param name="context">The execution context to operate on.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Returns true if the command executed successfully such that the transition to the next state should occur, false 
        /// if the current state is to be maintained.</returns>
        internal override async Task<bool> ExecuteAsync(SmtpSessionContext context, CancellationToken cancellationToken)
        {
            context.Authentication = AuthenticationContext.Unauthenticated;

            if (context.ServiceProvider.GetService(typeof(IUserAuthenticatorFactory)) == null)
            {
                await context.Pipe.Output.WriteReplyAsync(SmtpResponse.CommandNotImplemented, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var credentialsReceived = Method switch
            {
                AuthenticationMethod.Plain => await TryPlainAsync(context, cancellationToken).ConfigureAwait(false),
                AuthenticationMethod.Login => await TryLoginAsync(context, cancellationToken).ConfigureAwait(false),
                _ => false
            };

            // malformed credentials count as a failed attempt as well, otherwise they could be sent without any limit
            if (credentialsReceived == false || await AuthenticateAsync(context, cancellationToken).ConfigureAwait(false) == false)
            {
                var remaining = context.ServerOptions.MaxAuthenticationAttempts - ++context.AuthenticationAttempts;
                var response = new SmtpResponse(SmtpReplyCode.AuthenticationFailed, $"authentication failed, {remaining} attempt(s) remaining.");

                await context.Pipe.Output.WriteReplyAsync(response, cancellationToken).ConfigureAwait(false);

                if (remaining <= 0)
                {
                    throw new SmtpResponseException(SmtpResponse.ServiceClosingTransmissionChannel, true);
                }

                return false;
            }

            await context.Pipe.Output.WriteReplyAsync(SmtpResponse.AuthenticationSuccessful, cancellationToken).ConfigureAwait(false);

            context.Authentication = new AuthenticationContext(_user);
            context.RaiseSessionAuthenticated();

            return true;
        }

        /// <summary>
        /// Authenticate the user name and password combination that was received.
        /// </summary>
        /// <param name="context">The execution context to operate on.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>true if the user was authenticated, false if not.</returns>
        async Task<bool> AuthenticateAsync(ISessionContext context, CancellationToken cancellationToken)
        {
            var userAuthenticator = context.ServiceProvider.GetService<IUserAuthenticatorFactory, IUserAuthenticator>(context, UserAuthenticator.Default);

            using var container = new DisposableContainer<IUserAuthenticator>(userAuthenticator);

            return await container.Instance.AuthenticateAsync(context, _user, _password, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Attempt a PLAIN login sequence.
        /// </summary>
        /// <param name="context">The execution context to operate on.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>true if the PLAIN login sequence worked, false if not.</returns>
        async Task<bool> TryPlainAsync(ISessionContext context, CancellationToken cancellationToken)
        {
            var authentication = Parameter;

            if (string.IsNullOrWhiteSpace(authentication))
            {
                await context.Pipe.Output.WriteReplyAsync(new SmtpResponse(SmtpReplyCode.ContinueWithAuth, " "), cancellationToken).ConfigureAwait(false);

                authentication = await context.Pipe.Input.ReadLineAsync(Encoding.ASCII, context.ServerOptions.MaxMessageSizeOptions, context.ServerOptions.MaxLineLength, cancellationToken).ConfigureAwait(false);
            }

            return TryExtractFromBase64(authentication);
        }

        /// <summary>
        /// Attempt to extract the user name and password combination from a single line base64 encoded string.
        /// </summary>
        /// <param name="base64">The base64 encoded string to extract the user name and password from.</param>
        /// <returns>true if the user name and password were extracted from the base64 encoded string, false if not.</returns>
        /// <remarks><![CDATA[[authzid] NUL authcid NUL passwd (RFC 4616)]]></remarks>
        bool TryExtractFromBase64(string base64)
        {
            var parts = DecodeBase64(base64).Split('\0');

            if (parts.Length != 3)
            {
                return false;
            }

            // acting on behalf of another identity is not supported
            if (parts[0].Length > 0 && parts[0] != parts[1])
            {
                return false;
            }

            _user = parts[1];
            _password = parts[2];

            return true;
        }

        /// <summary>
        /// Attempt a LOGIN login sequence.
        /// </summary>
        /// <param name="context">The execution context to operate on.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>true if the LOGIN login sequence worked, false if not.</returns>
        async Task<bool> TryLoginAsync(ISessionContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Parameter) == false)
            {
                _user = DecodeBase64(Parameter);
            }
            else
            {
                //Username = VXNlcm5hbWU6 (base64)
                await context.Pipe.Output.WriteReplyAsync(new SmtpResponse(SmtpReplyCode.ContinueWithAuth, "VXNlcm5hbWU6"), cancellationToken).ConfigureAwait(false);

                _user = await ReadBase64EncodedLineAsync(context.Pipe.Input, context.ServerOptions, cancellationToken).ConfigureAwait(false);
            }

            //Password = UGFzc3dvcmQ6 (base64)
            await context.Pipe.Output.WriteReplyAsync(new SmtpResponse(SmtpReplyCode.ContinueWithAuth, "UGFzc3dvcmQ6"), cancellationToken).ConfigureAwait(false);

            _password = await ReadBase64EncodedLineAsync(context.Pipe.Input, context.ServerOptions, cancellationToken).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Read a Base64 encoded line.
        /// </summary>
        /// <param name="reader">The pipe to read from.</param>
        /// <param name="serverOptions">The server options that define the size limits.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The decoded Base64 string.</returns>
        static async Task<string> ReadBase64EncodedLineAsync(PipeReader reader, ISmtpServerOptions serverOptions, CancellationToken cancellationToken)
        {
            var text = await reader.ReadLineAsync(serverOptions.MaxMessageSizeOptions, serverOptions.MaxLineLength, cancellationToken).ConfigureAwait(false);

            return text == null ? string.Empty : DecodeBase64(text);
        }

        static string DecodeBase64(string parameter)
        {
            if (string.IsNullOrEmpty(parameter)) return string.Empty;

            // Allocate memory on the stack for performance, the length is controlled by the client so larger inputs go to the heap
            // Max size required is approximately 3/4 the length of the Base64 string
            Span<byte> buffer = parameter.Length <= MaxStackAllocLength ? stackalloc byte[parameter.Length] : new byte[parameter.Length];

            if (Convert.TryFromBase64String(parameter, buffer, out int bytesWritten))
            {
                // Success: Only decode the portion that was actually written
                return Encoding.UTF8.GetString(buffer.Slice(0, bytesWritten));
            }

            // Handle the failure gracefully without an exception
            return string.Empty;
        }

        /// <summary>
        /// The authentication method.
        /// </summary>
        public AuthenticationMethod Method { get; }

        /// <summary>
        /// The authentication parameter.
        /// </summary>
        public string Parameter { get; }
    }
}

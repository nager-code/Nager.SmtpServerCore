# Change Log

## Unreleased

Entries for the open pull requests, remove the ones that are not merged.

- Changed: Messages are limited to 25 MB by default (`MaxMessageSizeHandling.Strict`). `MaxMessageSize(length)` now defaults to `Strict`, a length of `0` disables the limit. (#13)
- Changed: `SupportedSslProtocols` defaults to `SslProtocols.None`, the operating system chooses the protocol (enables TLS 1.3). Use `SupportedSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)` to restrict it explicitly. (#8)
- Changed: After `STARTTLS` the session state (EHLO, authentication, transaction) is discarded per RFC 3207, the client has to send EHLO again. (#17)
- Fixed: Listener creation for IPv4 endpoints and for hosts without IPv6 support. (#1)
- Fixed: `AUTH` replied `502` when only an `IUserAuthenticator` without a factory was registered in a custom `IServiceProvider`, although EHLO advertised AUTH. (#4)
- Fixed: `554` reply without text, text-less replies no longer end with a trailing space. (#9)
- Fixed: Quoted local parts are unquoted per RFC 5321 instead of `Regex.Unescape`, control characters can no longer be injected into the mailbox. (#14)
- Fixed: Unhandled exceptions for ESMTP parameters that span multiple buffer segments and for duplicate parameters (now `501`). (#15)
- Fixed: `AUTH PLAIN` is parsed per RFC 4616, malformed credentials get a single `535` reply and count as a failed attempt. (#16)
- Fixed: An empty message (`.<CRLF>` directly after `DATA`) no longer hangs the session, the dot-stuffing of the first line is removed. (#18)
- Improved: Tests for the state machine and the retry handling. (#19)

## v11.1.0

- Added: Configuration option to define the maximum allowed message size.
- Added: Support for custom SMTP greeting messages.
- Improved: Optimized protection against excessively long text segments to enhance stability and performance.

```cs
var options = new SmtpServerOptionsBuilder()
	.ServerName("My mail server")
	.MaxMessageSize(5242880, MaxMessageSizeHandling.Strict) //5MB
	.CommandWaitTimeout(TimeSpan.FromSeconds(60))
```

## v11.0.0

- Added: SslProtocol support to SecurableDuplexPipe.
- Added: GitHub workflow for automated build and unit tests.
- Added: Summary information to classes for improved code documentation.
- Added: Session timeout to automatically close connections that remain open for too long.
- Fixed: Missing SessionCreated event in failure scenarios.

## v10.0.1

- Fixed a bug that could cause a failure to recognize commands when using a large number of recipients.

## v10.0.0

- Removed MailboxFilterResult in favor of bool result. Implementations can throw SmtpResponseException for more control.
- Handled servers that send the QUIT command and immediately close the connection.
- Added an ICertificateFactory on the Endpoint that allows a new certificate to be created when required without having to restart the server.

## v9.1.0

- Added a ResponseException event handler to the ISessionContext to enable external logging of exceptions.

## v9.0.3

- Fixed a bug with the session not closing when the cancellation token was cancelled.

## v9.0.2

- Fixed a performance issue whereby the server would block incoming connections whilst another connection was upgrading to SSL.

## v9.0.0

- Breaking API change by removing the Certificate from the server options and adding it to the endpoint.

## v8

- Version 8 contains substantial refactoring to take advantage of [System.IO.Pipelines](https://www.nuget.org/packages/System.IO.Pipelines/) in an effort to improve throughput performance and reduce memory allocations. 
- In addition to this there are also changes to make service resolution easier via Dependency Injection through utilizing the [IServiceProvider](https://docs.microsoft.com/en-us/dotnet/api/system.iserviceprovider) interface. 

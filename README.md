# What is Nager.SmtpServerCore?

**Nager.SmtpServerCore** is a lightweight yet powerful SMTP server implementation in C#.  
Built entirely in .NET, it leverages the Task Parallel Library (TPL) for maximum performance.

## Origin & Development
**Nager.SmtpServerCore** is based on the original [SmtpServer library by cosullivan](https://github.com/cosullivan/SmtpServer). This repository represents an independent continuation and further development of the project, focusing on modern .NET standards and long-term maintainability.

## 🆕 What's New?

Check the [Changelog](https://github.com/nager/Nager.SmtpServerCore/blob/main/CHANGELOG.md)

## ⚡ Supported ESMTP Extensions

Nager.SmtpServerCore currently supports the following extensions:

- STARTTLS
- SIZE
- PIPELINING
- 8BITMIME
- SMTPUTF8
- AUTH PLAIN LOGIN

## Installation

The package is available on [NuGet](https://www.nuget.org/packages/Nager.SmtpServerCore)
```powershell
PM> install-package Nager.SmtpServerCore
```

## 🚀 Getting Started

Starting a basic SMTP server requires only a few lines of code:

```cs
using Nager.SmtpServerCore;
using Nager.SmtpServerCore.ComponentModel;

var options = new SmtpServerOptionsBuilder()
    .ServerName("localhost")
    .Port(25, 587)
    .Build();

var smtpServer = new SmtpServer(options, ServiceProvider.Default);
await smtpServer.StartAsync(CancellationToken.None);
```

### Server options

| Option | Default | Description |
|---|---|---|
| `ServerName(string)` | | The name announced in the greeting and the EHLO reply. |
| `Port(params int[])` / `Endpoint(...)` | | The endpoints to listen on, see `EndpointDefinitionBuilder` for TLS, authentication and session timeout settings. |
| `MaxMessageSize(int, MaxMessageSizeHandling)` | unlimited | Announced via the SIZE extension; with `MaxMessageSizeHandling.Strict` the session is closed when more data is received. |
| `MaxConcurrentSessions(int)` | 100 | Maximum number of sessions handled at the same time, further connections wait for a free slot. |
| `MaxRcptToCount(int)` | 10 | Maximum number of recipients per message. |
| `MaxRetryCount(int)` | 5 | Number of rejected commands before the session is closed. |
| `MaxAuthenticationAttempts(int)` | 3 | Number of failed AUTH attempts before the session is closed. |
| `CommandWaitTimeout(TimeSpan)` | 5 minutes | How long the server waits for the next command. |
| `CustomSmtpGreeting(Func<ISessionContext, string>)` | | Replaces the default `220` greeting line. |

### What hooks are provided?

There are three hooks that can be implemented; IMessageStore, IMailboxFilter, and IUserAuthenticator.

```cs
var options = new SmtpServerOptionsBuilder()
    .ServerName("localhost")
    .Endpoint(builder =>
        builder
            .Port(9025, true)
            .AllowUnsecureAuthentication(false)
            .Certificate(CreateCertificate()))
    .Build();

var serviceProvider = new ServiceProvider();
serviceProvider.Add(new SampleMessageStore());
serviceProvider.Add(new SampleMailboxFilter());
serviceProvider.Add(new SampleUserAuthenticator());

var smtpServer = new SmtpServer(options, serviceProvider);
await smtpServer.StartAsync(CancellationToken.None);

// load the server certificate (PFX) that is used for implicit TLS and STARTTLS
static X509Certificate2 CreateCertificate()
{
    var certificate = File.ReadAllBytes(@"Certificate.pfx");

    return new X509Certificate2(certificate, "P@ssw0rd");
}
```

```cs
public class SampleMessageStore : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();

        var position = buffer.GetPosition(0);
        while (buffer.TryGet(ref position, out var memory))
        {
            await stream.WriteAsync(memory, cancellationToken);
        }

        stream.Position = 0;

        var message = await MimeKit.MimeMessage.LoadAsync(stream, cancellationToken);
        Console.WriteLine(message.TextBody);

        return SmtpResponse.Ok;
    }
}
```

```cs
public class SampleMailboxFilter : IMailboxFilter, IMailboxFilterFactory
{
    public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox @from, int size, CancellationToken cancellationToken)
    {
        if (string.Equals(@from.Host, "test.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        // returning false replies with "550 mailbox unavailable",
        // throw a SmtpResponseException to reply with a specific code and text
        throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.RelayDenied, "relaying denied"));
    }

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox @from, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public IMailboxFilter CreateInstance(ISessionContext context)
    {
        return new SampleMailboxFilter();
    }
}
```

```cs
public class SampleUserAuthenticator : IUserAuthenticator, IUserAuthenticatorFactory
{
    public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken token)
    {
        Console.WriteLine("User={0} Password={1}", user, password);

        return Task.FromResult(user.Length > 4);
    }

    public IUserAuthenticator CreateInstance(ISessionContext context)
    {
        return new SampleUserAuthenticator();
    }
}
```

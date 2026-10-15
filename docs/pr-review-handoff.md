# Review of the open AI-generated PRs (#1–#12), handoff notes

Status as of 2026-10-15. All twelve PRs target `main` at `cde606a`, are open, and have green CI (`build` check).
None of the follow-up changes below have been applied yet. This file lists, per PR, what is right, what is wrong
and what still needs to be done, so the next agent can continue without repeating the review.

Each PR branch was reviewed against `main` with PR #1 merged in (see "Environment"), and for every bug fix
the production change was reverted to confirm that the new tests fail without it.

## Environment notes (cloud container)

- No .NET SDK is preinstalled and `dot.net` / `builds.dotnet.microsoft.com` are blocked by the proxy.
  Install via apt instead: `apt-get update && apt-get install -y dotnet-sdk-10.0`.
- The container is **IPv4-only**. On `main` 21 tests fail with `Address family not supported by protocol`
  because `Port()` binds `IPv6Any`. Merge `claude/wonderful-shannon-abiklv-ipv4-endpoints` (PR #1) into any
  local test branch, otherwise those failures mask everything else.
  Baseline `main`: 96 passed / 21 failed / 1 skipped. `main` + PR #1: 123 passed / 0 failed / 1 skipped.
- All tests use the fixed port **9025**. Parallel test runs on the same host (e.g. several worktrees) fail
  with `Address already in use`. Run each suite in its own network namespace:
  `unshare -rn sh -c 'python3 -I loup.py && dotnet test src/Nager.SmtpServerCore.Tests'`, where `loup.py`
  brings the loopback interface up:

  ```python
  import socket, fcntl, struct
  s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
  ifr = struct.pack('16sH14s', b'lo', 0, b'\0' * 14)
  flags = struct.unpack('16sH14s', fcntl.ioctl(s, 0x8913, ifr))[1] | 0x1 | 0x40   # SIOCGIFFLAGS, IFF_UP|IFF_RUNNING
  fcntl.ioctl(s, 0x8914, struct.pack('16sH14s', b'lo', flags, b'\0' * 14))         # SIOCSIFFLAGS
  ```

## Overview

| PR | Branch (`claude/wonderful-shannon-abiklv-…`) | Verdict | Main open point |
|---|---|---|---|
| #1 | `ipv4-endpoints` | merge | PR text overstates the Docker claim |
| #2 | `line-length-limit` | fix first | CR/LF split boundary bug, default below RFC 4954, breaking interface change |
| #3 | `session-cancel-leak` | small fixes | `SessionCancelled` can fire without `SessionCreated`; wrong "faulted" claim |
| #4 | `auth-service-resolution` | merge | optional: resolve the authenticator only once |
| #5 | `rcpt-limit` | merge | CHANGELOG note: `MaxRcptToCount(0)` now rejects every recipient |
| #6 | `data-command-exceptions` | small fixes | over-claiming test name, CHANGELOG note about events |
| #7 | `mailbox-parsing` | rework or close | RFC justification is false, `AsAddress()` returns `"postmaster@"` |
| #8 | `tls-defaults` | fix first | platform-dependent TLS 1.3 test, undocumented TLS 1.0/1.1 implication, wrong revocation rationale |
| #9 | `reply-messages` | trim | keep the three text fixes, drop the churn |
| #10 | `docs` | fix first | README sample does not compile (CS0121), CHANGELOG gaps, BOM noise |
| #11 | `test-project-hygiene` | fix first | vulnerable package still in Benchmarks/SampleApp, useless MailKit bump |
| #12 | `editorconfig` | close, or merge last and reduced | its CI check breaks 7 of the other PRs |

Pairwise merge conflicts: only #12 conflicts (with #3, #4, #8; whitespace only). #1–#11 merge cleanly with each other.
Recommended order: #1 first (it unblocks the test suite on IPv4-only hosts), #12 last or not at all.

## Per PR

### #1 Fix listener creation for IPv4 endpoints and IPv4-only hosts → merge

- Both bugs are real. Setting `DualMode = true` on an IPv4 socket throws `NotSupportedException`, and the default `IPv6Any` endpoint fails on hosts without IPv6.
- The fix only sets `DualMode` for IPv6 and only falls back when `Socket.OSSupportsIPv6 == false`, so IPv6 and dual-stack hosts behave exactly as before.
- The new tests fail without the fix.
- Nit: the `IsIPv4MappedToIPv6` branch in `Net/EndpointListenerFactory.cs` (~line 76) is untested. Add a test or remove the branch.
- PR text: "Docker has IPv6 disabled by default" is overstated. `AF_INET6` sockets work in Docker. The error only occurs when the kernel has no IPv6 at all (`ipv6.disable=1`, gVisor, Firecracker-style sandboxes).

### #2 Limit the length of command lines → fix first

- The memory problem is real. The limit is wired to command lines and AUTH continuation lines, while DATA passes `0`. On overflow the client gets `500 line too long` and the session is closed.
- **Bug:** `IO/PipeReaderExtensions.cs` (~line 65): `read.Buffer.Length > maxLength` counts a trailing `\r` whose `\n` has not arrived yet. A line of exactly `maxLength` bytes is rejected when the TCP segments split between CR and LF. Reproduction: write 100×`A` + `\r`, then `\n`, with limit 100 → throws `LineTooLong`. Fix: compare against `maxLength + sequence.Length - 1` (or `maxLength + 2`), and add this case as a test.
- The default of 8192 is below RFC 4954's 12288 octets for AUTH lines. Raise it to at least 12288. RFC 4954 also defines `500 5.5.6 Authentication Exchange line is too long`.
- `ISmtpServerOptions.MaxLineLength` is a breaking change for external implementers. Use a default interface member (`int MaxLineLength => SmtpServerOptionsBuilder.DefaultMaxLineLength;`; both TFMs support it), or at least mention it in the CHANGELOG.
- Tests:
  - Only 2 of the 9 new tests fail without the limit, and no test covers the session level (reply text, connection close, wiring through `SmtpSession`/`AuthCommand`). Add one in-memory session test.
  - Drop `ReadLineWithoutLimitBuffersEverythingTheClientSends`: it buffers 64 MB and passes either way.
- PR title/text: DATA is still unbounded unless `MaxMessageSize(..., Strict)` is set, and `SessionTimeout` already bounds the attack in time. Say so.
- Document that `0` or a negative value means "no limit" for custom `ISmtpServerOptions` implementations.

### #3 Clean up sessions cancelled while waiting for a slot → small fixes

- The bug is real: `WaitAsync` was outside the `try`, so the pipe and TCP connection leaked. The concurrency is correct, with no double or missing `Release`.
- `SmtpSessionManager.cs` (~lines 54/57/69): `SessionCancelled` is now raised for sessions that never got `SessionCreated`. That breaks handlers relying on per-session state, e.g. `examples/SampleApp/Examples/SessionTracingExample.cs`. Either raise `SessionCreated` before the wait, or skip the event and only clean up when no slot was acquired.
- Commit/PR text says the task "ended in a faulted state". It is **Canceled**, not Faulted. Correct the text.
- Test nits: `Task.Delay(250)` is unnecessary, and the `SmtpServer` instances in the tests are not disposed.
- Pre-existing, not addressed: `SmtpServer.Dispose` disposes the semaphore while sessions are still finishing, so `Release()` throws `ObjectDisposedException`.

### #4 AUTH honours an IUserAuthenticator registered without a factory → merge

- The EHLO/AUTH mismatch is real: EHLO advertised AUTH via factory **or** service, while AUTH only checked the factory and replied 502. It only shows with a custom `IServiceProvider` such as Microsoft DI (`examples/WorkerService`).
- The fix is correct. Without it, 2 of the 4 new tests fail; the other 2 are regression guards.
- Optional: AUTH resolves the authenticator twice. With MS DI and a transient `IDisposable` authenticator, the instance created only for the check is never disposed. Resolve once with a `null` default and reply 502 when it is null.
- The test count in the PR text (121) is outdated.

### #5 Off-by-one in MaxRcptToCount → merge

- `MaxRcptToCount = N` now accepts exactly N recipients. Recipient N+1 gets `452 Too many recipients`, as RFC 5321 §4.5.3.1.10 requires. Without the fix, 3 of the 4 tests fail.
- CHANGELOG: `MaxRcptToCount(0)` now rejects every recipient (before, it allowed one).
- Out of scope: RFC 5321 §4.5.3.1.8 says a server MUST accept at least 100 recipients, so the default of 10 violates it.

### #6 SmtpResponseException from the DATA phase reaches the session → small fixes

- On `main` the client gets **no reply** after `.` when the message store throws `SmtpResponseException`. The next reply it sees belongs to the following command, which desynchronises pipelining clients. The fix (rethrow) produces exactly one reply, with no double reply. Without it, 3 of the 5 tests fail.
- Rename `MaxMessageSizeExceededInStrictModeClosesTheSessionWithAReply` (`DataCommandExceptionTests.cs` ~line 101): it only checks that the exception propagates. Alternatively, assert on a session transcript.
- CHANGELOG: on DATA failures `CommandExecuted` is no longer raised and `ResponseException` is.
- Optional: `catch (Exception ex) when (ex is not SmtpResponseException)` instead of catch and rethrow.

### #7 Mailbox(string) without a host part → rework or close

- The PR text claims this matches what the parser produces for `<Postmaster>`. **That is false:** `SmtpParser.TryMakeMailbox` requires an `@`, so `RCPT TO:<postmaster>` is still rejected with 502. The library never calls `Mailbox(string)`; the parser uses `Mailbox(user, host)`.
- `new Mailbox("postmaster").AsAddress()` returns `"postmaster@"`.
- A quoted local part keeps its quotes, unlike the parser. The existing `Replace(" ", "")` still mangles `"Fred Bloggs"@x`.
- Suggestion: keep the null check and `LastIndexOf('@')`, and throw a clear `ArgumentException` when there is no `@`. Real `<Postmaster>` support belongs in the parser, in its own PR. Correct the description.

### #8 OS TLS defaults and reader options after STARTTLS → fix first

- `SslProtocols.None` (OS default) enables TLS 1.3, where `main` hard-coded `Tls12`. That is correct on .NET Core 3.0+ for both TFMs, and netstandard2.1 has no `Tls13` member.
- **Undocumented behaviour change:** on Windows 10 / Server 2016–2019 SChannel enables TLS 1.0/1.1 for servers by default, so they become accepted again. Linux/OpenSSL 3 is unaffected. Add a CHANGELOG note, or use `Tls12 | Tls13` under `#if NET`.
- `NegotiatesTls13ByDefault` (`SmtpServerTests.cs` ~lines 394–413) fails where the server cannot do TLS 1.3 (Windows 10 / Server 2019, probably macOS). Skip it there.
- The rationale for removing `CertificateRevocationCheckMode = Online` is wrong: without `ClientCertificateRequired` the client never sends a certificate. The removal is harmless, but describe it honestly or drop it.
- "Keep reader options after STARTTLS" is a real fix but affects performance only. `cde606a` set the options only in the constructor, and `UpgradeAsync` recreated the reader without them.

### #9 Client-visible reply texts and the empty "554 " → trim

- Keep:
  - "has be cancelled" → "has been cancelled"
  - "maximium" → "maximum"
  - `TransactionFailed` with the text "transaction failed" (RFC 5321 §4.2 requires reply text)
  - the update of `WillSessionTimeoutDuringMailDataTransmission`
- Drop:
  - the `WriteReplyAsync` change in `IO/PipeWriterExtensions.cs`. It is unnecessary once 554 has text, and it removes the trailing space of a text-less 334.
  - the "occurr" XML-doc fixes in 11 files (churn)
  - `CancelledSessionReplyIsSpelledCorrectly` (a 50-line fake pipe with a 500 ms timing dependency, duplicating the existing test)

### #10 README samples and CHANGELOG → fix first

- `README` (~lines 78–79): `serviceProvider.Add(new SampleMailboxFilter())` and `Add(new SampleUserAuthenticator())` fail to compile with **CS0121** (ambiguous `Add` overloads, because the samples implement both the interface and the factory). Cast, e.g. `Add((IMailboxFilterFactory)new SampleMailboxFilter())`, or drop the factory interfaces from the samples. The commit message claims the samples compile.
- README ~line 89: `new X509Certificate2(bytes, pwd)` gives SYSLIB0057 on net10. Use `X509CertificateLoader.LoadPkcs12FromFile`.
- README ~line 128: 551 `RelayDenied` in `CanAcceptFromAsync` (MAIL FROM) is odd. Use 550 or 553.
- Options table:
  - Add `SessionTimeout` (2 min, bounds the whole session) and `SupportedSslProtocols`.
  - `MaxRcptToCount` = 10 is only accurate after #5.
- CHANGELOG:
  - The "16 KB read buffer" only holds for TLS sessions after #8.
  - Missing: PROXY support removed (676baa9), net10.0 TFM added, `Port()` binds `IPv6Any` + DualMode (162b873), NuGet package id changed.
- Remove the UTF-8 BOM the PR added to README.md and CHANGELOG.md.

### #11 Test project hygiene → fix first

- The vulnerability is real: `System.Security.Cryptography.Pkcs` 10.0.0 (GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7 moderate). It is fixed only in the Tests project; **Benchmarks and SampleApp are still affected**, so NU1902/NU1903 warnings remain in CI. Pin it everywhere, e.g. via `Directory.Build.props` / central package management.
- The MailKit 4.17.0 → 4.18.1 bump does not help (MimeKit 4.18.1 still depends on Pkcs 10.0.0). Drop it, or bump everywhere.
- Warnings in the test project are gone (14 → 8 in total; the rest are in examples/SampleApp and Benchmarks).
- Re-enabled timeout test: stable on Linux (12/12 isolated, 6/6 under CPU load), adds ~4.3 s. Risk (unverified): on Windows the NOOP write after the server's close triggers a RST that discards the buffered 221, which gives `IOException`. Better: read the proactive `221 Timeout while waiting for input.` with `RawSmtpClient` without writing, and assert the text. The comment "server answers the next command with 221" is wrong: the reply is sent proactively (`SmtpSession.cs` ~line 144).

### #12 .editorconfig + dotnet format + CI check → close, or merge last and reduced

- The reformat is semantically inert: trailing whitespace across 30 files, one stray `;;`, BOMs on 5 files.
- `end_of_line = lf` does not match a Windows checkout with `core.autocrlf=true`; every line gets flagged.
- `charset = utf-8-bom` is enforced as an error by the CI check. After merging the 7 cleanly merging PRs, the check **fails (CHARSET)** on new test files without a BOM:
  - from those merges: DataCommandExceptionTests, EndpointListenerFactoryTests, LineLengthLimitTests, MailboxTests, RcptCommandLimitTests
  - also affected: SmtpSessionManagerTests (#3), AuthCommandServiceResolutionTests (#4)
- Conflicts with #3, #4 and #8.
- If wanted at all: merge last, drop `end_of_line` (or add `.gitattributes`), make the BOM rule non-blocking, and reformat once after the others have landed.

## Pre-existing problems found during the review (not covered by any PR; candidates for issues)

1. **Timeout/cancellation during DATA** replies `554`, then `221`. 554 is permanent, so the sender bounces the mail instead of retrying. The cause is `catch (Exception)` in `Protocol/DataCommand.cs` swallowing `OperationCanceledException`. It should be `421` (timeout) or `451` (transient store failure).
2. After a failed DATA (550/554) the transaction and state are not reset, so a following `MAIL FROM` gets 501 until `RSET` (RFC 5321 §4.1.1.4).
3. The strict `MaxMessageSize` check (`IO/PipeReaderExtensions.cs` ~line 40) measures the whole read buffer. A pipelined burst larger than the limit gets `552` even on `EHLO`.
4. The test suite uses the fixed port 9025. Use port 0 or per-test ports to allow parallel runs.
5. `MaxRcptToCount` defaults to 10, but RFC 5321 requires at least 100.
6. `SmtpServer.Dispose` disposes the session semaphore while sessions are still running (see #3).
7. The parser rejects `RCPT TO:<postmaster>` (RFC 5321 §4.5.1 requires it to be accepted).

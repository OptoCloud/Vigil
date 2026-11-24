using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Vigil.Core.Alerting;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class EmailClientTests
{
    private const string Recipient = "owner@private.example";

    /// <summary>
    /// A mail server on loopback that takes one message, as the fake upsd takes one poll. It
    /// answers RCPT with <paramref name="recipientReply"/>, so a test can have it refuse.
    /// </summary>
    private static (int Port, Task<List<string>> Conversation) StartServer(string recipientReply = "250 OK")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        async Task<List<string>> ServeAsync()
        {
            var heard = new List<string>();
            using (listener)
            using (var client = await listener.AcceptTcpClientAsync())
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

                await writer.WriteLineAsync("220 mail.test ESMTP");
                var inData = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    heard.Add(line);
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            await writer.WriteLineAsync("250 queued");
                        }

                        continue;
                    }

                    var verb = line.Split(' ', ':')[0].ToUpperInvariant();
                    if (verb == "DATA")
                    {
                        inData = true;
                    }

                    await writer.WriteLineAsync(verb switch
                    {
                        "EHLO" or "HELO" => "250 mail.test",
                        "RCPT" => recipientReply,
                        "DATA" => "354 go on",
                        "QUIT" => "221 bye",
                        _ => "250 OK",
                    });
                    if (verb == "QUIT")
                    {
                        break;
                    }
                }
            }

            return heard;
        }

        return (port, ServeAsync());
    }

    private static EmailClient Client(int port, string host = "127.0.0.1") => new(new EmailSettings
    {
        Host = host,
        Port = port,
        StartTls = false,
        From = new MailAddress("vigil@private.example"),
        To = [new MailAddress(Recipient), new MailAddress("second@private.example")],
    });

    private static Notification Raised(string message) => new(new NewEvent(Severity.Critical, "zfs-pool:tank", message), IsAllClear: false);

    [Fact]
    public async Task HandsTheServerOneMessageForEveryRecipient()
    {
        var (port, conversation) = StartServer();

        Assert.Null(await Client(port).TryDeliverAsync(Raised("Pool tank is DEGRADED."), TestContext.Current.CancellationToken));

        var heard = await conversation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(heard, line => line.StartsWith("MAIL FROM:<vigil@private.example>", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, heard.Count(line => line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(heard, line => line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase) && line.Contains("vigil CRITICAL: Pool tank is DEGRADED.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSubjectCannotCarryAHeaderAndIsCutToLength()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.Zero);
        using var forged = Client(25).MessageFor(Raised("Pool tank\r\nBcc: someone@else.example is DEGRADED."), now);
        using var endless = Client(25).MessageFor(Raised(new string('x', 400)), now);

        Assert.DoesNotContain('\r', forged.Subject);
        Assert.DoesNotContain('\n', forged.Subject);
        Assert.Equal(EmailClient.MaximumSubjectLength, endless.Subject.Length);
        // The body has all of it, and says what and when.
        Assert.Contains(new string('x', 400), endless.Body, StringComparison.Ordinal);
        Assert.Contains("Kind: zfs-pool:tank", endless.Body, StringComparison.Ordinal);
        Assert.Contains("Sent: 2026-11-24 12:30:00 UTC", endless.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureIsReportedWithoutAnAddressAndNeverThrown()
    {
        // A server that quotes the mailbox back, as real ones do.
        var (refusingPort, refusing) = StartServer($"550 5.1.1 <{Recipient}>: no such user");
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var closedPort = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        string?[] failures =
        [
            await Client(refusingPort).TryDeliverAsync(Raised("m"), TestContext.Current.CancellationToken),
            await Client(closedPort).TryDeliverAsync(Raised("m"), TestContext.Current.CancellationToken),
        ];
        await refusing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(failures, failure =>
        {
            Assert.NotNull(failure);
            Assert.DoesNotContain("private.example", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", failure, StringComparison.Ordinal);
        });
        Assert.Contains("refused", failures[0], StringComparison.Ordinal);
        Assert.Contains("could not be reached", failures[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ItCarriesWarningsAndWorse()
    {
        Assert.Equal((Severity.Warning, "email"), (Client(25).MinimumSeverity, Client(25).Name));
    }
}

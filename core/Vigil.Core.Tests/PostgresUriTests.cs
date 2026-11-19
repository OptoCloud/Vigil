using Npgsql;
using Vigil.Core.Data;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class PostgresUriTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url) =>
        new(PostgresUri.ToConnectionString(url, "vigil-test"));

    [Fact]
    public void ReadsAFullUrl()
    {
        var parsed = Parse("postgres://vigil:secret@192.0.2.10:5432/vigil");

        Assert.Equal("192.0.2.10", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("vigil", parsed.Database);
        Assert.Equal("vigil", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void DefaultsThePortAndAcceptsNoPassword()
    {
        // This is the development default, Limits.DatabaseUrlDefault.
        var parsed = Parse("postgres://vigil@localhost/vigil");

        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("vigil", parsed.Username);
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void AcceptsThePostgresqlScheme()
    {
        Assert.Equal("example", Parse("postgresql://u@example/db").Host);
    }

    [Fact]
    public void DecodesAPercentEncodedPassword()
    {
        // A password containing @ : / must be encoded in a URL. Not decoding it produces an
        // authentication failure that looks like a wrong password rather than a parsing bug.
        var parsed = Parse("postgres://vigil:p%40ss%3Aword%2F1@db:5432/vigil");

        Assert.Equal("p@ss:word/1", parsed.Password);
        Assert.Equal("db", parsed.Host);
    }

    [Fact]
    public void PassesQueryParametersThroughAsNpgsqlKeywords()
    {
        var parsed = Parse("postgres://u:p@h:5432/db?sslmode=require&Timeout=4");

        Assert.Equal(SslMode.Require, parsed.SslMode);
        Assert.Equal(4, parsed.Timeout);
    }

    [Fact]
    public void AcceptsAKeywordConnectionStringUnchanged()
    {
        // So an operator can set Npgsql options directly when a URL cannot express them.
        var parsed = Parse("Host=db;Database=vigil;Username=vigil");

        Assert.Equal("db", parsed.Host);
        Assert.Equal("vigil", parsed.Database);
    }

    [Fact]
    public void AppliesVigilsOwnSettings()
    {
        var parsed = Parse("postgres://vigil@localhost/vigil");

        // No query may tie up a connection indefinitely (docs/SECURITY.md §6).
        Assert.Equal(15, parsed.CommandTimeout);
        // So pg_stat_activity says which process a connection belongs to.
        Assert.Equal("vigil-test", parsed.ApplicationName);
        // No Kerberos probe: it fails noisily on a guest without krb5-libs, and vigil never uses it.
        Assert.Equal(GssEncryptionMode.Disable, parsed.GssEncryptionMode);
    }

    [Theory]
    [InlineData("mysql://u:p@h/db")]
    [InlineData("not a url at all://")]
    public void RefusesSomethingThatIsNotPostgres(string url)
    {
        Assert.Throws<ArgumentException>(() => PostgresUri.ToConnectionString(url, "vigil-test"));
    }

    [Fact]
    public void RefusesAnEmptyUrlRatherThanConnectingSomewhereSurprising()
    {
        Assert.Throws<ArgumentException>(() => PostgresUri.ToConnectionString("  ", "vigil-test"));
    }

    [Theory]
    [InlineData("mysql://vigil:verysecret@h/db")]
    // An unencoded "/" ends the authority early, so the password is not where a redactor looks.
    [InlineData("postgres://vigil:very/secret@h/db")]
    [InlineData("postgres://vigil:very secret@h:notaport/db")]
    // A password may also sit in the query, as an Npgsql keyword.
    [InlineData("mysql://h/db?password=verysecret")]
    [InlineData("postgres://h:notaport/db?Password=verysecret")]
    public void AMalformedUrlsErrorDoesNotLeakThePassword(string url)
    {
        // docs/SECURITY.md §7: a credential never reaches a log line or an exception. The
        // error names the variable and never repeats its value.
        var error = Assert.ThrowsAny<ArgumentException>(() => PostgresUri.ToConnectionString(url, "vigil-test"));

        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }
}

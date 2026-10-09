using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The credential store against the real Windows Credential Manager, under a test-only target name so it can never
/// touch the credential the user actually uses. Each test deletes its entry afterwards.
///
/// The password used here is a literal dummy; the point of these tests is the round trip through the OS, which a
/// fake store could not exercise — and the store's whole purpose is to keep real secrets out of files like this one.
/// </summary>
public class CredentialStoreTests : IDisposable
{
    private const string TestTarget = "EQLWikiEditorAssistant:unit-test-do-not-use";

    private readonly WindowsCredentialStore _store = new(TestTarget);

    public void Dispose() => _store.Delete();

    [Fact]
    public void Read_ReturnsNullWhenNothingIsStored()
    {
        _store.Delete();

        Assert.Null(_store.Read());
    }

    [Fact]
    public void WriteThenRead_RoundTripsTheCredential()
    {
        _store.Write(new BotCredentials("Editor@assistant", "not-a-real-password"));

        BotCredentials? read = _store.Read();

        Assert.NotNull(read);
        Assert.Equal("Editor@assistant", read.UserName);
        Assert.Equal("not-a-real-password", read.Password);
    }

    /// <summary>Bot passwords are 32 characters of mixed case and digits; the blob length is a byte count, not a
    /// character count, and getting that wrong truncates the password to half its length.</summary>
    [Fact]
    public void WriteThenRead_HandlesAFullLengthBotPassword()
    {
        const string generated = "a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6";
        _store.Write(new BotCredentials("Editor@assistant", generated));

        Assert.Equal(generated, _store.Read()!.Password);
    }

    [Fact]
    public void Write_ReplacesAnExistingCredential()
    {
        _store.Write(new BotCredentials("Editor@old", "first"));
        _store.Write(new BotCredentials("Editor@new", "second"));

        BotCredentials read = _store.Read()!;
        Assert.Equal("Editor@new", read.UserName);
        Assert.Equal("second", read.Password);
    }

    [Fact]
    public void Delete_ReportsWhetherThereWasAnythingToDelete()
    {
        _store.Write(new BotCredentials("Editor@assistant", "x"));

        Assert.True(_store.Delete());
        Assert.False(_store.Delete());
    }

    [Fact]
    public void TheTestStoreIsNotTheRealOne() =>
        Assert.NotEqual(WindowsCredentialStore.DefaultTargetName, TestTarget);
}

public class BotCredentialsTests
{
    [Theory]
    [InlineData("Editor@assistant", "Editor")]
    [InlineData("Some User@bot name", "Some User")]
    [InlineData("Editor", "Editor")]
    public void AccountName_StripsTheBotSuffix(string userName, string expected) =>
        Assert.Equal(expected, new BotCredentials(userName, "x").AccountName);

    /// <summary>Records print every property by default, so the generated ToString would put the bot password into
    /// any log line, exception message or debugger watch that touched it.</summary>
    [Fact]
    public void ToString_DoesNotLeakThePassword()
    {
        string text = new BotCredentials("Editor@assistant", "super-secret-value").ToString();

        Assert.DoesNotContain("super-secret-value", text);
        Assert.Contains("redacted", text);
    }
}

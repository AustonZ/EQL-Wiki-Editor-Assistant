namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// A Special:BotPasswords credential pair.
///
/// Bot passwords are the right mechanism here for a concrete reason, not by preference: eqlwiki.com has no OAuth
/// extension installed, and it does have OATHAuth (2FA), which a bot password bypasses. <see cref="UserName"/> is
/// the full <c>User@BotName</c> form the wiki issues, and <see cref="Password"/> is the generated secret — never
/// the account's own password.
///
/// **This type never gets serialized.** It exists only between reading from the OS credential store and posting
/// the login, so the secret has no reason to reach a config file, a log line or a crash dump.
/// </summary>
public sealed record BotCredentials(string UserName, string Password)
{
    /// <summary>The account name without the <c>@BotName</c> suffix — what the wiki attributes the edit to.</summary>
    public string AccountName
    {
        get
        {
            int at = UserName.IndexOf('@');
            return at < 0 ? UserName : UserName[..at];
        }
    }

    public override string ToString() => $"BotCredentials {{ UserName = {UserName}, Password = <redacted> }}";
}

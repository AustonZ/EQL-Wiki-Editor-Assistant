namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>
/// Where the bot password lives. A port because the rule it enforces is a repo constraint, not an implementation
/// detail: credentials go to Windows Credential Manager / DPAPI and never into the repo, a config file or an
/// environment variable. Keeping it behind an interface means a test can exercise the login path without either
/// touching the real credential store or tempting anyone to hardcode a secret.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The stored credential, or null if none has been saved yet.</summary>
    BotCredentials? Read();

    /// <summary>Saves (or replaces) the credential.</summary>
    void Write(BotCredentials credentials);

    /// <summary>Removes the stored credential. Returns false if there was nothing to remove.</summary>
    bool Delete();
}

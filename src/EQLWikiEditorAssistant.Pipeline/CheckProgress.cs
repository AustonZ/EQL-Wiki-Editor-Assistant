namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>The stages of <see cref="ItemCheckPipeline.CheckAsync"/>, in the order they happen.</summary>
public enum CheckStage
{
    /// <summary>Finding every item window in the screenshot.</summary>
    FindingWindows,

    /// <summary>One located window being read and compared with its wiki page.</summary>
    CheckingWindow,
}

/// <summary>Where a check has got to. <see cref="Window"/> and <see cref="Of"/> are 1-based and only set while
/// checking windows.</summary>
public sealed record CheckProgress(CheckStage Stage, int Window = 0, int Of = 0);

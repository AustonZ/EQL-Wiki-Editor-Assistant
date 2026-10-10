using EQLWikiEditorAssistant.Core.Ocr;
using EQLWikiEditorAssistant.TestSupport;
using EQLWikiEditorAssistant.TestSupport.Accuracy;

namespace EQLWikiEditorAssistant.Tests.Accuracy;

/// <summary>
/// The one rule every dev tool reads a screenshot's font by. **Arial unless something says otherwise** (user,
/// 2026-10-10): a flag, the ground truth or the file name's marker. Pure, so it runs without any samples.
/// </summary>
public class SampleFontsTests
{
    private static readonly ExpectedCorpus Truth = new()
    {
        Samples =
        [
            new ExpectedSample { File = "01-arial.png" },
            new ExpectedSample { File = "15a-custom.png", Font = UiFont.EqlWikiEditorAssistant },
        ],
    };

    private static UiFont For(string file, params string[] args) => SampleFonts.For(file, args, Truth);

    [Fact]
    public void AnUnmarkedScreenshotNobodyHasSeenIsArial() =>
        Assert.Equal(UiFont.Arial, For("2026-10-10 12-00-00 - Spit.png"));

    [Fact]
    public void AMarkedScreenshotIsReadInItsMarkedFont() =>
        Assert.Equal(UiFont.EqlWikiEditorAssistant, For("2026-10-10 12-00-00 [font EqlWikiEditorAssistant] - Spit.png"));

    [Fact]
    public void GroundTruthNamesTheFontOfASampleItKnows()
    {
        Assert.Equal(UiFont.Arial, For("01-arial.png"));
        Assert.Equal(UiFont.EqlWikiEditorAssistant, For("15a-custom.png"));
    }

    [Fact]
    public void AFlagOverridesEverything() =>
        Assert.Equal(UiFont.Arial, For("15a-custom.png", "--font", "Arial"));

    /// <summary>A sample whose name and ground truth disagree is a corpus defect; letting either win quietly would read
    /// it by the wrong l/I rule.</summary>
    [Fact]
    public void AMarkerContradictingTheGroundTruthIsRefused()
    {
        var truth = new ExpectedCorpus { Samples = [new ExpectedSample { File = "23-spit [font EqlWikiEditorAssistant].png" }] };

        Assert.Throws<InvalidOperationException>(() =>
            SampleFonts.For("23-spit [font EqlWikiEditorAssistant].png", [], truth));
    }
}

using Microsoft.UI.Xaml.Automation.Peers;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

public sealed partial class AnimatedTextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new TextAutomationPeer(this);

    private sealed class TextAutomationPeer(AnimatedTextBlock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(AnimatedTextBlock);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            if (!string.IsNullOrEmpty(name))
                return name;
            return owner.Document is { Paragraphs.Count: > 0 } document
                ? document.Paragraphs[0].Text
                : owner.Text ?? string.Empty;
        }
    }
}

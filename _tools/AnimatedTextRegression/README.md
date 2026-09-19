# AnimatedTextBlock WinUI regression

Run on Windows with the repository's .NET SDK and Windows App SDK dependencies:

```powershell
pwsh -File _tools/AnimatedTextRegression/Run.ps1
```

Uses a real WinUI application, offscreen window, CanvasControl, DirectWrite layouts,
Draw callbacks and shared animation clock. No mocked layout or dispatcher.
The original implementation failed the first assertion with `DesiredSize.Height = 0`;
the fixed implementation reports 33 DIP for the same 24-point text.

Covers auto height without a placeholder, font changes, direct dependency-property
updates, wrapping, padding, empty text, constrained width, disabled/enabled hover,
scroll progress, transition priority and resumption, exit/reset, untrimmed text,
resize, RTL, unload/reload and clock cleanup. Results are written beside the executable;
the runner fails on a failed assertion, crash or timeout.

Multiline cases cover album/artist CRLF and LF, either/both lines overflowing,
independent travel distances, short-line alignment and baseline stability, empty/trailing
lines, RTL, transition handoff and cleanup. `NoWrap` does not suppress explicit newlines.

The cross-script regression reproduced `祝融` → `All In My Head` changing natural
height from 31 to 32 DIP at 24-point Semibold and canceling the transition. Tests now
verify that resizing preserves transitions and that an explicit `LineHeight` stabilizes
both height and baseline, including Fade/Default/Wipe transitions across line counts.

`LayoutRegressionPage` additionally uses compiled XAML with the page's nested Grid,
`x:Load`, ViewModel bindings and Loaded-time font changes. It reproduced both labels
being absent from the visual tree: the generated `x:Bind` connector wrote its default
deferred `FontSize` of zero before evaluating the source, and WinUI rejected it.
The legacy fixture uses ordinary Binding for FontSize and
checks attachment, measured dimensions, drawing, responsive updates and re-entry.
`layout.txt` beside the executable records the native layout tree. The main project
excludes `_tools` XAML so test pages and generated output are never compiled into the app.

`DocumentLayoutRegressionPage` exercises PlayingDetailPage's new single-canvas binding:
one atomic Document contains a Semibold title and two Normal metadata lines with
different sizes, fixed line heights and opacity. Checks cover all eight effects,
one progress advance per tick, live hover settings, coalesced updates, layout changes
during transitions, independent line scrolling, and unload/re-entry cleanup.

The mixed-script endpoint regression reproduced a 26.49% normalized alpha difference
between the final Default animation frame and the static frame. It now compares native
Win2D images immediately before completion against the static frame for all six glyph
effects, 96/144 DPI, RTL, combining marks, trimmed multiline text and emoji. Artifacts
are saved as `endpoint-*-animation.png` / `endpoint-*-static.png` beside the executable.
This checks rendering consistency, not runtime frame rate or allocation savings.

For an external lvt tree inspection, launch the test executable with `--inspect-layout`.
It keeps the offscreen fixture window alive for 45 seconds.

Pointer presence is injected through reflection, and pointer exit invokes the actual
handler. Physical mouse hit testing and visual quality on PlayingDetailPage at different
DPI settings still require interactive verification.

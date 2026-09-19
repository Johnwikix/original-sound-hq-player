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

Pointer presence is injected through reflection, and pointer exit invokes the actual
handler. Physical mouse hit testing and visual quality on PlayingDetailPage at different
DPI settings still require interactive verification.

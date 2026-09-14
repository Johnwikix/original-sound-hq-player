using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Numerics;
using Windows.Foundation;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Check((int)EasingType.Bounce == 11 && (int)EasingType.FlowWave == 12,
    "Existing curve indices must remain stable.");
Check((int)EaseMode.Continuous == 3 && (int)EaseMode.FlowWave == 4,
    "Existing direction indices must remain stable.");

foreach (var mode in Enum.GetValues<EaseMode>())
{
    var interpolate = EasingHelper.GetInterpolatorByEasingType<double>(EasingType.FlowWave, mode);
    Check(interpolate(0, 1, -1) == 0 && interpolate(0, 1, 2) == 1, "Clamped endpoints");
    double previous = 0;
    for (int i = 0; i <= 10000; i++)
    {
        double t = i / 10000.0;
        double value = interpolate(0, 1, t);
        Check(double.IsFinite(value) && value >= previous - 1e-12 && value <= 1,
            $"Monotonic bounded curve: {mode}, {t}");
        Check(Math.Abs(interpolate(800, -200, t) - (800 - 1000 * value)) < 1e-9,
            "Reverse scrolling must preserve the curve.");
        previous = value;
    }
}

// The direction remains composable with every existing curve, including overshoot curves.
foreach (var type in Enum.GetValues<EasingType>())
{
    var f = EasingHelper.GetInterpolatorByEasingType<double>(type, EaseMode.FlowWave);
    for (int i = 0; i <= 1000; i++)
        Check(double.IsFinite(f(0, 1, i / 1000.0)), $"Finite composition: {type}");
}

var apple = EasingHelper.GetInterpolatorByEasingType<double>(EasingType.FlowWave, EaseMode.FlowWave);
const double h = 1e-6;
Check(apple(0, 1, h) / h < 0.001, "Resting spring must start softly.");

// A real line change: each lyric row keeps its own position and delayed target.
var rows = Enumerable.Range(0, 6).Select(_ => new LyricScrollMotion()).ToArray();
for (int i = 0; i < rows.Length; i++)
    rows[i].Start(-120, 0.7, LyricScrollMotion.StaggerDelay(i, 0, 0.7), true, apple);
foreach (var row in rows) row.Update(0.025);
Check(rows[0].Value < 0 && rows.Skip(1).All(r => r.Value == 0), "Rows must not start together.");
// Total 0.115 s sits between row 1's (~0.081 s) and row 2's (~0.145 s) stagger delay.
foreach (var row in rows) row.Update(0.09);
Check(rows[0].Value < rows[1].Value && rows[1].Value < 0 && rows[2].Value == 0,
    "Movement must propagate down the rows.");
foreach (var row in rows) row.Update(3);
Check(rows.All(r => r.Value == -120 && !r.IsMoving), "Every row must reach the final layout.");

static LyricScrollMotion Moving(Func<double, double, double, double> easing)
{
    var motion = new LyricScrollMotion();
    motion.Start(-120, 0.7, 0, true, easing);
    motion.Update(0.15);
    return motion;
}
var moving = Moving(apple);
double position = moving.Value, velocity = moving.Velocity;
moving.Start(-240, 0.5, 0, true, apple);
Check(moving.Value == position && moving.Velocity == velocity, "Retarget must preserve position and velocity.");
moving.Update(0.01);
Check(moving.Value < position, "Retarget must continue the motion.");

var queued = Moving(apple);
position = queued.Value;
queued.Start(-240, 0.7, 0.1, true, apple);
queued.Update(0.04);
Check(queued.Value < position, "Waiting for the next target must not freeze existing movement.");
queued.Start(-360, 0.7, 0.1, true, apple);
queued.Update(0.061);
queued.Update(3);
Check(queued.Value == -360, "Rapid lines must replace stale queued targets.");

foreach (int fps in new[] { 30, 60, 120, 144 })
{
    var sampled = new LyricScrollMotion();
    sampled.Start(-300, 0.8, 0.075, true, apple);
    for (int i = 0; i < fps; i++) sampled.Update(1.0 / fps);
    var oneStep = new LyricScrollMotion();
    oneStep.Start(-300, 0.8, 0.075, true, apple);
    oneStep.Update(1);
    Check(Math.Abs(sampled.Value - oneStep.Value) < 1e-8, $"Frame-rate independence: {fps}");
}
moving.Start(100, 0.7, 0, true, apple);
moving.Update(4);
Check(moving.Value == 100, "Backward seeking must settle at the new target.");
queued.Start(-500, 0.7, 0.2, true, apple);
queued.JumpTo(20);
queued.Update(5);
Check(queued.Value == 20 && queued.Velocity == 0 && !queued.IsMoving, "Relayout must clear queued motion.");
queued.Start(42, 0, 0.2, true, apple);
Check(queued.Value == 42 && !queued.IsMoving, "Zero duration must snap without stagger.");
var tween = new LyricScrollMotion();
tween.Start(-100, 0.5, 0.1, false, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine));
tween.Update(0.05);
Check(tween.Value == 0, "Other curves must also support stagger.");
tween.Update(1);
Check(tween.Value == -100, "Other curves must finish after stagger.");

// Tweens share a deadline; springs retain their configured response across all cadences.
foreach (double interval in new[] { 0.15, 0.25, 0.3, 0.4, 0.5, 0.8 })
{
    for (int index = 0; index < 30; index++)
    {
        var timing = LyricScrollMotion.FlowWaveTiming(index, 3, 0.7, interval);
        var springTiming = LyricScrollMotion.FlowWaveTiming(index, 3, 0.7, interval, true);
        Check(springTiming.Duration == 0.7 && springTiming.Delay == timing.Delay,
            "Short lines must reduce propagation without stiffening the spring.");
        Check(timing.Duration >= 0 && timing.Delay >= 0
            && timing.Duration + timing.Delay <= interval * 0.9 + 1e-12,
            $"Shared response deadline: {interval}, row {index}");
        if (index <= 3 || interval <= 0.25)
            Check(timing.Delay == 0, "Focus and very short lines must not wait.");
    }
}
foreach (double boundary in new[] { 0.25, 0.5 })
{
    var before = LyricScrollMotion.FlowWaveTiming(8, 3, 0.7, boundary - 1e-6);
    var after = LyricScrollMotion.FlowWaveTiming(8, 3, 0.7, boundary + 1e-6);
    Check(Math.Abs(before.Delay - after.Delay) < 1e-5
        && Math.Abs(before.Duration - after.Duration) < 1e-5, "Timing thresholds must be continuous.");
}
Check(LyricScrollMotion.FlowWaveTiming(8, 3, 0, 0.3) == (0, 0), "Zero duration disables stagger.");
Check(LyricScrollMotion.FlowWaveTiming(8, 3, 0.7, 0).Duration == 0.7,
    "Missing next timestamp retains configured duration.");

foreach (int fps in new[] { 30, 60, 120, 144 })
foreach (double[] intervals in new[] {
    Enumerable.Repeat(0.15, 12).ToArray(), Enumerable.Repeat(0.25, 12).ToArray(),
    Enumerable.Repeat(0.4, 12).ToArray(), new[] { 0.8, 0.15, 0.4, 0.25, 0.8, 0.15 } })
{
    var rapidRows = Enumerable.Range(0, 20).Select(_ => new LyricScrollMotion()).ToArray();
    for (int current = 0; current < intervals.Length; current++)
    {
        double interval = intervals[current], target = -120 * (current + 1);
        for (int row = 0; row < rapidRows.Length; row++)
        {
            var timing = LyricScrollMotion.FlowWaveTiming(row, current, 0.7, interval, true);
            double oldPosition = rapidRows[row].Value, oldVelocity = rapidRows[row].Velocity;
            rapidRows[row].Start(target, timing.Duration, timing.Delay, true, apple);
            Check(rapidRows[row].Value == oldPosition && rapidRows[row].Velocity == oldVelocity,
                "Rapid retargeting preserves position and velocity.");
        }
        double remaining = interval;
        while (remaining > 1e-10)
        {
            double step = Math.Min(1.0 / fps, remaining);
            foreach (var row in rapidRows) row.Update(step);
            remaining -= step;
        }
        Check(Math.Abs(rapidRows[current].Value - target) < 120,
            $"Focus tracking lag must stay below one row: {fps}, {interval}");
        Check(rapidRows.All(row => Math.Abs(row.Value - target) < 240),
            $"Trailing rows must not accumulate lag: {fps}, {interval}");
    }
}
var promoted = new LyricScrollMotion();
promoted.Start(-120, 0.7, 0.2, true, apple);
promoted.Update(0.05);
var focusTiming = LyricScrollMotion.FlowWaveTiming(2, 2, 0.7, 0.15, true);
promoted.Start(-240, focusTiming.Duration, focusTiming.Delay, true, apple);
promoted.Update(0.01);
Check(promoted.Value < 0, "A queued row becoming active must start immediately.");
promoted.Update(3);
Check(promoted.Value == -240, "Promotion must discard the stale pending destination.");

// Compare the visible speed pulse against the former per-line stiffening at steady cadence.
foreach (double interval in new[] { 0.15, 0.25, 0.4 })
{
    var stable = new LyricScrollMotion();
    var compressed = new LyricScrollMotion();
    double stableMin = double.PositiveInfinity, stableMax = 0;
    double compressedMin = double.PositiveInfinity, compressedMax = 0;
    for (int current = 0; current < 20; current++)
    {
        var timing = LyricScrollMotion.FlowWaveTiming(current, current, 0.7, interval, true);
        stable.Start(-120 * (current + 1), timing.Duration, timing.Delay, true, apple);
        compressed.Start(-120 * (current + 1), interval * 0.85, 0, true, apple);
        for (int frame = 0; frame < 100; frame++)
        {
            stable.Update(interval / 100);
            compressed.Update(interval / 100);
            Check(stable.Velocity <= 0, "Forward cadence must not bounce backwards.");
            if (current < 10) continue;
            stableMin = Math.Min(stableMin, -stable.Velocity);
            stableMax = Math.Max(stableMax, -stable.Velocity);
            compressedMin = Math.Min(compressedMin, -compressed.Velocity);
            compressedMax = Math.Max(compressedMax, -compressed.Velocity);
        }
    }
    Check(stableMax - stableMin < (compressedMax - compressedMin) * 0.8,
        $"Stable response must reduce speed ripple by at least 20%: {interval}");
    Console.WriteLine($"Cadence {interval * 1000:F0}ms: speed ripple {compressedMax - compressedMin:F1} -> {stableMax - stableMin:F1} px/s");
    stable.Update(4);
    Check(stable.Value == -2400 && stable.Velocity == 0, "Continuous tracking must settle after the last line.");
}

var layoutRows = Enumerable.Range(0, 3).Select(i => new RenderLyricsLine
{
    TopLeftPosition = new Vector2(20, i * 100),
    BottomRightPosition = new Vector2(220, i * 100 + 60),
    CenterPosition = new Vector2(120, i * 100 + 30),
}).ToArray();
layoutRows[0].ScrollMotion.JumpTo(-200);
layoutRows[1].ScrollMotion.JumpTo(-90); // visible at y=10..70
layoutRows[2].ScrollMotion.JumpTo(400);
Check(LyricsLayoutManager.CalculateVisibleRange(layoutRows, 0, 0, 100, 100, 0, true) == (1, 1),
    "Visibility must use independently animated rows, not the global scroll.");
Check(LyricsLayoutManager.FindMouseHoverLineIndex(layoutRows, true, new Point(30, 20), 0, 100, 0, true) == 1,
    "Click must select the drawn staggered row rather than the old global position.");
layoutRows[1].ScaleTransition.Value = 0.5;
Check(LyricsLayoutManager.FindMouseHoverLineIndex(layoutRows, true, new Point(30, 40), 0, 100, 0, true) == -1,
    "Hit testing must account for row scaling.");
Check(LyricsLayoutManager.FindMouseHoverLineIndex(layoutRows, true, new Point(100, 80), 0, 100, 0, true, 40) == 1,
    "Manual scroll must apply the same shared offset as drawing.");
layoutRows[2].ScrollMotion.JumpTo(-190); // overlaps row 1, drawn on top
Check(LyricsLayoutManager.FindMouseHoverLineIndex(layoutRows, true, new Point(100, 40), 0, 100, 0, true) == 2,
    "Overlapping retargeted rows must follow draw order, not binary-search order.");
Check(LyricsLayoutManager.FindMouseHoverLineIndex(layoutRows, false, new Point(100, 40), 0, 100, 0, true) == -1,
    "Pointer outside lyrics must not select a row.");

var options = new JsonSerializerOptions();
options.Converters.Add(new WinUIMusicPlayer.Model.LyricsEasingTypeJsonConverter());
options.Converters.Add(new WinUIMusicPlayer.Model.LyricsEaseModeJsonConverter());
Check(JsonSerializer.Deserialize<EasingType>("\"AppleMusic\"", options) == EasingType.FlowWave
    && JsonSerializer.Deserialize<EaseMode>("\"AppleMusic\"", options) == EaseMode.FlowWave,
    "Experimental saved names must remain readable.");
Check(JsonSerializer.Serialize(EasingType.FlowWave, options) == "\"FlowWave\"",
    "Save only the new name.");
foreach (var curve in Enum.GetValues<EasingType>())
    Check(JsonSerializer.Deserialize<EasingType>(JsonSerializer.Serialize(curve, options), options) == curve,
        $"Preserve saved curve: {curve}");
foreach (var mode in Enum.GetValues<EaseMode>())
    Check(JsonSerializer.Deserialize<EaseMode>(JsonSerializer.Serialize(mode, options), options) == mode,
        $"Preserve saved direction: {mode}");
Check(JsonSerializer.Deserialize<EasingType>(JsonSerializer.Serialize(EasingType.FlowWave, options), options)
    == EasingType.FlowWave, "Curve persistence");
Check(JsonSerializer.Deserialize<EaseMode>(JsonSerializer.Serialize(EaseMode.FlowWave, options), options)
    == EaseMode.FlowWave, "Direction persistence");

// Run from the repository root, or pass its path as the first argument.
string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
foreach (string view in new[] { "View/SubView/SettingsDialog.xaml", "View/SubView/Settings/LyricsSettingsControl.xaml" })
{
    var xaml = XDocument.Load(Path.Combine(root, view));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    foreach (string uid in new[] { "EasingTypeFlowWave", "EaseModeFlowWave" })
        Check(xaml.Descendants().Count(e => (string?)e.Attribute(x + "Uid") == uid) == 1,
            $"Missing setting option: {view}, {uid}");
}
foreach (string path in Directory.GetFiles(Path.Combine(root, "Strings"), "Resources.resw", SearchOption.AllDirectories))
{
    var document = XDocument.Load(path);
    foreach (string key in new[] { "EasingTypeFlowWave.Content", "EaseModeFlowWave.Content" })
    {
        var entries = document.Root!.Elements("data").Where(x => (string?)x.Attribute("name") == key).ToArray();
        Check(entries.Length == 1 && !string.IsNullOrWhiteSpace(entries[0].Element("value")?.Value),
            $"Missing or duplicate resource: {path}, {key}");
    }
}
Console.WriteLine("PASS: staggered rows, spring retargeting, queued targets, frame rates, seeks, relayout, visibility, hit testing, both settings views, persistence, and localization.");

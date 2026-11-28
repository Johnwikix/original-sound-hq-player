using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Enums;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;

internal sealed partial class ProbeApplication
{
    private sealed record LineGlowSample(float Peak, float Settled, int SdrHaloPixels);
    private sealed record LineGlowEnvelope(int MotionTick, int FadeInTick, int PeakTick, int ReleaseTick, int SettledTick,
        float HeldRadius, float TailRadius);
    private sealed record LineGlowInterruption(double BeforeCut, double AfterCut, double After100, double Settled, float CutInk, float CutRadius);

    private object MeasureLineEntryGlow(CanvasDevice device)
    {
        const int width = 420, height = 350;
        const long start = 1000;
        using var target = CreateFloatTarget(device, width, height);
        Rect previousRegion = _lyrics.LyricsRegion;
        _lyrics.LyricsRegion = new Rect(0, 0, width, height);
        LyricsFontSizeBus.Publish(32);
        IsPlayingBus.Publish(true);
        long position = 0;
        TimeProgressBus.SetClock(() => position);
        try
        {
            var untimedAuto = Measure(WordByWordEffectMode.Auto, 10, timed: false, checkSdr: true);
            var timedNever = Measure(WordByWordEffectMode.Never, 10, timed: true);
            var midpoint = Measure(WordByWordEffectMode.Auto, 5, timed: false);
            var zero = Measure(WordByWordEffectMode.Never, 0, timed: true);
            var timedAuto = Measure(WordByWordEffectMode.Auto, 10, timed: true);
            var untimedAlways = Measure(WordByWordEffectMode.Always, 10, timed: false);
            var seek = Measure(WordByWordEffectMode.Auto, 10, timed: false, seek: true);

            if (!Near(untimedAuto.Peak, Hdr.Peak) || untimedAuto.SdrHaloPixels < 80
                || !Near(timedNever.Peak, Hdr.Peak) || !Near(midpoint.Peak, 5f)
                || !Near(zero.Peak, Hdr.White) || !Near(timedAuto.Peak, Hdr.White)
                || !Near(untimedAlways.Peak, Hdr.White) || !Near(seek.Peak, Hdr.White)
                || !Near(untimedAuto.Settled, Hdr.White) || !Near(timedNever.Settled, Hdr.White))
            {
                throw new InvalidOperationException($"Invalid line entry glow. Auto untimed {untimedAuto}; Never timed {timedNever}; midpoint {midpoint}; disabled {zero}; Auto timed {timedAuto}; Always untimed {untimedAlways}; seek {seek}.");
            }

            var standardEnvelope = MeasureEnvelope(false);
            var flowWaveEnvelope = MeasureEnvelope(true);
            var interruptions = MeasureInterruptions();
            return new { UntimedAuto = untimedAuto, TimedNever = timedNever, Midpoint = midpoint,
                Zero = zero, TimedAuto = timedAuto, UntimedAlways = untimedAlways, Seek = seek,
                StandardEnvelope = standardEnvelope, FlowWaveEnvelope = flowWaveEnvelope, Interruptions = interruptions };
        }
        finally
        {
            TimeProgressBus.SetClock(null);
            _lyrics.LyricsRegion = previousRegion;
        }

        LineGlowSample Measure(WordByWordEffectMode mode, double amount, bool timed, bool seek = false, bool checkSdr = false)
        {
            PublishSettings(mode, amount);
            var line = new LyricLine { StartMs = start, EndMs = 5000, HasRealWordTiming = timed };
            line.Words.Add(new LyricWord { Word = "MMMM MMMM MMMM ", StartMs = start, DurationMs = 2000 });
            line.Words.Add(new LyricWord { Word = "MMMM MMMM MMMM MMMM", StartMs = 3000, DurationMs = 2000 });
            var previous = new LyricLine { StartMs = 0, EndMs = start, HasRealWordTiming = false };
            previous.Words.Add(new LyricWord { Word = "Previous verse", StartMs = 0, DurationMs = start });
            UILyricsBus.Publish([previous, line], animateChange: false);
            position = 900;
            for (int i = 0; i < 10; i++) _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(10));
            if (seek) position += 420;

            float peak = 0;
            int haloPixels = 0;
            bool sdrMeasured = false;
            for (int i = 0; i < 112; i++)
            {
                Advance();
                DrawLyricsProbe(target, Hdr);
                byte[] pixels = target.GetPixelBytes();
                float sample = ReadOpaqueInk(pixels).X;
                peak = Math.Max(peak, sample);
                if (checkSdr && !sdrMeasured && Near(sample, Hdr.Peak))
                {
                    int top = height, bottom = 0;
                    for (int offset = 0; offset < pixels.Length; offset += 8)
                    {
                        if (ReadHalf(pixels, offset) < Hdr.Peak - .05f) continue;
                        int row = offset / (width * 8);
                        top = Math.Min(top, row);
                        bottom = Math.Max(bottom, row);
                    }
                    if (bottom - top < 32)
                        throw new InvalidOperationException("The line entry glow did not light both wraps.");

                    DrawLyricsProbe(target, default(PlaybackHdrColor));
                    byte[] active = target.GetPixelBytes();
                    PublishSettings(mode, 0);
                    DrawLyricsProbe(target, default(PlaybackHdrColor));
                    byte[] plain = target.GetPixelBytes();
                    PublishSettings(mode, amount);
                    sdrMeasured = true;
                    for (int offset = 6; offset < active.Length; offset += 8)
                    {
                        if (ReadHalf(plain, offset) <= .001f && ReadHalf(active, offset) > .04f)
                            haloPixels++;
                    }
                }
            }

            for (int i = 0; i < 130; i++) Advance();
            DrawLyricsProbe(target, Hdr);
            return new(peak, ReadOpaqueInk(target.GetPixelBytes()).X, haloPixels);
        }

        LineGlowEnvelope MeasureEnvelope(bool flowWave)
        {
            PublishSettings(WordByWordEffectMode.Never, 10, flowWave);
            UILyricsBus.Publish([MakeLine(0, start, "Previous verse"), MakeLine(start, 5000, "Incoming verse"),
                MakeLine(5000, 6000, "Next verse")], animateChange: false);
            position = start - 10;
            for (int i = 0; i < 60; i++) _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(10));

            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var lines = (List<RenderLyricsLine>)typeof(LyricsRenderCoordinator).GetField("_renderLines", fields)!.GetValue(_lyrics)!;
            var line = lines[1];
            var offsetField = typeof(LyricsRenderCoordinator).GetField("_smoothedScrollY", fields)!;
            double firstOffset = GetOffset();
            double firstOpacity = line.PlayedPrimaryOpacityTransition.Value;
            if (firstOpacity >= 1) throw new InvalidOperationException("The incoming row must have room to fade in.");

            var glowProperty = typeof(RenderLyricsLine).GetProperty("EntryGlowTransition", fields)!;
            var haloField = typeof(RenderLyricsLine).GetField("_entryGlowHalo", fields)!;
            ValueTransition<double>? glow = null;
            int motionTick = 0, fadeInTick = 0, peakTick = 0, releaseTick = 0, settledTick = 0;
            float heldRadius = 0, tailRadius = 0;
            for (int tick = 10; tick <= 2450; tick += 10)
            {
                if (tick == 260) _lyrics.OnSizeChanged();
                Advance();
                glow ??= (ValueTransition<double>?)glowProperty.GetValue(line);
                double glowValue = glow?.Value ?? 0;
                if (motionTick == 0 && Math.Abs(GetOffset() - firstOffset) > 1e-6) motionTick = tick;
                if (peakTick == 0)
                {
                    double fade = (line.PlayedPrimaryOpacityTransition.Value - firstOpacity) / (1 - firstOpacity);
                    if (Math.Abs(fade - glowValue) > 1e-6)
                        throw new InvalidOperationException($"Row opacity and glow drifted apart at {tick} ms ({fade}, {glowValue}, FlowWave {flowWave}).");
                    if (fadeInTick == 0 && glowValue > 1e-6) fadeInTick = tick;
                    if (glowValue == 1) peakTick = tick;
                }
                else
                {
                    if (releaseTick == 0 && glowValue < 1) releaseTick = tick;
                    if (settledTick == 0 && glowValue == 0) settledTick = tick;
                    if (tick - peakTick == 100 || tick - peakTick == 1000)
                    {
                        DrawLyricsProbe(target, Hdr);
                        var halo = (GaussianBlurEffect?)haloField.GetValue(line);
                        if (tick - peakTick == 100) heldRadius = halo?.BlurAmount ?? 0;
                        else tailRadius = halo?.BlurAmount ?? 0;
                    }
                }
            }

            var result = new LineGlowEnvelope(motionTick, fadeInTick, peakTick, releaseTick, settledTick, heldRadius, tailRadius);
            if (fadeInTick == 0 || fadeInTick != motionTick || peakTick - fadeInTick is < 490 or > 510
                || releaseTick - peakTick is < 190 or > 210 || settledTick - releaseTick is < 1480 or > 1510
                || !Near(heldRadius, 10) || tailRadius is < 4f or > 5f)
            {
                throw new InvalidOperationException($"Invalid shared fade, hold or tail (FlowWave {flowWave}): {result}.");
            }
            return result;

            double GetOffset() => flowWave ? line.ScrollMotion.Value : (double)offsetField.GetValue(_lyrics)!;
        }

        object MeasureInterruptions()
        {
            var rapid = MeasureInterruption("rapid");
            var scroll = MeasureInterruption("scroll");
            var seek = MeasureInterruption("seek");
            var pending = MeasureInterruption("pending");
            var hold = MeasureInterruption("hold");
            if (rapid.BeforeCut is < .75 or > .83 || hold.BeforeCut != 1 || !ContinuesFromCurrent(hold)
                || pending.BeforeCut != 0 || pending.AfterCut != 0 || pending.After100 != 0 || pending.Settled != 0
                || !ContinuesFromCurrent(rapid) || !ContinuesFromCurrent(scroll) || !ContinuesFromCurrent(seek))
            {
                throw new InvalidOperationException($"Invalid line glow timing or interruption. Rapid {rapid}; scroll {scroll}; seek {seek}; pending {pending}; hold {hold}.");
            }
            return new { Rapid = rapid, Scroll = scroll, Seek = seek, Pending = pending, Hold = hold };
        }

        LineGlowInterruption MeasureInterruption(string interruption)
        {
            bool rapid = interruption == "rapid";
            bool pending = interruption == "pending";
            bool hold = interruption == "hold";
            bool manual = interruption == "scroll" || pending || hold;
            PublishSettings(WordByWordEffectMode.Never, 10, flowWave: rapid || pending);
            long end = rapid ? 1300 : 5000;
            UILyricsBus.Publish([MakeLine(0, start, "Previous verse"),
                MakeLine(start, end, "Interrupted row"), MakeLine(end, 6000, "Next row")], animateChange: false);
            position = 900;
            int framesBeforeCut = pending ? 10 : hold ? 70 : 39;
            for (int i = 0; i < framesBeforeCut; i++) Advance();

            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var lines = (List<RenderLyricsLine>)typeof(LyricsRenderCoordinator).GetField("_renderLines", fields)!.GetValue(_lyrics)!;
            var glow = (ValueTransition<double>)typeof(RenderLyricsLine).GetProperty("EntryGlowTransition", fields)!.GetValue(lines[1])!;
            double before = glow.Value;
            if (pending && (!glow.IsTransitioning || before != 0))
                throw new InvalidOperationException("The interruption fixture must have an entrance waiting for its scroll delay.");
            var userScrolling = typeof(LyricsRenderCoordinator).GetField("_userScrolling", fields)!;
            var scrollingChanged = typeof(LyricsRenderCoordinator).GetField("_isUserScrollingChanged", fields)!;
            var scrollTimeout = typeof(LyricsRenderCoordinator).GetField("_userScrollCooldownSec", fields)!;
            if (manual)
            {
                userScrolling.SetValue(_lyrics, true);
                scrollingChanged.SetValue(_lyrics, true);
                scrollTimeout.SetValue(_lyrics, 1.0);
            }
            if (rapid) position = 1300;
            if (interruption == "seek") position += 420;

            try
            {
                _lyrics.OnUpdate(target, TimeSpan.Zero);
                double after = glow.Value;
                DrawLyricsProbe(target, Hdr);
                byte[] pixels = target.GetPixelBytes();
                var halo = (GaussianBlurEffect?)typeof(RenderLyricsLine).GetField("_entryGlowHalo", fields)!.GetValue(lines[1]);
                float radius = halo?.BlurAmount ?? 0;
                float litInk = 0;
                for (int offset = 0; offset < pixels.Length; offset += 8)
                {
                    float alpha = ReadHalf(pixels, offset + 6);
                    if (alpha >= .3f) litInk = Math.Max(litInk, ReadHalf(pixels, offset) / alpha);
                }

                for (int i = 0; i < 10; i++) Advance();
                double fading = glow.Value;
                for (int i = 0; i < 65; i++) Advance();
                return new(before, after, fading, glow.Value, litInk, radius);
            }
            finally
            {
                if (manual)
                {
                    userScrolling.SetValue(_lyrics, false);
                    scrollingChanged.SetValue(_lyrics, true);
                }
            }
        }

        static bool ContinuesFromCurrent(LineGlowInterruption sample) => sample.BeforeCut >= .1
            && Math.Abs(sample.AfterCut - sample.BeforeCut) < 1e-6
            && sample.After100 > 0 && sample.After100 < sample.AfterCut
            && sample.Settled == 0 && sample.CutInk > Hdr.White + .25f
            && Near(sample.CutRadius, (float)(sample.AfterCut * 10));

        static LyricLine MakeLine(long from, long to, string text)
        {
            var line = new LyricLine { StartMs = from, EndMs = to, HasRealWordTiming = false };
            line.Words.Add(new LyricWord { Word = text, StartMs = from, DurationMs = to - from });
            return line;
        }

        void Advance()
        {
            position += 10;
            _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(10));
        }

        static bool Near(float left, float right) => Math.Abs(left - right) < .02f;

        static void PublishSettings(WordByWordEffectMode mode, double amount, bool flowWave = false)
            => LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, true, 1, 0,
                amount, 0, 0, 10000, false, false, .5, 0, 0, flowWave ? EasingType.FlowWave : EasingType.Sine,
                flowWave ? EaseMode.FlowWave : EaseMode.Out,
                .35, 60, false, Colors.White, isLyricsEdgeFeatheringEffectEnabled: false,
                wordByWordEffectMode: mode));
    }
}

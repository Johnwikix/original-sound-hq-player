using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;

/// <summary>Checks lazy effect ownership, reuse and recreation with real Win2D drawing.</summary>
internal static class CharacterCacheChecks
{
    internal static void Run(ICanvasResourceCreator creator)
    {
        var character = new RenderLyricsChar(new Rect(0, 0, 32, 32));
        var cropField = typeof(RenderLyricsChar).GetField("_crop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var glowField = typeof(RenderLyricsChar).GetField("_glow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (cropField.GetValue(character) is not null || glowField.GetValue(character) is not null)
            throw new InvalidOperationException("Unused characters allocated native effects.");
        using var image = new CanvasCommandList(creator);
        using (var ds = image.CreateDrawingSession()) ds.FillRectangle(0, 0, 32, 32, Colors.White);
        using var target = new CanvasRenderTarget(creator, 64, 64, 96);
        var crop = character.Crop;
        var glow = character.Glow;
        if (!ReferenceEquals(crop, character.Crop) || !ReferenceEquals(glow, character.Glow))
            throw new InvalidOperationException("Character effects were not cached.");
        crop.Source = image;
        crop.SourceRectangle = new Rect(0, 0, 32, 32);
        glow.BlurAmount = 2;
        using (var ds = target.CreateDrawingSession()) { ds.Clear(Colors.Transparent); ds.DrawImage(glow); }
        byte[] before = target.GetPixelBytes();
        character.DisposeEffects();
        character.DisposeEffects();
        if (cropField.GetValue(character) is not null || glowField.GetValue(character) is not null)
            throw new InvalidOperationException("Retired native effects remained attached.");
        if (ReferenceEquals(crop, character.Crop) || ReferenceEquals(glow, character.Glow))
            throw new InvalidOperationException("A reset character reused closed effects.");
        character.Crop.Source = image;
        character.Crop.SourceRectangle = new Rect(0, 0, 32, 32);
        character.Glow.BlurAmount = 2;
        using (var ds = target.CreateDrawingSession()) { ds.Clear(Colors.Transparent); ds.DrawImage(character.Glow); }
        if (!before.AsSpan().SequenceEqual(target.GetPixelBytes()))
            throw new InvalidOperationException("Character cache recreation changed native pixels.");
        character.DisposeEffects();
    }
}

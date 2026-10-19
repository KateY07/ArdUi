using SkiaSharp;

namespace ArdUi;

// Preview only: three flat logo directions drawn with SkiaSharp (`--render-logo-preview <png>`).
static class LogoPreview
{
    static readonly SKColor Terracotta = new(0xC6, 0x61, 0x3F), Cream = new(0xF5, 0xF4, 0xEE), Ink = new(0x1F, 0x1E, 0x1D), White = SKColors.White;

    public static int Render(string path)
    {
        const int cell = 300, pad = 24;
        var options = new (string Name, string Note, Action<SKCanvas, float, bool> Draw)[]
        {
            ("A  指针 + 信号", "鼠标指针发出信号：远程控制", Pointer),
            ("B  双屏", "两块叠放的屏幕：一台控制另一台", Screens),
            ("C  交叠圆环", "两个圆环相交：两端相连", Rings),
        };
        using var bitmap = new SKBitmap(cell * options.Length + pad * (options.Length + 1), 470);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Cream);
        using var title = new SKPaint { Color = Ink, IsAntialias = true, TextSize = 20, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Bold) };
        using var note = new SKPaint { Color = new SKColor(0x7A, 0x78, 0x6F), IsAntialias = true, TextSize = 13, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        for (var i = 0; i < options.Length; i++)
        {
            var x = pad + i * (cell + pad);
            canvas.DrawText(options[i].Name, x, 34, title);
            canvas.DrawText(options[i].Note, x, 58, note);
            canvas.Save(); canvas.Translate(x + 20, 80); options[i].Draw(canvas, 200, false); canvas.Restore();
            // small sizes the logo must survive: window icon, tray, taskbar
            canvas.Save(); canvas.Translate(x + 20, 310); options[i].Draw(canvas, 48, false); canvas.Restore();
            canvas.Save(); canvas.Translate(x + 84, 318); options[i].Draw(canvas, 32, false); canvas.Restore();
            canvas.Save(); canvas.Translate(x + 128, 326); options[i].Draw(canvas, 16, false); canvas.Restore();
            canvas.Save(); canvas.Translate(x + 168, 310); options[i].Draw(canvas, 48, true); canvas.Restore();   // glyph-only variant (no tile) for the title bar
            canvas.DrawText("48 / 32 / 16 px      无底色", x + 20, 392, note);
        }
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return 0;
    }

    static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
    static SKPaint Stroke(SKColor color, float width) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    // The tile: a terracotta rounded square. With glyphOnly the mark is drawn in terracotta on nothing, for use on light chrome.
    static void Tile(SKCanvas c, float size, bool glyphOnly)
    {
        if (glyphOnly) return;
        using var tile = Fill(Terracotta);
        c.DrawRoundRect(new SKRect(0, 0, size, size), size * 0.23f, size * 0.23f, tile);
    }

    static void Pointer(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var body = Fill(ink); using var edge = Stroke(ink, s * 0.03f);
        using var cursor = new SKPath();
        cursor.MoveTo(s * .30f, s * .24f); cursor.LineTo(s * .30f, s * .74f); cursor.LineTo(s * .43f, s * .62f); cursor.LineTo(s * .52f, s * .82f);
        cursor.LineTo(s * .61f, s * .78f); cursor.LineTo(s * .52f, s * .58f); cursor.LineTo(s * .70f, s * .58f); cursor.Close();
        c.DrawPath(cursor, body); c.DrawPath(cursor, edge);
        using var wave = Stroke(ink, s * .055f);
        c.DrawArc(new SKRect(s * .34f - s * .20f, s * .28f - s * .20f, s * .34f + s * .20f, s * .28f + s * .20f), -82, 62, false, wave);
        c.DrawArc(new SKRect(s * .34f - s * .27f, s * .28f - s * .27f, s * .34f + s * .27f, s * .28f + s * .27f), -82, 62, false, wave);
    }

    static void Screens(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var back = new SKPaint { Color = ink.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var front = Fill(ink); using var hole = Fill(glyphOnly ? Cream : Terracotta);
        c.DrawRoundRect(new SKRect(s * .20f, s * .22f, s * .68f, s * .60f), s * .06f, s * .06f, back);
        c.DrawRoundRect(new SKRect(s * .32f, s * .38f, s * .80f, s * .76f), s * .06f, s * .06f, front);
        c.DrawCircle(s * .56f, s * .57f, s * .045f, hole);     // the remote screen is "live"
    }

    static void Rings(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var ring = Stroke(ink, s * .07f); using var lens = new SKPaint { Color = ink.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Fill };
        float r = s * .2f, y = s * .5f, ax = s * .39f, bx = s * .61f;
        using var left = new SKPath(); left.AddCircle(ax, y, r); using var right = new SKPath(); right.AddCircle(bx, y, r);
        using var both = left.Op(right, SKPathOp.Intersect);
        if (both != null) c.DrawPath(both, lens);
        c.DrawCircle(ax, y, r, ring); c.DrawCircle(bx, y, r, ring);
    }

    static void Link(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var line = Stroke(ink, s * 0.07f); using var dot = Fill(ink);
        float y = s * 0.5f, a = s * 0.27f, b = s * 0.73f, r = s * 0.115f;
        c.DrawLine(a, y, b, y, line);
        c.DrawCircle(a, y, r, dot); c.DrawCircle(b, y, r, dot);
    }

    static void Arch(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var stroke = Stroke(ink, s * 0.085f); using var dot = Fill(ink);
        c.DrawLine(s * 0.27f, s * 0.76f, s * 0.5f, s * 0.26f, stroke);
        c.DrawLine(s * 0.5f, s * 0.26f, s * 0.73f, s * 0.76f, stroke);
        c.DrawCircle(s * 0.5f, s * 0.2f, s * 0.085f, dot);
    }

    static void Orbit(SKCanvas c, float s, bool glyphOnly)
    {
        Tile(c, s, glyphOnly);
        var ink = glyphOnly ? Terracotta : White;
        using var ring = Stroke(ink, s * 0.075f); using var dot = Fill(ink);
        var box = new SKRect(s * 0.25f, s * 0.25f, s * 0.75f, s * 0.75f);
        c.DrawArc(box, -50, 280, false, ring);          // ring with an opening at the upper right
        c.DrawCircle(s * 0.5f + s * 0.25f * (float)Math.Cos(-25 * Math.PI / 180), s * 0.5f + s * 0.25f * (float)Math.Sin(-25 * Math.PI / 180), s * 0.075f, dot);
    }
}

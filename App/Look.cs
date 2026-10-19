using Avalonia.Animation;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;

namespace ArdUi;

// One place for the look: colour tokens, flat rounded buttons, soft inputs and short transitions.
static class Look
{
    public static readonly IBrush Bg = Brush.Parse("#F5F4EE"), Card = Brush.Parse("#FFFFFF"), CardHover = Brush.Parse("#FDFCF9"),
        Line = Brush.Parse("#E6E3D8"), Ink = Brush.Parse("#1F1E1D"), Muted = Brush.Parse("#7A786F"),
        Accent = Brush.Parse("#C6613F"), AccentHover = Brush.Parse("#B3532F"), AccentPressed = Brush.Parse("#9E4727"),
        Ghost = Brush.Parse("#EFEDE4"), GhostHover = Brush.Parse("#E7E4D8"), GhostPressed = Brush.Parse("#DDD9CA"),
        OnlineDeep = Brush.Parse("#2F7D4F"), OnlineHalo = Brush.Parse("#CFE8D6"), Online = Brush.Parse("#5E9E6E"), Relay = Brush.Parse("#D79A3B"), Idle = Brush.Parse("#BDBAAE");
    public static readonly BoxShadows CardShadow = BoxShadows.Parse("0 1 1 0 #0A000000");
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(130);

    static Transitions Fade(AvaloniaProperty brush) => new() { new BrushTransition { Property = brush, Duration = Fast } };

    // Line icons share one stroke style so the cross, minus and share marks look like one family (24-unit grid).
    public const string CrossPath = "M5,5 L19,19 M19,5 L5,19", MinusPath = "M5,12 L19,12",
        SharePath = "M21,5 a3,3 0 1 1 -6,0 a3,3 0 1 1 6,0 z M9,12 a3,3 0 1 1 -6,0 a3,3 0 1 1 6,0 z M21,19 a3,3 0 1 1 -6,0 a3,3 0 1 1 6,0 z M8.59,13.51 l6.83,3.98 M15.41,6.51 l-6.82,3.98";
    public static Avalonia.Controls.Shapes.Path Icon(string data, double size = 14, IBrush? stroke = null, double thickness = 1.8) => new()
    {
        Data = Geometry.Parse(data), Stroke = stroke ?? Muted, StrokeThickness = thickness, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false,
    };

    // A zero-height line cannot be stretched into a box, so the minus mark is a small rounded bar.
    public static Border MinusIcon() => new() { Width = 12, Height = 2, CornerRadius = new CornerRadius(1), Background = Muted,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };

    public static void Apply(Styles styles)
    {
        // Buttons: flat, rounded, ghost by default; the "primary" class carries the one main action of a row.
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Ghost), new Setter(TemplatedControl.ForegroundProperty, Ink),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(13, 5)),
                new Setter(TemplatedControl.FontSizeProperty, 12.0), new Setter(Layoutable.MinHeightProperty, 30.0),
                new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)),
                new Setter(Visual.OpacityProperty, 1.0),
                new Setter(Animatable.TransitionsProperty, new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = Fast } }),
            }
        });
        Presenter(styles, x => x.OfType<Button>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(Animatable.TransitionsProperty, Fade(ContentPresenter.BackgroundProperty)));
        Presenter(styles, x => x.OfType<Button>().Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.BackgroundProperty, GhostHover), new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent));
        Presenter(styles, x => x.OfType<Button>().Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.BackgroundProperty, GhostPressed));
        styles.Add(new Style(x => x.OfType<Button>().Class("primary"))
        { Setters = { new Setter(TemplatedControl.BackgroundProperty, Accent), new Setter(TemplatedControl.ForegroundProperty, Brushes.White) } });
        Presenter(styles, x => x.OfType<Button>().Class("primary").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.ForegroundProperty, Brushes.White));
        Presenter(styles, x => x.OfType<Button>().Class("primary").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.BackgroundProperty, AccentHover), new Setter(ContentPresenter.ForegroundProperty, Brushes.White));
        Presenter(styles, x => x.OfType<Button>().Class("primary").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.BackgroundProperty, AccentPressed), new Setter(ContentPresenter.ForegroundProperty, Brushes.White));
        Presenter(styles, x => x.OfType<Button>().Class(":disabled").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.OpacityProperty, 0.45));
        styles.Add(new Style(x => x.OfType<Button>().Class("quiet"))
        { Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.ForegroundProperty, Muted) } });
        Presenter(styles, x => x.OfType<Button>().Class("quiet").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            new Setter(ContentPresenter.BackgroundProperty, Ghost));

        // Inputs: soft border, rounded, accent ring while focused.
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)), new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 6)),
                new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.BackgroundProperty, Brushes.White),
                new Setter(Layoutable.MinHeightProperty, 32.0), new Setter(TemplatedControl.FontSizeProperty, 13.0),
            }
        });
        Presenter(styles, x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"),
            new Setter(Border.BorderBrushProperty, Accent));
        // Thin, quiet scrollbars.
        styles.Add(new Style(x => x.OfType<ScrollBar>())
        { Setters = { new Setter(Layoutable.MinWidthProperty, 6.0), new Setter(Layoutable.MinHeightProperty, 6.0), new Setter(Visual.OpacityProperty, 0.55),
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) } });
        Presenter(styles, x => x.OfType<ScrollBar>().Template().OfType<RepeatButton>(), new Setter(Visual.IsVisibleProperty, false));
        styles.Add(new Style(x => x.OfType<ScrollBar>().Class(":vertical")) { Setters = { new Setter(Layoutable.WidthProperty, 8.0) } });
        styles.Add(new Style(x => x.OfType<ScrollBar>().Class(":horizontal")) { Setters = { new Setter(Layoutable.HeightProperty, 8.0) } });
        Presenter(styles, x => x.OfType<ScrollBar>().Template().OfType<Thumb>(),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)), new Setter(TemplatedControl.BackgroundProperty, Idle));
        styles.Add(new Style(x => x.OfType<ToolTip>())
        { Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 5)) } });
    }

    static void Presenter(Styles styles, Func<Selector?, Selector?> selector, params Setter[] setters)
    {
        var style = new Style(selector);
        foreach (var setter in setters) style.Setters.Add(setter);
        styles.Add(style);
    }

    public static Transitions BrushFade(AvaloniaProperty property) => Fade(property);
}

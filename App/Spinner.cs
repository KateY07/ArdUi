using Avalonia.Animation;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;

namespace ArdUi;

// A three-quarter ring that turns while it is visible; the animation stops when it is hidden or detached.
sealed class Spinner : Avalonia.Controls.Shapes.Arc
{
    CancellationTokenSource? run;

    public Spinner(double size = 16)
    {
        Width = Height = size; StartAngle = 0; SweepAngle = 270; StrokeThickness = Math.Max(2, size / 8);
        Stroke = Look.Accent; StrokeLineCap = PenLineCap.Round;
        RenderTransform = new RotateTransform(); RenderTransformOrigin = RelativePoint.Center;
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Sync(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Stop(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Sync();
    }

    void Sync() { if (IsVisible && this.GetVisualRoot() != null) Start(); else Stop(); }
    void Start()
    {
        if (run != null) return;
        run = new CancellationTokenSource();
        var turn = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(900), IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360.0) } },
            }
        };
        try { _ = turn.RunAsync(this, run.Token); } catch (InvalidOperationException) { }
    }
    void Stop() { run?.Cancel(); run = null; }
}

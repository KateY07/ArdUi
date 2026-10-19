using Avalonia.Animation;
using Avalonia.Media.Transformation;

namespace ArdUi;

// The placeholder that flies into the "connected" area while a connection is being made. It shows progress, asks for the
// connection password in place, reports a failure in place, and on success draws a green check and then makes way for
// the real session card.
sealed class ConnectCard : Border
{
    static readonly IBrush Red = Brush.Parse("#B3432B");
    readonly bool animated;
    readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock subtitle = new() { FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
    readonly ContentControl icon = new() { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top };
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
    readonly ContentControl extra = new();
    readonly TranslateTransform shift = new();
    public event Action? CancelRequested;

    public ConnectCard(string name, bool animated)
    {
        this.animated = animated;
        title.Text = name;
        CornerRadius = new CornerRadius(12); Padding = new Thickness(14, 10); Background = Look.Card; BorderBrush = Look.CardEdge; BorderThickness = new Thickness(1);
        BoxShadow = Look.CardShadow; RenderTransform = shift;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        icon.Margin = new Thickness(0, 0, 12, 0);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
        Grid.SetColumn(text, 1); Grid.SetColumn(buttons, 2);
        Grid.SetRow(extra, 1); Grid.SetColumn(extra, 1); Grid.SetColumnSpan(extra, 2); extra.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(icon); grid.Children.Add(text); grid.Children.Add(buttons); grid.Children.Add(extra);
        Child = grid;
        if (animated) { Opacity = 0; shift.Y = 26; }
    }

    // ---- entering and leaving -------------------------------------------------------------------------------------
    public async Task Enter()
    {
        if (!animated) return;
        var fly = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(320), Easing = new Avalonia.Animation.Easings.CubicEaseOut(), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, 26.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } },
            }
        };
        try { await fly.RunAsync(this); } catch (InvalidOperationException) { }
        Opacity = 1; shift.Y = 0;
    }

    public async Task Leave()
    {
        if (!animated) return;
        var go = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240), Easing = new Avalonia.Animation.Easings.CubicEaseIn(), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, -10.0) } },
            }
        };
        try { await go.RunAsync(this); } catch (InvalidOperationException) { }
        Opacity = 0;
    }

    // ---- states -----------------------------------------------------------------------------------------------------
    Button Make(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 64 }; if (primary) b.Classes.Add("primary");
        b.Click += (_, _) => click(); return b;
    }

    // A small round grey cross for cancelling or dismissing.
    Button CircleX(Action click, string tip)
    {
        var b = new Button { Content = Look.Icon(Look.CrossPath, 10, Brushes.White, 2.4), Width = 26, Height = 26 };
        b.Classes.Add("circlex"); ToolTip.SetTip(b, tip); b.Click += (_, _) => click(); return b;
    }

    void Reset()
    {
        buttons.Children.Clear(); extra.Content = null;
        BorderBrush = Look.CardEdge; subtitle.Foreground = Look.Muted;
    }

    public void SetProgress(string stage)
    {
        if (icon.Content is not Logo { Animate: true })
        {
            Reset(); icon.Content = new Logo(30) { Animate = animated };
            buttons.Children.Add(CircleX(() => CancelRequested?.Invoke(), "取消"));
        }
        subtitle.Text = stage;
    }

    public Task<string?> AskPassword(string? problem)
    {
        Reset();
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        icon.Content = new Logo(30);
        if (problem != null) { BorderBrush = Red; subtitle.Foreground = Red; }
        subtitle.Text = problem ?? "请输入对方提供的 6 位连接密码，通过后以后不再需要。";
        var box = new TextBox { MaxLength = 6, Watermark = "连接密码", FontFamily = new FontFamily("Consolas"), FontSize = 16, LetterSpacing = 4, MinHeight = 34, HorizontalContentAlignment = HorizontalAlignment.Center };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            var clean = new string((box.Text ?? "").Where(char.IsDigit).ToArray());
            if (clean != box.Text) box.Text = clean;
        };
        void Submit() { if ((box.Text ?? "").Length == 6) result.TrySetResult(box.Text); }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Submit(); } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(box); var ok = Make("连接", Submit, true); ok.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(ok, 1); row.Children.Add(ok);
        extra.Content = row;
        buttons.Children.Add(CircleX(() => result.TrySetResult(null), "取消"));
        box.Focus();
        return result.Task;
    }

    public Task<bool> ShowFailure(ConnectProblem problem, bool canRetry)
    {
        Reset();
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        icon.Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brush.Parse("#FBE4DE"), Child = Look.Icon(Look.CrossPath, 12, Red, 2.2) };
        BorderBrush = Red; subtitle.Foreground = Red; subtitle.Text = problem.Title + "：" + problem.Hint;
        buttons.Children.Add(CircleX(() => result.TrySetResult(false), "关闭"));
        if (canRetry) { var retry = Make("重试", () => result.TrySetResult(true), true); retry.Content = Look.IconText(Look.RefreshPath, "重试", Brushes.White); buttons.Children.Add(retry); }
        return result.Task;
    }

    // A green disc with a check that draws itself, held for a moment.
    public async Task Succeed()
    {
        Reset(); BorderBrush = Look.OnlineDeep; subtitle.Text = "已连接";
        var check = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,12.5 L10,17 L19,7.5"), Stroke = Brushes.White, StrokeThickness = 2.4, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeDashArray = animated ? [9, 9] : null, StrokeDashOffset = animated ? 9 : 0,
        };
        icon.Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Look.OnlineDeep, Child = check };
        if (!animated) return;
        var draw = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(420), Easing = new Avalonia.Animation.Easings.CubicEaseOut(), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Avalonia.Controls.Shapes.Shape.StrokeDashOffsetProperty, 9.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Avalonia.Controls.Shapes.Shape.StrokeDashOffsetProperty, 0.0) } },
            }
        };
        try { await draw.RunAsync(check); } catch (InvalidOperationException) { }
        check.StrokeDashOffset = 0;
        await Task.Delay(650);
    }
}

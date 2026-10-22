namespace ArdUi;

// Chooses what a controlling device may use: FRD, RDP, the conversation, files and calls. Large chips (with a note) for the approval
// card, small pills for the list of authorised devices.
sealed class PermissionPicker : Border
{
    const string Tick = "M5,12.5 L10,17 L19,7.5";
    static readonly IBrush OnFill = new SolidColorBrush(Color.FromArgb(255, 252, 236, 228));
    readonly bool large, allowNone;
    readonly (Perm Flag, string Title, string Short, string Note)[] items =
    [
        (Perm.Frd, "远程桌面（FRD）", "FRD", "推荐：加密、可调画质"),
        (Perm.Rdp, "RDP 远程桌面", "RDP", "Windows 原生，端口 3389"),
        (Perm.Chat, "文字对话", "对话", "双方互发消息"),
        (Perm.Files, "文件与图片", "文件", "双方互传文件，保存到本机"),
        (Perm.Voice, "语音通话", "通话", "双方都可以发起"),
    ];
    Perm value;
    public event Action<Perm>? Changed;
    public Perm Value => value;

    public PermissionPicker(Perm initial, bool large, bool allowNone = false)
    {
        value = initial; this.large = large; this.allowNone = allowNone;
        Build();
    }

    void Build()
    {
        // five small pills wrap on a narrow card instead of running off its edge
        Panel panel = large ? new StackPanel { Spacing = 6 } : new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            var chip = Chip(item.Flag, large ? item.Title : item.Short, item.Note);
            if (!large) chip.Margin = new Thickness(0, 0, 6, 6);
            panel.Children.Add(chip);
        }
        Child = panel;
    }

    Control Chip(Perm flag, string label, string note)
    {
        var on = value.HasFlag(flag);
        Control mark = large
            ? on ? new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Look.Accent, Child = Look.Icon(Tick, 11, Brushes.White, 2.4) }
                 : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderBrush = Look.Idle, BorderThickness = new Thickness(1.4) }
            : on ? Look.Icon(Tick, 11, Look.Accent, 2.2) : new Border { Width = 1 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = label, FontSize = large ? 13 : 12, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, Foreground = on ? (large ? Look.Ink : Look.Accent) : Look.Muted } } };
        if (large) text.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Look.Muted });
        var chip = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = large ? 10 : 5, Children = { mark, text } },
            Padding = large ? new Thickness(12, 8) : new Thickness(11, 4), CornerRadius = new CornerRadius(large ? 12 : 14),
            Background = on ? OnFill : new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), BorderBrush = on ? Look.Accent : Look.Line, BorderThickness = new Thickness(on ? 1.2 : 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        chip.RenderTransformOrigin = RelativePoint.Center; chip.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)"); chip.Transitions = Look.Move(120);
        // hover hints at the accent; pressing sinks the chip a little
        if (!on) { chip.PointerEntered += (_, _) => chip.BorderBrush = Look.Accent; chip.PointerExited += (_, _) => chip.BorderBrush = Look.Line; }
        chip.PointerPressed += (_, e) => { e.Handled = true; chip.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)"); Toggle(flag); };
        return chip;
    }

    void Toggle(Perm flag)
    {
        var next = value ^ flag;
        if (next == Perm.None && !allowNone) return;   // a device with nothing allowed would be useless
        value = next; Build(); Changed?.Invoke(value);
    }
}

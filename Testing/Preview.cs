using Avalonia.VisualTree;
namespace ArdUi;

static class Preview
{
    public static int Render(string path)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        var window = new MainWindow(true); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            if (window.Title?.Contains(Program.Version, StringComparison.Ordinal) != true || window.Content is not Grid)
                throw new IOException("无头界面结构不完整。");
            using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染界面。");
            if (bitmap.PixelSize.Width < 360 || bitmap.PixelSize.Height < 340) throw new IOException("无头界面尺寸异常。");
            bitmap.Save(path);VerifyConfirmation(window);VerifySessionPrompt(window);return 0;
        }
        finally { window.Close(); }
    }
    static void VerifyConfirmation(MainWindow owner)
    {
        var method=typeof(MainWindow).GetMethod("ConfirmPair",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        Task<bool> Open(CancellationToken ct)=>(Task<bool>)method.Invoke(owner,[new Pairing("ABC123",new string('a',64),false),ct])!;
        static bool Finish(Task<bool> pending)
        {
            var limit=Stopwatch.StartNew();
            while(!pending.IsCompleted&&limit.Elapsed<TimeSpan.FromSeconds(2)){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}
            if(!pending.IsCompleted)throw new IOException("Confirmation did not complete.");
            return pending.GetAwaiter().GetResult();
        }
        foreach(var approve in new[]{false,true})
        {
            var pending=Open(CancellationToken.None);Dispatcher.UIThread.RunJobs();
            if(!owner.IsEffectivelyEnabled||pending.IsCompleted)throw new IOException("Pending confirmation disabled its owner or approved automatically.");
            var dialog=owner.OwnedWindows.Single();
            var buttons=((StackPanel)dialog.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            buttons[approve?1:0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if(Finish(pending)!=approve)throw new IOException("Confirmation decision was not preserved.");
        }
        using var cancelled=new CancellationTokenSource();var waiting=Open(cancelled.Token);cancelled.Cancel();
        if(Finish(waiting)||owner.OwnedWindows.Count!=0)throw new IOException("Cancelled confirmation remained active.");
        Console.WriteLine("PASS: non-modal fingerprint confirmation keeps owner enabled; accept, reject and cancellation remain explicit.");
    }
    // "Ask me before every session": a card with Allow and Decline, a yes lets the controller in, anything else keeps it out.
    static void VerifySessionPrompt(MainWindow owner)
    {
        var method=typeof(MainWindow).GetMethod("ConfirmSession",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var panel=(StackPanel)typeof(MainWindow).GetField("pendingIncoming",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
        static bool Finish(Task<bool> pending)
        {
            var limit=Stopwatch.StartNew();
            while(!pending.IsCompleted&&limit.Elapsed<TimeSpan.FromSeconds(2)){Dispatcher.UIThread.RunJobs();Thread.Sleep(1);}
            if(!pending.IsCompleted||pending.IsCanceled)return false;return pending.GetAwaiter().GetResult();
        }
        foreach(var allow in new[]{false,true})
        {
            var pending=(Task<bool>)method.Invoke(owner,[new Pairing("ABC123",new string('a',64),true),CancellationToken.None])!;
            Dispatcher.UIThread.RunJobs();
            if(pending.IsCompleted||panel.Children.Count!=1)throw new IOException("The session prompt did not wait for the user.");
            var buttons=panel.GetVisualDescendants().OfType<Button>().Where(b=>b.Content is "允许连接" or "拒绝").ToArray();
            if(buttons.Length!=2)throw new IOException("The session prompt needs Allow and Decline.");
            buttons.Single(b=>b.Content as string==(allow?"允许连接":"拒绝")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if(Finish(pending)!=allow||panel.Children.Count!=0)throw new IOException("The session prompt decision was not kept.");
        }
        using var cancelled=new CancellationTokenSource();
        var waiting=(Task<bool>)method.Invoke(owner,[new Pairing("ABC123",new string('a',64),true),cancelled.Token])!;
        Dispatcher.UIThread.RunJobs();cancelled.Cancel();
        if(Finish(waiting)||panel.Children.Count!=0)throw new IOException("A cancelled session prompt stayed open.");
        Console.WriteLine("PASS: the per-session confirmation card allows, declines and cancels.");
    }
}

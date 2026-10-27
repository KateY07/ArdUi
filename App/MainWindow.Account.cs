namespace ArdUi;

// The account. Anonymous until signed in with a user of this network's M (made by an administrator, or registered on M's account
// page and approved by one). Only a signed-in device may connect to (control) others; an anonymous one can only be controlled.
// Signing in, registering and changing the password all happen on M's account page in the browser; ArdUi opens it and waits. A
// floating card offers that when it is needed (a connection the person started was refused, or the account in the title capsule
// was clicked); the settings show the account and offer the same.
sealed partial class MainWindow
{
    readonly TextBlock accountLabel = new() { Text = "匿名用户", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock accountState = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel signedOut = new() { Spacing = 8 }, signedIn = new() { Spacing = 8 };
    string? shownAccount = "";

    // ---- the floating card ------------------------------------------------------------------------------------------------
    readonly Border accountOverlay = new() { Background = Brush.Parse("#8C1F1E1D"), IsVisible = false };
    readonly TextBlock accountReason = new() { Foreground = Look.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock accountMessage = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button webLogin = new() { Content = "在浏览器中登录", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    CancellationTokenSource? waitingLogin;

    Border BuildAccountOverlay()
    {
        webLogin.Classes.Add("primary");
        webLogin.Click += async (_, _) => await SignInInBrowser();
        var register = new Button { Content = "注册新账号", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        register.Click += async (_, _) => { if (directory != null) await OpenUrl(directory.AccountPage("#register").ToString()); };
        var card = Card(WithCloseIcon(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "账号", FontSize = 17, FontWeight = FontWeight.SemiBold }, accountReason,
                new TextBlock { Text = "登录和注册都在浏览器中完成。登录后本机才能连接其他设备；注册的账号需要管理员审核。被控的一方不需要账号。", Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                webLogin, register, accountMessage,
            },
        }, CloseAccount));
        card.Background = Look.CardSolid; card.BorderBrush = Look.Line; card.Padding = new Thickness(20); card.MaxWidth = 380; card.Margin = new Thickness(14);
        card.HorizontalAlignment = HorizontalAlignment.Stretch; card.VerticalAlignment = VerticalAlignment.Center;
        accountOverlay.Child = new ScrollViewer { VerticalAlignment = VerticalAlignment.Center, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = card };
        return accountOverlay;
    }

    // Opens M's account page for this device and waits (the card says so) until it was signed in there, or the request ran out.
    async Task SignInInBrowser()
    {
        if (directory == null) return;
        waitingLogin?.Cancel(); var wait = waitingLogin = new CancellationTokenSource();
        try
        {
            webLogin.IsEnabled = false; Note("正在打开浏览器…", null);
            var (token, page, until) = await directory.StartWebLogin(wait.Token);
            await OpenUrl(page.ToString());
            Note("请在浏览器中完成登录（10 分钟内有效）。完成后这里会自动更新。", null);
            if (await directory.WaitWebLogin(token, until, wait.Token)) { CloseAccount(); UpdateAccount(); Toast("已登录为 " + directory.Username); }
            else Note("登录请求已过期，请再点一次“在浏览器中登录”。", true);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or TaskCanceledException) { Note(ex.Message, true); }
        finally { webLogin.IsEnabled = true; if (waitingLogin == wait) waitingLogin = null; }
    }

    void Note(string text, bool? bad)
    {
        accountMessage.Text = text; accountMessage.IsVisible = true;
        accountMessage.Foreground = bad switch { true => Brush.Parse("#B3432B"), false => Brush.Parse("#2F7D4F"), _ => Muted };
    }

    // Shows the card; reason says why it came up by itself (a refused connection). A signed-in device goes to its settings instead.
    void OpenAccount(string? reason = null)
    {
        if (directory?.Username != null) { ShowSettings(true); selectSettingsTab?.Invoke(0); return; }
        accountReason.Text = reason ?? ""; accountReason.IsVisible = reason != null;
        if (waitingLogin == null) accountMessage.IsVisible = false;
        accountOverlay.IsVisible = true;
    }

    void CloseAccount() { accountOverlay.IsVisible = false; waitingLogin?.Cancel(); }

    // ---- the settings ------------------------------------------------------------------------------------------------------
    Control BuildAccount()
    {
        var open = Button("在浏览器中登录", () => { OpenAccount(); return SignInInBrowser(); });
        open.Classes.Add("primary");
        var register = Button("注册新账号", () => directory == null ? Task.CompletedTask : OpenUrl(directory.AccountPage("#register").ToString()));
        signedOut.Children.Add(new TextBlock
        {
            Text = "未登录（匿名用户）时，本机只能被控，不能连接其他设备。登录后才能连接和控制其他设备。登录和注册都在浏览器中完成；注册的账号需要网络管理员审核。被控的一方不需要账号。",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        signedOut.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { open, register } });

        var password = Button("修改密码（在浏览器中）", () => directory == null ? Task.CompletedTask : OpenUrl(directory.AccountPage("#password").ToString()));
        var logout = Button("退出登录", async () => { if (directory != null) { await directory.Logout(); UpdateAccount(); Say("已退出登录，现在是匿名用户。"); } });
        signedIn.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { password, logout } });
        UpdateAccount();
        return Card(new StackPanel { Spacing = 10, Children = { accountState, signedOut, signedIn } });
    }

    void UpdateAccount()
    {
        var name = directory?.Username;
        if (name == shownAccount) return;
        shownAccount = name;
        accountLabel.Text = name ?? "匿名用户";
        accountLabel.Foreground = name == null ? Look.Muted : Look.Ink;
        accountLabel[Loc.KeepProperty] = name != null;
        accountState.Text = name == null ? "匿名用户" : "已登录：" + name;
        signedOut.IsVisible = name == null; signedIn.IsVisible = name != null;
    }
}

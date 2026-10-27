namespace ArdUi;

// The account (settings → 通用): anonymous until signed in with a user an administrator of this network's M made. Only a signed-in
// device may connect to (control) others; an anonymous one can only be controlled. The title capsule says who this device is.
sealed partial class MainWindow
{
    readonly TextBlock accountLabel = new() { Text = "匿名用户", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock accountState = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBox accountName = new() { Watermark = "用户名", MaxLength = 40 };
    readonly TextBox accountPassword = new() { Watermark = "密码", PasswordChar = '•', MaxLength = 200 };
    readonly TextBox oldPassword = new() { Watermark = "当前密码", PasswordChar = '•', MaxLength = 200 };
    readonly TextBox newPassword = new() { Watermark = "新密码（至少 8 个字符）", PasswordChar = '•', MaxLength = 200 };
    readonly TextBox newPassword2 = new() { Watermark = "再输入一次新密码", PasswordChar = '•', MaxLength = 200 };
    readonly StackPanel signedOut = new() { Spacing = 8 }, signedIn = new() { Spacing = 8 };
    string? shownAccount = "";

    Control BuildAccount()
    {
        async Task SignIn()
        {
            if (directory == null) return;
            await directory.Login(accountName.Text ?? "", accountPassword.Text ?? "");
            accountPassword.Text = ""; UpdateAccount(); Say("已登录为 " + directory.Username);
        }
        var login = Button("登录", SignIn);
        login.Classes.Add("primary"); login.HorizontalAlignment = HorizontalAlignment.Left;
        accountPassword.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await Guard(SignIn); } };
        signedOut.Children.Add(new TextBlock
        {
            Text = "未登录（匿名用户）时，本机只能被控，不能连接其他设备。登录后才能连接和控制其他设备。账号由网络管理员创建；被控的一方不需要登录。",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        signedOut.Children.Add(accountName); signedOut.Children.Add(accountPassword); signedOut.Children.Add(login);

        var change = Button("修改密码", async () =>
        {
            if (directory == null) return;
            if ((newPassword.Text ?? "") != (newPassword2.Text ?? "")) throw new InvalidOperationException("两次输入的新密码不一致。");
            await directory.ChangePassword(oldPassword.Text ?? "", newPassword.Text ?? "");
            oldPassword.Text = newPassword.Text = newPassword2.Text = ""; Say("密码已修改。");
        });
        var logout = Button("退出登录", async () => { if (directory != null) { await directory.Logout(); UpdateAccount(); Say("已退出登录，现在是匿名用户。"); } });
        signedIn.Children.Add(new TextBlock { Text = "修改密码", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        signedIn.Children.Add(oldPassword); signedIn.Children.Add(newPassword); signedIn.Children.Add(newPassword2);
        signedIn.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { change, logout } });
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

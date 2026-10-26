using Avalonia.Platform.Storage;
namespace ArdUi;

// The shared folder in the settings ("被控与授权"): which folder authorised controllers with "文件" may read and write, and this
// machine's own view of it in the browser (the same page the controller sees).
sealed partial class MainWindow
{
    readonly TextBlock sharedPath = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    readonly Button sharedOpen = new() { Content = "在浏览器中打开" }, sharedReveal = new() { Content = "打开文件夹" }, sharedStop = new() { Content = "停止共享" };

    Control BuildSharedFolder()
    {
        var choose = Button("选择文件夹…", async () =>
        {
            if (directory == null || !Person("设置共享文件夹")) return;
            var picked = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "选择共享文件夹", AllowMultiple = false });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
            directory.SetSharedFolder(folder); UpdateSharedFolder(); Say("已设置共享文件夹：" + folder);
        });
        choose.Classes.Add("primary");
        sharedOpen.Click += async (_, _) => await Guard(() => engine == null ? Task.CompletedTask : OpenUrl($"http://127.0.0.1:{engine.Files.Port}/"));
        sharedReveal.Click += async (_, _) => await Guard(() => { var folder = directory?.SharedFolder ?? Engine.DefaultFilesBase; Directory.CreateDirectory(folder); return Launch("explorer.exe", folder); });
        sharedStop.Click += async (_, _) => await Guard(() => { if (directory != null && Person("停止共享")) { directory.SetSharedFolder(null); UpdateSharedFolder(); Say("已停止共享文件夹。"); } return Task.CompletedTask; });
        var note = new TextBlock
        {
            Text = "有“文件”权限的设备可以读写共享文件夹（浏览、下载、上传、改名、删除），仅限直连。没有选择文件夹时，每台设备使用自己的文件夹：文档\\ArdUi 文件\\<对方计算机名>-<设备ID>。删除和覆盖的内容会移到其中隐藏的 .ardui-trash 文件夹，可以找回。",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { choose, sharedOpen, sharedReveal, sharedStop } };
        UpdateSharedFolder();
        return Card(new StackPanel { Spacing = 10, Children = { sharedPath, note, buttons } });
    }

    void UpdateSharedFolder()
    {
        var folder = directory?.SharedFolder;
        sharedPath.Text = folder ?? "默认：每台设备各用一个文件夹（" + Engine.DefaultFilesBase + "）";
        sharedPath.Foreground = folder == null ? Muted : Look.Ink;
        sharedOpen.IsVisible = sharedStop.IsVisible = folder != null;
        sharedStop.Content = "恢复默认";
    }
}

using System.Windows;

namespace ColumnView;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 退避ごみ箱 (NAS 等) の保持期限切れを背景で掃除する。
        // 起動をブロックしない (オフラインの NAS は Directory.Exists が数秒待つことがある)
        _ = Task.Run(FileOps.PurgeAllTrashRoots);
    }
}

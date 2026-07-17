using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ColumnView;

/// <summary>フォルダーの既定アプリ登録 (エクスプローラー代替) と、
/// 設定アプリの「インストール済みアプリ」一覧への登録。
/// Files アプリと同じ乗っ取りポイントを使う:
///   (1) Folder\shell\open・explore の command 上書き … フォルダー/ドライブのダブルクリック、
///       explorer.exe へのパス指定、「エクスプローラーで表示」系がここへ流れてくる
///   (2) DelegateExecute="" … Explorer 内部への COM 委譲を無効化しコマンド行を実行させる鍵
///   (3) File Explorer 本体 CLSID の opennewwindow … Win+E とタスクバーの Explorer アイコン
/// すべて HKCU 配下のみ書くので管理者権限は不要で、解除すれば HKLM の既定値に戻る。
/// 入口: コマンドライン --register / --unregister (+--quiet)。
/// 設定アプリの「アンインストール」も UninstallString 経由で --unregister を呼ぶ。</summary>
internal static class AppRegistration
{
    private const string FolderShell = @"Software\Classes\Folder\shell";

    /// <summary>「File Explorer」本体の CLSID。Win+E とタスクバーの Explorer アイコンは
    /// この opennewwindow verb を起動する。</summary>
    private const string ExplorerClsid = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    /// <summary>上書きする Folder verb。open=ダブルクリック等の既定、explore=「エクスプローラーで開く」系。</summary>
    private static readonly string[] FolderVerbs = { "open", "explore" };

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ColumnView";

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("実行ファイルのパスを取得できません");

    /// <summary>フォルダーの既定アプリにし、アプリ一覧にも登録する。</summary>
    public static void Register(bool quiet)
    {
        try
        {
            foreach (var verb in FolderVerbs)
            {
                using var command = Registry.CurrentUser.CreateSubKey($@"{FolderShell}\{verb}\command");
                command.SetValue("", $"\"{ExePath}\" \"%1\"");
                command.SetValue("DelegateExecute", "");
            }

            using (var command = Registry.CurrentUser.CreateSubKey($@"{ExplorerClsid}\shell\opennewwindow\command"))
            {
                command.SetValue("", $"\"{ExePath}\"");  // Win+E は引数なし = ホームを開く
                command.SetValue("DelegateExecute", "");
            }

            // 旧方式 (v1.6 の Directory/Drive 独自 verb) が残っていれば掃除する
            RemoveLegacyVerbs();

            WriteUninstallEntry();

            if (!quiet)
                MessageBox.Show(
                    "フォルダーの既定アプリとして登録しました。\n\n" +
                    "・フォルダーのダブルクリック / Win+E で ColumnView が開きます\n" +
                    "・設定アプリの「インストール済みアプリ」にも表示されます\n\n" +
                    "解除するには設定アプリからアンインストールするか、\n" +
                    "--unregister を付けて実行してください。",
                    "ColumnView", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Fail("登録", ex, quiet);
        }
    }

    /// <summary>関連付けとアプリ登録を解除して Explorer の既定動作に戻す (ファイルは消さない)。</summary>
    public static void Unregister(bool quiet)
    {
        try
        {
            foreach (var verb in FolderVerbs)
            {
                using var verbKey = Registry.CurrentUser.OpenSubKey($@"{FolderShell}\{verb}", writable: true);
                if (verbKey is not null && IsOurCommand(verbKey.OpenSubKey("command")))
                    verbKey.DeleteSubKeyTree("command", throwOnMissingSubKey: false);
                // 空になった親キーを畳む (他ソフトの HKCU カスタマイズは残す)
            }
            foreach (var verb in FolderVerbs)
                DeleteIfEmpty(FolderShell, verb);
            DeleteIfEmpty(@"Software\Classes\Folder", "shell");
            DeleteIfEmpty(@"Software\Classes", "Folder");

            using (var probe = Registry.CurrentUser.OpenSubKey($@"{ExplorerClsid}\shell\opennewwindow\command"))
            {
                if (IsOurCommand(probe))
                    Registry.CurrentUser.DeleteSubKeyTree(ExplorerClsid, throwOnMissingSubKey: false);
            }

            RemoveLegacyVerbs();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

            if (!quiet)
                MessageBox.Show(
                    "フォルダーの関連付けとアプリ登録を解除しました。\n" +
                    "アプリ本体のファイルは削除されません。不要な場合はフォルダーごと削除してください。",
                    "ColumnView", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Fail("解除", ex, quiet);
        }
    }

    /// <summary>command キーの既定値が ColumnView を指しているか (他ツールの設定を壊さないための確認)。</summary>
    private static bool IsOurCommand(RegistryKey? command)
    {
        using (command)
        {
            return command?.GetValue("") is string value
                && value.Contains("ColumnView", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>値もサブキーも無いキーだけを削除する (中身があれば何もしない)。</summary>
    private static void DeleteIfEmpty(string parentPath, string name)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
            if (parent is null)
                return;
            var empty = false;
            using (var key = parent.OpenSubKey(name))
            {
                if (key is null)
                    return;
                empty = key.SubKeyCount == 0 && key.ValueCount == 0;
            }
            if (empty)
                parent.DeleteSubKey(name, throwOnMissingSubKey: false);
        }
        catch
        {
            // 空でない・権限なし等は残してよい
        }
    }

    /// <summary>旧方式 (Directory/Drive の独自 verb + 既定 verb 化) を取り除く。</summary>
    private static void RemoveLegacyVerbs()
    {
        foreach (var cls in new[] { "Directory", "Drive" })
        {
            using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell", writable: true);
            if (shell is null)
                continue;
            if (shell.GetValue("") as string == "ColumnView")
                shell.DeleteValue("", throwOnMissingValue: false);
            shell.DeleteSubKeyTree("ColumnView", throwOnMissingSubKey: false);
        }
    }

    /// <summary>設定アプリ「インストール済みアプリ」用のアンインストール情報を書く。</summary>
    private static void WriteUninstallEntry()
    {
        using var un = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        var dir = Path.GetDirectoryName(ExePath) ?? "";
        un.SetValue("DisplayName", "ColumnView");
        un.SetValue("DisplayIcon", ExePath);
        un.SetValue("DisplayVersion",
            typeof(AppRegistration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        un.SetValue("Publisher", "ColumnView");
        un.SetValue("InstallLocation", dir);
        un.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        un.SetValue("UninstallString", $"\"{ExePath}\" --unregister");
        un.SetValue("NoModify", 1, RegistryValueKind.DWord);
        un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        un.SetValue("EstimatedSize", EstimateSizeKb(dir), RegistryValueKind.DWord);
    }

    private static void Fail(string what, Exception ex, bool quiet)
    {
        Environment.ExitCode = 1;
        if (!quiet)
            MessageBox.Show($"{what}に失敗しました。\n\n{ex.Message}",
                "ColumnView", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>アプリ一覧に出すサイズ (KB)。exe と同じフォルダー直下の合計で十分。</summary>
    private static int EstimateSizeKb(string dir)
    {
        try
        {
            var bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Sum(f => f.Length);
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }
}

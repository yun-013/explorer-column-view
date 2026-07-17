using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ColumnView;

/// <summary>フォルダーの既定アプリ登録 (エクスプローラー代替) と、
/// 設定アプリの「インストール済みアプリ」一覧への登録。
/// すべて HKCU 配下のみ書くので管理者権限は不要で、解除すれば完全に元へ戻る。
/// 入口: コマンドライン --register / --unregister (+--quiet)。
/// 設定アプリの「アンインストール」も UninstallString 経由で --unregister を呼ぶ。</summary>
internal static class AppRegistration
{
    private const string Verb = "ColumnView";

    /// <summary>既定の動詞を上書きするシェルクラス。Directory=実フォルダ、Drive=ドライブルート。
    /// Folder (zip・仮想フォルダーを含む広い概念) はあえて対象外にして副作用を避ける。</summary>
    private static readonly string[] ShellClasses = { "Directory", "Drive" };

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ColumnView";

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("実行ファイルのパスを取得できません");

    /// <summary>フォルダーダブルクリックの既定アプリにし、アプリ一覧にも登録する。</summary>
    public static void Register(bool quiet)
    {
        try
        {
            foreach (var cls in ShellClasses)
            {
                using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell");
                using (var verb = shell.CreateSubKey(Verb))
                {
                    verb.SetValue("", "ColumnView で開く");
                    verb.SetValue("Icon", $"\"{ExePath}\"");
                    using var command = verb.CreateSubKey("command");
                    command.SetValue("", $"\"{ExePath}\" \"%1\"");
                }
                // 既定の動詞にする = ダブルクリック/Enter で ColumnView が開く
                shell.SetValue("", Verb);
            }

            using (var un = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
            {
                var dir = Path.GetDirectoryName(ExePath) ?? "";
                un.SetValue("DisplayName", "ColumnView");
                un.SetValue("DisplayIcon", ExePath);
                un.SetValue("DisplayVersion",
                    typeof(AppRegistration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                un.SetValue("Publisher", "ColumnView");
                un.SetValue("InstallLocation", dir);
                un.SetValue("UninstallString", $"\"{ExePath}\" --unregister");
                un.SetValue("NoModify", 1, RegistryValueKind.DWord);
                un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                un.SetValue("EstimatedSize", EstimateSizeKb(dir), RegistryValueKind.DWord);
            }

            if (!quiet)
                MessageBox.Show(
                    "フォルダーの既定アプリとして登録しました。\n\n" +
                    "・フォルダーのダブルクリックで ColumnView が開きます\n" +
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

    /// <summary>フォルダーの関連付けとアプリ一覧の登録を解除する (ファイルは消さない)。</summary>
    public static void Unregister(bool quiet)
    {
        try
        {
            foreach (var cls in ShellClasses)
            {
                using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell", writable: true);
                if (shell is null)
                    continue;
                // 既定の動詞を自分にしていた場合のみ外す (他ツールの設定は壊さない)
                if (shell.GetValue("") as string == Verb)
                    shell.DeleteValue("", throwOnMissingValue: false);
                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            }
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

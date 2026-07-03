using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ColumnView;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 予期しない例外は %APPDATA%\ColumnView\error.log に記録する (フリーズ/クラッシュ調査用)。
        // UI スレッドの例外はログ後に握りつぶしてアプリごと落ちるのを防ぐ
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash(a.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, a) => { LogCrash(a.Exception, "Task"); a.SetObserved(); };

        // Windows の「アプリのモード」(ライト/ダーク) に追従
        ApplySystemTheme();

        // 退避ごみ箱 (NAS 等) の保持期限切れを背景で掃除する。
        // 起動をブロックしない (オフラインの NAS は Directory.Exists が数秒待つことがある)
        _ = Task.Run(FileOps.PurgeAllTrashRoots);
    }

    // ---- テーマ (ライト / ダーク) ----

    /// <summary>システム設定 (アプリのモード) を読み、対応するパレットを適用する。
    /// 環境変数 COLUMNVIEW_THEME=dark|light で強制もできる (動作確認用)。</summary>
    public static void ApplySystemTheme()
    {
        switch (Environment.GetEnvironmentVariable("COLUMNVIEW_THEME"))
        {
            case "dark": ApplyTheme(true); return;
            case "light": ApplyTheme(false); return;
        }
        var light = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            // 読めなければライトのまま
        }
        ApplyTheme(!light);
    }

    /// <summary>パレットのブラシ/影リソースを一括で差し替える (参照は全て DynamicResource なので即時反映)。</summary>
    private static void ApplyTheme(bool dark)
    {
        var r = Current.Resources;
        void Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            r[key] = brush;
        }
        void SetShadow(string key, string hex, double opacity, double blur, double depth)
        {
            var effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(hex),
                Opacity = opacity,
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
            };
            effect.Freeze();
            r[key] = effect;
        }

        if (dark)
        {
            // 温かみのあるダーク (Claude のダークモード風の焦げ茶ベース)
            Set("AccentBrush", "#D97757");
            Set("AccentSelectBrush", "#2BD97757");
            Set("AccentSelectHoverBrush", "#3AD97757");
            Set("AccentFocusBrush", "#33D97757");
            Set("AccentCrumbHoverBrush", "#26D97757");
            Set("AccentDropHighlightBrush", "#40D97757");
            Set("AccentDropBorderBrush", "#B3D97757");
            Set("WindowBackgroundBrush", "#262421");
            Set("SurfaceBrush", "#37342F");
            Set("CaptionBarBrush", "#161412");
            Set("BorderSoftBrush", "#575145");
            Set("WindowOuterBorderBrush", "#645D4E");
            Set("TextPrimaryBrush", "#F0EDE6");
            Set("TextSecondaryBrush", "#B5AE9C");
            Set("TextTertiaryBrush", "#7D7668");
            Set("HoverOverlayBrush", "#1AFFFFFF");
            Set("PressedOverlayBrush", "#2AFFFFFF");
            Set("RowHoverBrush", "#12FFFFFF");
            Set("TabHoverBrush", "#1FFFFFFF");
            Set("ChipHoverBrush", "#423E36");
            Set("ChipPressedBrush", "#4D4940");
            Set("ScrollThumbBrush", "#40FFFFFF");
            Set("ScrollThumbHoverBrush", "#66FFFFFF");
            // ダークでは黒い影が見えないため、白いグローで面の重なりを表現する
            SetShadow("SoftShadow", "#FFFFFF", 0.10, 12, 1);
            SetShadow("MenuShadow", "#FFFFFF", 0.16, 16, 1);
            SetShadow("CardShadowBelow", "#FFFFFF", 0.10, 8, 1);
            SetShadow("SubtleShadow", "#FFFFFF", 0.09, 9, 1);
        }
        else
        {
            // ライト (App.xaml の既定値と同じ)
            Set("AccentBrush", "#C96442");
            Set("AccentSelectBrush", "#19C96442");
            Set("AccentSelectHoverBrush", "#24C96442");
            Set("AccentFocusBrush", "#1EC96442");
            Set("AccentCrumbHoverBrush", "#15C96442");
            Set("AccentDropHighlightBrush", "#2EC96442");
            Set("AccentDropBorderBrush", "#99C96442");
            Set("WindowBackgroundBrush", "#FCFAF7");
            Set("SurfaceBrush", "#FFFFFE");
            Set("CaptionBarBrush", "#F1ECE2");
            Set("BorderSoftBrush", "#ECE7DD");
            Set("WindowOuterBorderBrush", "#E3DCCF");
            Set("TextPrimaryBrush", "#2B2A27");
            Set("TextSecondaryBrush", "#8A8474");
            Set("TextTertiaryBrush", "#B6AFA0");
            Set("HoverOverlayBrush", "#12000000");
            Set("PressedOverlayBrush", "#1E000000");
            Set("RowHoverBrush", "#0D000000");
            Set("TabHoverBrush", "#66FFFFFF");
            Set("ChipHoverBrush", "#F6F2EB");
            Set("ChipPressedBrush", "#F1ECE2");
            Set("ScrollThumbBrush", "#30000000");
            Set("ScrollThumbHoverBrush", "#55000000");
            SetShadow("SoftShadow", "#000000", 0.08, 12, 1);
            SetShadow("MenuShadow", "#000000", 0.14, 16, 2);
            SetShadow("CardShadowBelow", "#000000", 0.07, 6, 2);
            SetShadow("SubtleShadow", "#000000", 0.05, 9, 1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "UI");
        e.Handled = true;
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\n詳細は %APPDATA%\\ColumnView\\error.log に記録しました。",
            "Column View", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColumnView");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ログの書き込み失敗でさらに落とさない
        }
    }
}

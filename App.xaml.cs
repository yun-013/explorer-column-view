using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ColumnView;

public partial class App : Application
{
    /// <summary>タスクバーのジャンプリスト「新しいウィンドウ」が渡してくる引数。</summary>
    private const string NewWindowArg = "--new-window";

    // 単一インスタンス判定用。プロセス終了まで握りっぱなしにする (GC 回収防止のため保持)
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IdleProbe.StartIfRequested(); // 【一時】アイドル CPU 調査用

        // UI スレッドが 500ms 以上固まったら %APPDATA%\ColumnView\stalls.log に記録する。
        // 「時折固まる」の原因を後から特定するため、常用ビルドでも有効にしておく。
        StallWatch.Start();

        // 予期しない例外は %APPDATA%\ColumnView\error.log に記録する (フリーズ/クラッシュ調査用)。
        // UI スレッドの例外はログ後に握りつぶしてアプリごと落ちるのを防ぐ
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash(a.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, a) => { LogCrash(a.Exception, "Task"); a.SetObserved(); };

        // ---- コマンドライン: 既定アプリ登録/解除 (ウィンドウを開かず終了) ----
        var quiet = e.Args.Contains("--quiet");
        if (e.Args.Contains("--register"))
        {
            AppRegistration.Register(quiet);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--unregister"))
        {
            AppRegistration.Unregister(quiet);
            Shutdown();
            return;
        }

        // フォルダーパス引数 (フォルダーの既定アプリとして起動されたとき Explorer が渡してくる)
        var rawArg = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        string? initialFolder = null;
        if (rawArg is not null)
        {
            initialFolder = NormalizeFolderArg(rawArg);
            if (initialFolder is null)
            {
                // 扱えない引数 (ごみ箱・PC 等の仮想フォルダー ::{CLSID} や shell: パス) は
                // 本物のエクスプローラーに任せて終了する (黙って無視しない)
                PassThroughToExplorer(rawArg);
                Shutdown();
                return;
            }
        }

        // ---- 単一インスタンス: 起動済みなら既存ウィンドウの新タブへ転送 ----
        // 引数なし (Win+E・exe 直接起動) はホームタブを開く要求として転送する
        // --new-window (ジャンプリスト) は新しいタブではなく別ウィンドウを開かせる
        var request = e.Args.Contains(NewWindowArg)
            ? SingleInstance.NewWindowRequest
            : initialFolder ?? SingleInstance.HomeRequest;

        // 開発用の分離プロファイル (COLUMNVIEW_PROFILE_DIR) では常用版と連携せず独立して起動する
        if (AppSettings.DevProfileDir == null)
        {
            _instanceMutex = new Mutex(true, SingleInstance.MutexName, out var isFirst);
            if (!isFirst && SingleInstance.TrySendToExisting(request))
            {
                Shutdown();
                return;
            }
            if (isFirst)
                SingleInstance.StartServer(OpenFolderInExistingWindow);
        }

        // Windows の「アプリのモード」(ライト/ダーク) に追従
        ApplySystemTheme();

        // 退避ごみ箱 (NAS 等) の保持期限切れを背景で掃除する。
        // 起動をブロックしない (オフラインの NAS は Directory.Exists が数秒待つことがある)
        _ = Task.Run(FileOps.PurgeAllTrashRoots);

        ConfigureJumpList();

        new MainWindow(MainViewModel.CreateForStartup(initialFolder)) { RestorePlacement = true }.Show();
    }

    /// <summary>引数をフォルダーパスとして解釈する (扱えないものは null)。
    /// ドライブルートは `"C:\"` の末尾 \ が閉じ引用符をエスケープして `C:"` で届くため補正する。
    /// ファイルのパスは親フォルダーとして解釈する (「エクスプローラーで表示」系の保険)。</summary>
    private static string? NormalizeFolderArg(string raw)
    {
        var path = raw.Trim().Trim('"');
        if (path.Length == 0)
            return null;
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            path += "\\";
        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
        if (Directory.Exists(path))
            return path;
        if (File.Exists(path))
            return Path.GetDirectoryName(path);
        return null;
    }

    /// <summary>仮想フォルダー等、ColumnView が表示できないものを本物のエクスプローラーで開く。
    /// explorer.exe の直接起動は Folder verb を再解決しない (仮想フォルダーはシェル内部処理) ので無限ループしない。</summary>
    private static void PassThroughToExplorer(string arg)
    {
        try
        {
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, $"\"{arg}\"")
            {
                UseShellExecute = false,
            });
        }
        catch
        {
            // 開けなければ諦める (エラーダイアログの連鎖を避ける)
        }
    }

    /// <summary>タスクバーのアイコンを右クリックしたときのメニュー (ジャンプリスト) を組み立てる。
    /// 「新しいウィンドウ」は自分自身を --new-window 付きで起動し、
    /// 起動済みなら単一インスタンス経由で既存プロセスに新しいウィンドウを開かせる。</summary>
    private void ConfigureJumpList()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        try
        {
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            list.JumpItems.Add(new JumpTask
            {
                Title = "新しいウィンドウ",
                Description = "Column View の新しいウィンドウを開きます",
                ApplicationPath = exe,
                Arguments = NewWindowArg,
                IconResourcePath = exe,
            });
            JumpList.SetJumpList(this, list);   // 設定した時点でシェルへ反映される
        }
        catch
        {
            // ジャンプリストは無くても支障が無い (シェルが応答しない環境などで失敗しうる)
        }
    }

    /// <summary>別プロセスから転送された要求を、最後に使ったウィンドウの新タブで開く。
    /// message はフォルダーパスか、ホーム要求 (SingleInstance.HomeRequest)。</summary>
    private void OpenFolderInExistingWindow(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // 注意: App 内では MainWindow が Application.MainWindow プロパティに解決されるため要修飾
            var window = ColumnView.MainWindow.LastActivated is { IsLoaded: true } w
                ? w
                : Windows.OfType<ColumnView.MainWindow>().FirstOrDefault();
            if (message == SingleInstance.NewWindowRequest)
            {
                ColumnView.MainWindow.OpenNewWindow(null, window);
                return;
            }
            if (window is not null)
                _ = window.OpenFolderTabAsync(message == SingleInstance.HomeRequest ? null : message);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // コピー直後に終了してもクリップボードの中身が消えないように
        ClipboardOps.FlushOnExit();
        base.OnExit(e);
        // 終了保険: メディア基盤 (MF) やシェル拡張・WinRT が残したスレッドが
        // プロセスを生かし続けると「ウィンドウは無いのに exe がロックされたまま」になる。
        // 正常なら OnExit 後すぐプロセスは消える (このスレッドは background なので
        // 終了を妨げない)。3 秒経っても生きていたら強制的に自決する。
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(3000);
            Environment.Exit(e.ApplicationExitCode);
        })
        { IsBackground = true, Name = "ExitWatchdog" };
        watchdog.Start();
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
            Set("AccentBrush", "#D0715A");
            Set("AccentSelectBrush", "#2BD0715A");
            Set("AccentSelectHoverBrush", "#3AD0715A");
            Set("AccentFocusBrush", "#33D0715A");
            Set("AccentCrumbHoverBrush", "#26D0715A");
            Set("AccentDropHighlightBrush", "#40D0715A");
            Set("AccentDropBorderBrush", "#B3D0715A");
            Set("DropCopyAccentBrush", "#7BC49A");
            Set("DropCopyTintBrush", "#337BC49A");
            Set("DropMoveAccentBrush", "#7FB2E5");
            Set("DropMoveTintBrush", "#337FB2E5");
            Set("WindowBackgroundBrush", "#262421");
            Set("SurfaceBrush", "#37342F");
            Set("CaptionBarBrush", "#161412");
            Set("CaptionBarMergeBrush", "#472D23");
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
            r["PaperTextureBrush"] = MakePaperTexture(Colors.White, mottle: 3, fine: 2.5, seed: 7);
        }
        else
        {
            // ライト (App.xaml の既定値と同じ)
            Set("AccentBrush", "#B4513A");
            Set("AccentSelectBrush", "#1AB4513A");
            Set("AccentSelectHoverBrush", "#26B4513A");
            Set("AccentFocusBrush", "#1EB4513A");
            Set("AccentCrumbHoverBrush", "#15B4513A");
            Set("AccentDropHighlightBrush", "#2EB4513A");
            Set("AccentDropBorderBrush", "#99B4513A");
            Set("DropCopyAccentBrush", "#2E7D50");
            Set("DropCopyTintBrush", "#1F2E7D50");
            Set("DropMoveAccentBrush", "#2B6CB0");
            Set("DropMoveTintBrush", "#1F2B6CB0");
            Set("WindowBackgroundBrush", "#FCFAF7");
            Set("SurfaceBrush", "#FFFFFE");
            Set("CaptionBarBrush", "#F1ECE2");
            Set("CaptionBarMergeBrush", "#E8CEBF");
            Set("BorderSoftBrush", "#ECE7DD");
            Set("WindowOuterBorderBrush", "#E3DCCF");
            Set("TextPrimaryBrush", "#2A2622");
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
            SetShadow("CardShadowBelow", "#3A2A18", 0.14, 3, 1.2);
            SetShadow("SubtleShadow", "#000000", 0.05, 9, 1);
            r["PaperTextureBrush"] = MakePaperTexture(Color.FromRgb(0x6B, 0x4E, 0x2A), mottle: 4.5, fine: 2.5, seed: 7);
        }
    }

    /// <summary>紙の地合い (ごく薄い雲状のムラ + 細かな粒) のタイル画像を作る。
    /// 目で「模様」と分かる濃さにはしない — 言われて初めて気づく程度でベタ塗り感だけを消す。
    /// 起動時・テーマ切替時に一度作るだけで、描画はタイル貼りのみ。</summary>
    /// <param name="ink">ムラの色 (明るい面には暗い色、暗い面には明るい色)。</param>
    /// <param name="mottle">雲状のムラの最大不透明度 (0-255)。</param>
    /// <param name="fine">細かな粒の最大不透明度 (0-255)。</param>
    private static ImageBrush MakePaperTexture(Color ink, double mottle, double fine, int seed)
    {
        const int size = 256;
        var rng = new Random(seed);
        // 2 オクターブの値ノイズ。格子を折り返して継ぎ目なくタイルできるようにする
        double[] Lattice(int cells)
        {
            var v = new double[cells * cells];
            for (var i = 0; i < v.Length; i++)
                v[i] = rng.NextDouble();
            return v;
        }
        static double Smooth(double t) => t * t * (3 - 2 * t);
        static double Sample(double[] lattice, int cells, int x, int y)
        {
            var fx = (double)x * cells / size;
            var fy = (double)y * cells / size;
            int x0 = (int)fx, y0 = (int)fy;
            double tx = Smooth(fx - x0), ty = Smooth(fy - y0);
            double At(int cx, int cy) => lattice[(cy % cells) * cells + (cx % cells)];
            var top = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * tx;
            var bottom = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * tx;
            return top + (bottom - top) * ty;
        }
        var coarse = Lattice(6);
        var medium = Lattice(24);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var cloud = Sample(coarse, 6, x, y) * 0.6 + Sample(medium, 24, x, y) * 0.4;
                var grain = rng.NextDouble();
                var a = (byte)Math.Clamp((int)Math.Round(cloud * mottle + grain * grain * fine), 0, 255);
                var i = (y * size + x) * 4;
                // Pbgra32 は乗算済みアルファ
                pixels[i + 0] = (byte)(ink.B * a / 255);
                pixels[i + 1] = (byte)(ink.G * a / 255);
                pixels[i + 2] = (byte)(ink.R * a / 255);
                pixels[i + 3] = a;
            }
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        brush.Freeze();
        return brush;
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

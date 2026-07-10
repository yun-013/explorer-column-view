using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ColumnView;

/// <summary>
/// スペースキーで開く装飾なしのプレビュー (macOS Finder の Quick Look 相当)。
/// タイトルバー・枠は持たず中身だけを角丸で表示する。ウィンドウはアクティブ化
/// しない (WS_EX_NOACTIVATE) ので、列のキーフォーカスは保たれ、選択を変えると
/// 追従してプレビューが切り替わる。
/// </summary>
public partial class QuickLookWindow : Window
{
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".cs", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".js", ".mjs", ".ts", ".tsx", ".jsx",
        ".py", ".rb", ".go", ".rs", ".php", ".swift", ".sh", ".bat", ".ps1", ".psm1", ".sql", ".r",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".toml", ".csv", ".tsv", ".gitignore",
        ".csproj", ".sln", ".props", ".targets", ".gradle", ".dockerfile", ".editorconfig",
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".opus",
    };

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>ウィンドウの上限 (モニター作業領域に対する比率)。Quick Look らしい控えめなサイズ。
    /// 縦長のコンテンツ (縦長 PDF・ポートレート写真/動画) が窮屈にならないよう、
    /// 高さ方向は幅より大きく取る (横長コンテンツの見た目はこれまでと変わらない)。</summary>
    private const double MaxWidthRatio = 0.62;
    private const double MaxHeightRatio = 0.85;

    // Segoe MDL2 Assets: 再生 / 一時停止 (既存 Glyphs と同じく ConvertFromUtf32 で ASCII ソースを保つ)
    private static readonly string GlyphPlay = char.ConvertFromUtf32(0xE768);
    private static readonly string GlyphPause = char.ConvertFromUtf32(0xE769);

    private readonly DispatcherTimer _mediaTimer;
    private readonly DispatcherTimer _hideBarTimer;
    private bool _seeking;
    private bool _isPlaying;
    private bool _mediaMode;

    public string? CurrentPath { get; private set; }

    public QuickLookWindow()
    {
        InitializeComponent();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaTimer.Tick += (_, _) => SyncSeek();
        _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideBarTimer.Tick += (_, _) => { _hideBarTimer.Stop(); FadeBar(false); };
        MouseMove += (_, _) => RevealBar();

        // handledEventsToo: true でないと ScrollViewer 等が内部でフォーカス処理のために
        // Handled にしてしまい (PDF/テキストのスクロール領域など)、ドラッグが始まらない。
        // 再生バーのボタン/スライダー・閉じるボタンはハンドラー内で個別に除外する。
        Card.AddHandler(MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Card_MouseLeftButtonDown), handledEventsToo: true);
        Card.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(Card_MouseLeftButtonUp), handledEventsToo: true);
        Card.AddHandler(MouseMoveEvent,
            new MouseEventHandler(Card_MouseMove), handledEventsToo: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 決してフォーカスを奪わない (列のキー操作を生かす) + タスク切替に出さない
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>連続で選択を変えたとき、遅れて完了した古い読み込みを捨てるための世代番号。</summary>
    private int _showGen;

    /// <summary>指定項目のプレビューを表示 (既に開いていれば中身だけ差し替え)。
    /// 重いデコード / サムネイル抽出はバックグラウンドで行い、UI を止めない。
    /// 読み込み完了までは直前の中身を出したままにする (矢印キー連打で白抜けしない)。</summary>
    public async void ShowFor(FileSystemItem item, Window owner)
    {
        CurrentPath = item.Path;
        int gen = ++_showGen;
        var path = item.Path;
        var ext = Path.GetExtension(path);

        // メディア再生中は先に止める (次の読み込み中に音が鳴り続けないように)
        if (_mediaMode)
            ResetViews();

        try
        {
            if (!item.IsDirectory && (AudioExts.Contains(ext) || ShellMetadata.IsMedia(item.Name)))
            {
                ResetViews();
                bool audio = AudioExts.Contains(ext);
                ShowMedia(path, owner, audio, gen);
                if (!audio)
                    return; // 動画は MediaOpened で実寸が分かってからサイズ確定・表示する
            }
            else if (!item.IsDirectory && ShellMetadata.IsImage(item.Name))
            {
                var bmp = await Task.Run(() => DecodeImage(path));
                if (gen != _showGen)
                    return;
                ResetViews();
                ImageView.Source = bmp;
                ImageView.Visibility = Visibility.Visible;
                double w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w <= 0 || h <= 0) { w = 640; h = 480; }
                FitToContent(w, h, owner);
            }
            else if (!item.IsDirectory && string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await ShowPdfAsync(path, gen, owner);
            }
            else if (!item.IsDirectory && (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase)))
            {
                await ShowMarkdownAsync(path, gen, owner);
            }
            else if (!item.IsDirectory && (TextExts.Contains(ext) || string.IsNullOrEmpty(ext)))
            {
                var text = await Task.Run(() => ReadTextHead(path));
                if (gen != _showGen)
                    return;
                if (text is null)
                    await ShowFallbackAsync(path, gen, owner); // バイナリだった
                else
                    ShowTextContent(text, owner);
            }
            else if (item.IsDirectory)
            {
                await ShowThumbnailAsync(path, gen, owner);
            }
            else
            {
                // 未知の拡張子: 本物のサムネイル → 中身がテキストならテキスト → アイコン
                await ShowFallbackAsync(path, gen, owner);
            }
        }
        catch
        {
            if (gen != _showGen)
                return;
            try { await ShowThumbnailAsync(path, gen, owner); } catch { }
        }

        if (gen == _showGen && !IsVisible)
            Show();
    }

    private void ResetViews()
    {
        _mediaTimer.Stop();
        _hideBarTimer.Stop();
        _mediaMode = false;
        StopMedia();
        ImageView.Visibility = Visibility.Collapsed;
        ImageView.Source = null;
        ImageView.Stretch = Stretch.Uniform;
        TextScroll.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        DocView.Visibility = Visibility.Collapsed;
        DocView.Document = null;
        PdfScroll.Visibility = Visibility.Collapsed;
        PdfPages.Children.Clear();
        MediaView.Visibility = Visibility.Collapsed;
        AudioGlyph.Visibility = Visibility.Collapsed;
        MediaBar.Visibility = Visibility.Collapsed;
        MediaBar.Opacity = 0;
    }

    // ---- 種類ごとの表示 ----

    /// <summary>画像をワーカースレッドでデコードする (Freeze して UI へ渡す)。</summary>
    private static BitmapImage DecodeImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        // 巨大画像でメモリを食い過ぎないよう、表示上限に合わせてデコード
        bmp.DecodePixelWidth = (int)Math.Min(SystemParameters.WorkArea.Width * MaxWidthRatio * 1.5, 1920);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>テキストの先頭を読む。バイナリらしければ null (フォールバック表示へ回す)。</summary>
    private static string? ReadTextHead(string path)
    {
        string text;
        using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[256 * 1024];
            int read = reader.Read(buffer, 0, buffer.Length);
            text = new string(buffer, 0, read);
            if (!reader.EndOfStream)
                text += "\n\n… (以降は省略)";
        }
        // NUL が多いバイナリはテキスト扱いしない
        if (text.Length > 0 && text.Count(c => c == '\0') > text.Length / 64)
            return null;
        return text;
    }

    private void ShowTextContent(string text, Window owner)
    {
        ResetViews();
        TextView.Text = text;
        TextScroll.Visibility = Visibility.Visible;
        FitToContent(640, 520, owner);
    }

    // ---- メディア (動画・音声) ----

    private int _mediaShowGen;
    private Window? _mediaOwner;
    private bool _mediaAudio;

    private void ShowMedia(string path, Window owner, bool audio, int gen)
    {
        _mediaMode = true;
        _mediaShowGen = gen;
        _mediaOwner = owner;
        _mediaAudio = audio;
        MediaView.Source = new Uri(path);
        // 音声でも MediaElement は表示のまま (Collapsed だと再生されない)。
        // 映像の無い音声は AudioGlyph を上に重ねて見た目を整える。
        MediaView.Visibility = Visibility.Visible;
        AudioGlyph.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        if (audio)
        {
            AudioName.Text = Path.GetFileName(path);
            MediaBar.Visibility = Visibility.Visible;
            FitToContent(420, 260, owner);
            RevealBar();
        }
        // 動画はここではサイズを決めない。MediaOpened で実寸が分かってから
        // サイズ確定→表示することで「小さく開いてから広がる」ガタつきを無くす
        MediaView.Play();
        _isPlaying = true;
        PlayPause.Content = GlyphPause;
        _mediaTimer.Start();
    }

    private void MediaView_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_mediaShowGen != _showGen)
            return;
        if (MediaView.NaturalDuration.HasTimeSpan)
            Seek.Maximum = MediaView.NaturalDuration.TimeSpan.TotalSeconds;
        if (!_mediaAudio)
        {
            double w = MediaView.NaturalVideoWidth > 0 ? MediaView.NaturalVideoWidth : 720;
            double h = MediaView.NaturalVideoHeight > 0 ? MediaView.NaturalVideoHeight : 420;
            FitToContent(w, h, _mediaOwner ?? Application.Current.MainWindow ?? this);
            MediaBar.Visibility = Visibility.Visible;
            RevealBar();
            if (!IsVisible)
                Show();
        }
        SyncSeek();
    }

    private async void MediaView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 再生できない形式はサムネイル / アイコンにフォールバック
        if (_mediaShowGen != _showGen || CurrentPath is null)
            return;
        var owner = _mediaOwner ?? Application.Current.MainWindow ?? this;
        try { await ShowFallbackAsync(CurrentPath, _showGen, owner); } catch { }
        if (!IsVisible)
            Show();
    }

    // ---- PDF (Windows 内蔵の PDF エンジンでページを実レンダリング) ----

    private const int PdfMaxPages = 8;
    private const uint PdfRenderWidth = 960;

    private async Task ShowPdfAsync(string path, int gen, Window owner)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var doc = await PdfDocument.LoadFromFileAsync(file);
        if (gen != _showGen)
            return;
        if (doc.PageCount == 0)
        {
            await ShowFallbackAsync(path, gen, owner);
            return;
        }

        int count = (int)Math.Min(doc.PageCount, PdfMaxPages);
        for (int i = 0; i < count; i++)
        {
            BitmapImage img;
            double pageW, pageH;
            using (var page = doc.GetPage((uint)i))
            {
                pageW = page.Size.Width;
                pageH = page.Size.Height;
                img = await RenderPdfPageAsync(page);
            }
            if (gen != _showGen)
                return;

            if (i == 0)
            {
                // 1 ページ目が届いた時点で表示を切り替え、ウィンドウをページ比率に合わせる
                ResetViews();
                PdfScroll.Visibility = Visibility.Visible;
                FitToContent(pageW, pageH * 1.02, owner);
                if (!IsVisible)
                    Show();
            }
            PdfPages.Children.Add(new Image
            {
                Source = img,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (gen == _showGen && doc.PageCount > (uint)count)
        {
            PdfPages.Children.Add(new TextBlock
            {
                Text = $"… 全 {doc.PageCount} ページ中 {count} ページを表示",
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private static async Task<BitmapImage> RenderPdfPageAsync(PdfPage page)
    {
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = PdfRenderWidth });
        ras.Seek(0);
        using var ms = new MemoryStream();
        await ras.AsStreamForRead().CopyToAsync(ms);
        return await Task.Run(() =>
        {
            // OnLoad なので EndInit 時点でデコード済み。ストリームは using で解放される
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        });
    }

    // ---- Markdown (簡易レンダリング) ----

    private async Task ShowMarkdownAsync(string path, int gen, Window owner)
    {
        var text = await Task.Run(() => ReadTextHead(path));
        if (gen != _showGen)
            return;
        if (text is null)
        {
            await ShowFallbackAsync(path, gen, owner);
            return;
        }
        try
        {
            var doc = MarkdownLite.Render(text);
            ResetViews();
            DocView.Document = doc;
            DocView.Visibility = Visibility.Visible;
            FitToContent(680, 560, owner);
        }
        catch
        {
            // 解析に失敗したら生テキストで見せる
            ShowTextContent(text, owner);
        }
    }

    // ---- フォールバック (未知の拡張子・バイナリ) ----

    /// <summary>本物のサムネイル → 中身がテキストならテキスト → アイコン、の順で試す。</summary>
    private async Task ShowFallbackAsync(string path, int gen, Window owner)
    {
        long stamp = 0;
        try { stamp = new FileInfo(path).LastWriteTimeUtc.Ticks; } catch { }

        // 1) サムネイルハンドラが本物の絵を出せる型 (SVG 等) はそれを表示
        var thumb = await Task.Run(() =>
            ShellThumbnail.Get(path, 1024, stamp, allowDownload: true, thumbnailOnly: true));
        if (gen != _showGen)
            return;
        if (thumb is not null)
        {
            ResetViews();
            ImageView.Source = thumb;
            ImageView.Visibility = Visibility.Visible;
            FitToContent(thumb.Width > 1 ? thumb.Width : 560,
                         thumb.Height > 1 ? thumb.Height : 440, owner);
            return;
        }

        // 2) 中身がテキストならテキスト表示 (.sub 等の未知のテキスト形式)
        string? text = null;
        try { text = await Task.Run(() => ReadTextHead(path)); } catch { }
        if (gen != _showGen)
            return;
        if (text is not null)
        {
            ShowTextContent(text, owner);
            return;
        }

        // 3) どうにもならないバイナリはアイコンを等倍で
        var icon = await Task.Run(() =>
            ShellThumbnail.Get(path, 256, stamp, allowDownload: true) ?? IconCache.GetByPath(path));
        if (gen != _showGen)
            return;
        ResetViews();
        ImageView.Source = icon;
        ImageView.Stretch = Stretch.None;
        ImageView.Visibility = Visibility.Visible;
        FitToContent(400, 320, owner);
    }

    /// <summary>シェルのサムネイル or アイコン (フォルダ・例外時のフォールバック) を非同期で表示。</summary>
    private async Task ShowThumbnailAsync(string path, int gen, Window owner)
    {
        var img = await Task.Run(() =>
        {
            long stamp = 0;
            try { stamp = new FileInfo(path).LastWriteTimeUtc.Ticks; } catch { }
            return ShellThumbnail.Get(path, 1024, stamp, allowDownload: true)
                   ?? IconCache.GetByPath(path);
        });
        if (gen != _showGen)
            return;
        ResetViews();
        ImageView.Source = img;
        ImageView.Visibility = Visibility.Visible;
        double w = 560, h = 440;
        if (img is not null && img.Width > 1 && img.Height > 1)
        {
            w = img.Width; h = img.Height;
        }
        FitToContent(w, h, owner);
    }

    // ---- サイズ・配置 ----

    private void FitToContent(double contentW, double contentH, Window owner)
    {
        var area = GetOwnerMonitorWorkArea(owner);
        double maxW = area.Width * MaxWidthRatio, maxH = area.Height * MaxHeightRatio;
        double scale = Math.Min(1.0, Math.Min(maxW / contentW, maxH / contentH));
        double w = Math.Max(360, contentW * scale);
        double h = Math.Max(240, contentH * scale);

        // Card の Margin(18) 分の余白を足す
        Width = w + 36;
        Height = h + 36;

        // アプリが開いているディスプレイの中央に開く (ウィンドウ位置ではなくモニター基準)
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>owner ウィンドウが乗っているモニターの作業領域を WPF 単位 (DIP) で返す。
    /// SystemParameters.WorkArea はプライマリモニター固定のため、マルチモニター環境で
    /// 別ディスプレイにアプリを出している場合に備えて実際のモニターを判定する。</summary>
    private static Rect GetOwnerMonitorWorkArea(Window owner)
    {
        try
        {
            var hwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    var dpi = VisualTreeHelper.GetDpi(owner);
                    return new Rect(
                        mi.rcWork.left / dpi.DpiScaleX,
                        mi.rcWork.top / dpi.DpiScaleY,
                        (mi.rcWork.right - mi.rcWork.left) / dpi.DpiScaleX,
                        (mi.rcWork.bottom - mi.rcWork.top) / dpi.DpiScaleY);
                }
            }
        }
        catch { /* 取得できなければ既定 (プライマリ) にフォールバック */ }
        var fallback = SystemParameters.WorkArea;
        return new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 中身を角丸でクリップする (子要素は CornerRadius に追従しないため)
        Host.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
    }

    // ---- ウィンドウのドラッグ移動 (手動) ----
    // Window.DragMove() は Windows のモーダル移動ループ (SC_MOVE) に入るが、
    // このウィンドウは非アクティブ (WS_EX_NOACTIVATE) なため、その組み合わせは
    // メッセージポンプを膠着させてフリーズを招くことがある。モーダルループを
    // 一切使わず、マウスをキャプチャして自前で座標を動かす方式にする。
    private bool _dragging;
    private POINT _dragStartCursor;
    private double _dragStartLeft, _dragStartTop;

    /// <summary>プレビューを掴んで移動を開始する。handledEventsToo で登録しているため、
    /// PDF/テキストの ScrollViewer が Handled にしても確実にここまで届く。再生バーと
    /// 閉じるボタンの上だけは、それぞれの操作を優先して除外する。</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        // このウィンドウは決してアクティブにならない (WS_EX_NOACTIVATE) ので、
        // 他アプリを使った後にプレビューをクリックしてもキーボードフォーカスは
        // 他アプリに残ったままになる。クリックでメインウィンドウを明示的に
        // アクティブ化し、Space / Esc / ↑↓ が再び効くようにする
        ActivateOwnerAndFocusColumn();

        if (e.OriginalSource is DependencyObject src
            && (IsDescendantOf(src, MediaBar) || IsDescendantOf(src, CloseButton)))
            return;
        if (!GetCursorPos(out _dragStartCursor))
            return;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragging = true;
        Card.CaptureMouse();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragging)
                EndDrag(); // ボタンが既に離れていた (キャプチャ外での解放など)
            return;
        }
        if (!GetCursorPos(out var now))
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartLeft + (now.X - _dragStartCursor.X) / dpi.DpiScaleX;
        Top = _dragStartTop + (now.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        Card.ReleaseMouseCapture();
    }

    /// <summary>node が ancestor 自身か、その (視覚 / 論理) 子孫かどうか。</summary>
    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        CloseButton.Opacity = 1;
        CloseButton.IsHitTestVisible = true;
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseButton.Opacity = 0;
        CloseButton.IsHitTestVisible = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseQuickLook();
        ActivateOwnerAndFocusColumn();
    }

    /// <summary>プレビューにフォーカスが渡ってしまった場合の保険。Space / Esc で必ず閉じる。</summary>
    private void QuickLookWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Escape)
        {
            e.Handled = true;
            CloseQuickLook();
            ActivateOwnerAndFocusColumn();
        }
    }

    /// <summary>メインウィンドウをアクティブ化し、選択中の列にキーボードフォーカスを戻す。
    /// これをしないと「もう一度プレビューするにはファイルをクリックし直す」羽目になる。</summary>
    private void ActivateOwnerAndFocusColumn()
    {
        try
        {
            var owner = Owner ?? Application.Current.MainWindow;
            owner?.Activate();
            (owner as MainWindow)?.FocusSelectionColumn();
        }
        catch { /* シャットダウン中などは無視 */ }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ---- メディア操作 ----

    private void StopMedia()
    {
        try { MediaView.Stop(); } catch { }
        MediaView.Source = null;
        _isPlaying = false;
    }

    private void MediaView_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            MediaView.Position = TimeSpan.Zero;
            MediaView.Pause();
        }
        catch { /* メディアが既にアンロードされていたら無視 */ }
        _isPlaying = false;
        PlayPause.Content = GlyphPlay;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isPlaying) { MediaView.Pause(); PlayPause.Content = GlyphPlay; }
            else { MediaView.Play(); PlayPause.Content = GlyphPause; }
            _isPlaying = !_isPlaying;
        }
        catch { /* メディアが既にアンロードされていたら無視 */ }
    }

    private void SyncSeek()
    {
        if (_seeking || !MediaView.NaturalDuration.HasTimeSpan)
            return;
        // 変化のないフレームでは UI を触らない (タイマー起因の無駄な再描画を抑える)
        var pos = MediaView.Position;
        var next = pos.TotalSeconds;
        if (Math.Abs(Seek.Value - next) > 0.05)
            Seek.Value = next;
        var cur = Fmt(pos);
        if (TimeCur.Text != cur)
            TimeCur.Text = cur;
        var total = " / " + Fmt(MediaView.NaturalDuration.TimeSpan);
        if (TimeTotal.Text != total)
            TimeTotal.Text = total;
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                          : $"{t.Minutes}:{t.Seconds:00}";

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SetMediaPosition(Seek.Value);
        _seeking = false;
    }

    /// <summary>バーの任意の位置をクリックしたらそこへ即ジャンプする。
    /// Slider 標準の LargeChange (1 秒) 頼みだと「飛ばない」ように見えるため、
    /// クリック座標から値を直接計算する。つまみ自体のドラッグはそのまま生かす。</summary>
    private void Seek_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInThumb(src))
            return; // つまみの上はドラッグ開始に任せる
        if (Seek.Template.FindName("PART_Track", Seek) is not Track track)
            return;
        var value = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), Seek.Minimum, Seek.Maximum);
        Seek.Value = value;
        SetMediaPosition(value);
        e.Handled = true;
    }

    private static bool IsInThumb(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Thumb)
                return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void SetMediaPosition(double seconds)
    {
        try
        {
            if (MediaView.Source is not null)
                MediaView.Position = TimeSpan.FromSeconds(seconds);
        }
        catch { /* アンロード直後のシークは無視 */ }
    }

    // ---- ホバーで操作バーを出す ----

    private void RevealBar()
    {
        if (!_mediaMode)
            return;
        FadeBar(true);
        _hideBarTimer.Stop();
        _hideBarTimer.Start();
    }

    private void FadeBar(bool show)
    {
        MediaBar.BeginAnimation(OpacityProperty, null);
        MediaBar.Opacity = show ? 1 : 0;
    }

    /// <summary>プレビューを閉じる (メディアを止めて隠す)。進行中の読み込みも無効化する。</summary>
    public void CloseQuickLook()
    {
        CurrentPath = null;
        _showGen++; // 遅れて完了した読み込みが再表示しないように
        _mediaTimer.Stop();
        StopMedia();
        Hide();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

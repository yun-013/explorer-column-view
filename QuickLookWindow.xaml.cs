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
        ".txt", ".log", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
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

    // Segoe MDL2 Assets: 再生 / 一時停止 (ソースを ASCII に保つため \u エスケープ)
    private const string GlyphPlay = "";
    private const string GlyphPause = "";

    private readonly DispatcherTimer _mediaTimer;
    private readonly DispatcherTimer _hideBarTimer;
    private bool _seeking;
    private bool _isPlaying;
    private bool _mediaMode;

    public string? CurrentPath { get; private set; }

    public QuickLookWindow()
    {
        InitializeComponent();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _mediaTimer.Tick += (_, _) => SyncSeek();
        _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideBarTimer.Tick += (_, _) => { _hideBarTimer.Stop(); FadeBar(false); };
        MouseMove += (_, _) => RevealBar();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 決してフォーカスを奪わない (列のキー操作を生かす) + タスク切替に出さない
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>指定項目のプレビューを表示 (既に開いていれば中身だけ差し替え)。</summary>
    public void ShowFor(FileSystemItem item, Window owner)
    {
        CurrentPath = item.Path;
        ResetViews();

        var ext = Path.GetExtension(item.Path);
        try
        {
            if (item.IsDirectory)
                ShowThumbnail(item.Path);
            else if (ShellMetadata.IsImage(item.Name))
                ShowImage(item.Path, owner);
            else if (AudioExts.Contains(ext))
                ShowMedia(item.Path, owner, audio: true);
            else if (ShellMetadata.IsMedia(item.Name))
                ShowMedia(item.Path, owner, audio: false);
            else if (TextExts.Contains(ext) || string.IsNullOrEmpty(ext))
                ShowText(item.Path, item.Name, owner);
            else
                ShowThumbnail(item.Path);
        }
        catch
        {
            ShowThumbnail(item.Path);
        }

        if (!IsVisible)
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
        TextScroll.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        MediaView.Visibility = Visibility.Collapsed;
        AudioGlyph.Visibility = Visibility.Collapsed;
        MediaBar.Visibility = Visibility.Collapsed;
        MediaBar.Opacity = 0;
    }

    // ---- 種類ごとの表示 ----

    private void ShowImage(string path, Window owner)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        // 巨大画像でメモリを食い過ぎないよう、画面幅を上限にデコード
        bmp.DecodePixelWidth = (int)Math.Min(SystemParameters.WorkArea.Width * 1.5, 2560);
        bmp.EndInit();
        bmp.Freeze();

        ImageView.Source = bmp;
        ImageView.Visibility = Visibility.Visible;
        double w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w <= 0 || h <= 0) { w = 640; h = 480; }
        FitToContent(w, h, owner);
    }

    private void ShowText(string path, string name, Window owner)
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
        {
            ShowThumbnail(path);
            return;
        }
        TextView.Text = text;
        TextScroll.Visibility = Visibility.Visible;
        FitToContent(760, 620, owner);
    }

    private void ShowMedia(string path, Window owner, bool audio)
    {
        _mediaMode = true;
        MediaView.Source = new Uri(path);
        // 音声でも MediaElement は表示のまま (Collapsed だと再生されない)。
        // 映像の無い音声は AudioGlyph を上に重ねて見た目を整える。
        MediaView.Visibility = Visibility.Visible;
        AudioGlyph.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        if (audio)
            AudioName.Text = Path.GetFileName(path);
        MediaBar.Visibility = Visibility.Visible;
        FitToContent(audio ? 460 : 720, audio ? 300 : 460, owner);
        MediaView.Play();
        _isPlaying = true;
        PlayPause.Content = GlyphPause; // 一時停止中
        _mediaTimer.Start();
        RevealBar();
    }

    private void ShowThumbnail(string path)
    {
        long stamp = 0;
        try { stamp = new FileInfo(path).LastWriteTimeUtc.Ticks; } catch { }
        var img = ShellThumbnail.Get(path, 1024, stamp, allowDownload: true)
                  ?? IconCache.GetByPath(path);
        ImageView.Source = img;
        ImageView.Visibility = Visibility.Visible;
        double w = 720, h = 560;
        if (img is not null && img.Width > 1 && img.Height > 1)
        {
            w = img.Width; h = img.Height;
        }
        FitToContent(w, h, Application.Current.MainWindow ?? this);
    }

    // ---- サイズ・配置 ----

    private void FitToContent(double contentW, double contentH, Window owner)
    {
        var area = SystemParameters.WorkArea;
        double maxW = area.Width * 0.82, maxH = area.Height * 0.82;
        double scale = Math.Min(1.0, Math.Min(maxW / contentW, maxH / contentH));
        double w = Math.Max(360, contentW * scale);
        double h = Math.Max(240, contentH * scale);

        // Card の Margin(18) 分の余白を足す
        Width = w + 36;
        Height = h + 36;

        double ownerCx = owner.Left + owner.Width / 2;
        double ownerCy = owner.Top + owner.Height / 2;
        if (double.IsNaN(ownerCx)) { ownerCx = area.Left + area.Width / 2; ownerCy = area.Top + area.Height / 2; }
        Left = Math.Clamp(ownerCx - Width / 2, area.Left, area.Right - Width);
        Top = Math.Clamp(ownerCy - Height / 2, area.Top, area.Bottom - Height);
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 中身を角丸でクリップする (子要素は CornerRadius に追従しないため)
        Host.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
    }

    // ---- メディア操作 ----

    private void StopMedia()
    {
        try { MediaView.Stop(); } catch { }
        MediaView.Source = null;
        _isPlaying = false;
    }

    private void MediaView_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (MediaView.NaturalDuration.HasTimeSpan)
            Seek.Maximum = MediaView.NaturalDuration.TimeSpan.TotalSeconds;
        // 動画は実アスペクト比に合わせて開き直す
        if (MediaView.NaturalVideoWidth > 0 && MediaView.NaturalVideoHeight > 0)
            FitToContent(MediaView.NaturalVideoWidth, MediaView.NaturalVideoHeight,
                Application.Current.MainWindow ?? this);
        SyncSeek();
    }

    private void MediaView_MediaEnded(object sender, RoutedEventArgs e)
    {
        MediaView.Position = TimeSpan.Zero;
        MediaView.Pause();
        _isPlaying = false;
        PlayPause.Content = GlyphPlay; // 停止/終了
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { MediaView.Pause(); PlayPause.Content = GlyphPlay; }
        else { MediaView.Play(); PlayPause.Content = GlyphPause; }
        _isPlaying = !_isPlaying;
    }

    private void SyncSeek()
    {
        if (_seeking || !MediaView.NaturalDuration.HasTimeSpan)
            return;
        Seek.Value = MediaView.Position.TotalSeconds;
        TimeText.Text = $"{Fmt(MediaView.Position)} / {Fmt(MediaView.NaturalDuration.TimeSpan)}";
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                          : $"{t.Minutes}:{t.Seconds:00}";

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        MediaView.Position = TimeSpan.FromSeconds(Seek.Value);
        _seeking = false;
    }

    private void Seek_Clicked(object sender, MouseButtonEventArgs e)
        => MediaView.Position = TimeSpan.FromSeconds(Seek.Value);

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

    /// <summary>プレビューを閉じる (メディアを止めて隠す)。</summary>
    public void CloseQuickLook()
    {
        CurrentPath = null;
        _mediaTimer.Stop();
        StopMedia();
        Hide();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

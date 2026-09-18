using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ColumnView;

/// <summary>
/// エクスプローラー互換のファイル クリップボード操作。
/// CF_HDROP (FileDrop) に "Preferred DropEffect" を添えることで、
/// コピー / 切り取りの区別がエクスプローラーと相互に通じる。
/// </summary>
public static class ClipboardOps
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 1; // DROPEFFECT_COPY
    private const int DropEffectMove = 2; // DROPEFFECT_MOVE

    /// <summary>ファイル群をクリップボードへ載せる。cut=true で「切り取り」。</summary>
    public static bool SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths)
            list.Add(p);

        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(PreferredDropEffect,
            new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));
        return SetData(data);
    }

    /// <summary>テキストをクリップボードへ載せる (パスのコピー用)。</summary>
    public static bool SetText(string text)
        => SetData(new DataObject(DataFormats.UnicodeText, text));

    // ---- 載せる / フラッシュ ----
    //
    // WPF の Clipboard.SetDataObject(data, copy: true) は OleSetClipboard の直後に
    // OleFlushClipboard を呼び、失敗すると Thread.Sleep(100) で最大 10 回再試行する。
    // ところが OleSetClipboard 直後はクリップボード監視アプリ (履歴・Seer 等) が
    // 中身を読みに来て、遅延レンダリングの WM_RENDERFORMAT を「このスレッド」に
    // SendMessage してくる。Sleep 中はそれを処理できないので相手はクリップボードを
    // 開いたまま待ち、こちらは開けずに待つ → 1 秒後に例外、という膠着が頻発していた
    // (しかもデータ自体はすでに載っているのに失敗扱いになる)。
    // そこで OLE を直接呼び、待つ間は送られてきたメッセージを処理し、
    // フラッシュは同期で粘らずディスパッチャーで後から行う。

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IComDataObject? pDataObj);

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    [DllImport("ole32.dll")]
    private static extern int OleIsCurrentClipboard(IComDataObject pDataObj);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, nint pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern nint GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint QS_SENDMESSAGE = 0x0040;
    private const uint PM_QS_SENDMESSAGE = QS_SENDMESSAGE << 16; // PM_NOREMOVE | 送信メッセージのみ処理

    /// <summary>OleSetClipboard 済みでまだフラッシュしていないデータ (遅延レンダリング中)。</summary>
    private static DataObject? _pending;
    private static DispatcherTimer? _flushTimer;
    private static int _flushTries;

    /// <summary>直近の失敗時にクリップボードを開いていたプロセス名 (分かれば)。</summary>
    public static string? LastBlocker { get; private set; }

    private static bool SetData(DataObject data)
    {
        LastBlocker = null;
        if (!Retry(() => OleSetClipboard(data), 1500))
        {
            LastBlocker = FindBlocker();
            return false;
        }

        // 載った。実データ化 (フラッシュ) は一度だけ試し、ダメなら後で
        _pending = data;
        if (OleFlushClipboard() >= 0)
            _pending = null;
        else
            ScheduleFlush();
        return true;
    }

    /// <summary>
    /// op が成功するまで再試行する。待つ間は他プロセスから送られたメッセージ
    /// (遅延レンダリング要求など) を処理し、相手を待たせっぱなしにしない。
    /// </summary>
    private static bool Retry(Func<int> op, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (op() >= 0)
                return true;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return false;
            MsgWaitForMultipleObjectsEx(0, 0, 20, QS_SENDMESSAGE, 0);
            PeekMessage(out _, 0, 0, 0, PM_QS_SENDMESSAGE);
        }
    }

    private static void ScheduleFlush()
    {
        _flushTries = 0;
        if (_flushTimer is null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _flushTimer.Tick += (_, _) =>
            {
                if (TryFlushPending() || ++_flushTries >= 40)
                    _flushTimer.Stop();
            };
        }
        _flushTimer.Stop();
        _flushTimer.Start();
    }

    /// <summary>保留中のデータをフラッシュする。もう不要 (完了 / 他アプリが上書き) なら true。</summary>
    private static bool TryFlushPending()
    {
        if (_pending is null)
            return true;
        if (OleIsCurrentClipboard(_pending) != 0) // S_FALSE = もう自分の中身ではない
        {
            _pending = null;
            return true;
        }
        if (OleFlushClipboard() < 0)
            return false;
        _pending = null;
        return true;
    }

    /// <summary>
    /// 終了時に呼ぶ。フラッシュしないままプロセスが消えると、
    /// コピーした内容がクリップボードから失われる。
    /// </summary>
    public static void FlushOnExit()
    {
        _flushTimer?.Stop();
        if (_pending is null)
            return;
        Retry(() => TryFlushPending() ? 0 : -1, 1000);
    }

    private static string? FindBlocker()
    {
        try
        {
            var hwnd = GetOpenClipboardWindow();
            if (hwnd == 0)
                return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>クリップボードのファイル一覧を取り出す。cut=true なら切り取り (貼り付け = 移動)。</summary>
    public static string[]? GetFiles(out bool cut)
    {
        cut = false;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
                return null;
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
                return null;

            if (data.GetDataPresent(PreferredDropEffect)
                && data.GetData(PreferredDropEffect) is MemoryStream ms)
            {
                var buf = new byte[4];
                if (ms.Read(buf, 0, 4) == 4)
                    cut = (BitConverter.ToInt32(buf, 0) & DropEffectMove) != 0;
            }
            return files;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>切り取りの貼り付け後に呼ぶ (二度目の移動は元ファイルが無く失敗するため)。</summary>
    public static void ClearAfterMove()
    {
        _pending = null;
        _flushTimer?.Stop();
        // 占有中で空にできなくても無視 (次の貼り付けが失敗するだけ)
        Retry(() => OleSetClipboard(null), 500);
    }
}

/// <summary>
/// 「いまクリップボードに載せた項目」の視覚マーク (切り取り = 半透明 / コピー = バッジ)。
/// クリップボードが外部で書き換わったら解除する (WM_CLIPBOARDUPDATE で照合)。
/// </summary>
public static class ClipboardMarks
{
    private static readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsCut { get; private set; }
    public static bool IsEmpty => _paths.Count == 0;

    public static void Set(IEnumerable<string> paths, bool cut)
    {
        _paths.Clear();
        foreach (var p in paths)
            _paths.Add(p);
        IsCut = cut;
    }

    public static void Clear() => _paths.Clear();

    public static ClipboardMarkKind MarkFor(string path)
        => _paths.Count > 0 && _paths.Contains(path)
            ? (IsCut ? ClipboardMarkKind.Cut : ClipboardMarkKind.Copied)
            : ClipboardMarkKind.None;

    /// <summary>クリップボードの中身がこのマークと一致しているか (自アプリの書き込み判定)。</summary>
    public static bool Matches(string[]? files, bool cut)
        => files is not null
            && cut == IsCut
            && files.Length == _paths.Count
            && files.All(_paths.Contains);
}

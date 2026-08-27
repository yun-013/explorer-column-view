using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

namespace ColumnView;

/// <summary>
/// UI スレッドが固まった瞬間を記録する見張り番。
/// 「時折固まる」という再現しづらい症状を、後から原因の特定できるログに変える。
/// 常用ビルドに入れっぱなしにできるよう、平常時のコストは
/// 「Dispatcher の処理開始/終了でタイムスタンプを1つ書く」だけに抑えてある
/// (デリゲート名の解決は停止を検出したときにだけ行う)。
/// </summary>
internal static class StallWatch
{
    /// <summary>これ以上 UI が応答しなければ体感で「固まった」と感じる閾値。</summary>
    private const int StallMs = 500;

    private static readonly FieldInfo? MethodField =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

    private static volatile DispatcherOperation? _running;
    private static long _startedAt;      // Stopwatch ticks
    private static volatile bool _reported;   // 同じ停止を何度も書かない
    private static string _logPath = "";
    private static Dispatcher? _ui;

    public static void Start()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColumnView");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "stalls.log");

            _ui = Dispatcher.CurrentDispatcher;
            _ui.Hooks.OperationStarted += OnStarted;
            _ui.Hooks.OperationCompleted += OnFinished;
            _ui.Hooks.OperationAborted += OnFinished;

            var t = new Thread(Watch)
            {
                IsBackground = true,
                Name = "StallWatch",
                Priority = ThreadPriority.AboveNormal, // 固まっている最中でも自分は動けるように
            };
            t.Start();
        }
        catch
        {
            // 監視の失敗でアプリを壊さない
        }
    }

    private static void OnStarted(object? sender, DispatcherHookEventArgs e)
    {
        _startedAt = Stopwatch.GetTimestamp();
        _running = e.Operation;
        _reported = false;
    }

    private static void OnFinished(object? sender, DispatcherHookEventArgs e)
    {
        if (_reported && _running is not null)
            Log($"  -> 復帰: {Elapsed()}ms で完了");
        _running = null;
    }

    private static long Elapsed()
        => (Stopwatch.GetTimestamp() - _startedAt) * 1000 / Stopwatch.Frequency;

    private static void Watch()
    {
        var tick = 0;
        while (true)
        {
            Thread.Sleep(200);
            try
            {
                if (++tick % 5 == 0)
                    Ping();

                var op = _running;
                if (op is null || _reported)
                    continue;
                var ms = Elapsed();
                if (ms < StallMs)
                    continue;

                _reported = true;
                Log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UIスレッド停止 {ms}ms: {Describe(op)}");
                LogPendingCounts();
            }
            catch
            {
                // 監視自身は決して落とさない
            }
        }
    }

    /// <summary>Dispatcher に往復させて実際の応答時間を測る。
    /// ネイティブのモーダルループ (シェルのコンテキストメニュー等) で固まった場合は
    /// Dispatcher の処理が「開始」すらしないため、フックだけでは検出できない。</summary>
    private static void Ping()
    {
        var t0 = Stopwatch.GetTimestamp();
        var done = _pingDone;
        done.Reset();
        _ui?.BeginInvoke(DispatcherPriority.Background, PingBack);
        if (done.Wait(10000))
        {
            var ms = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
            if (ms >= StallMs)
                Log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI応答遅延 {ms}ms (実行中: {DescribeCurrent()})");
        }
        else
        {
            Log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI応答なし 10秒超 (実行中: {DescribeCurrent()})");
        }
    }

    private static readonly ManualResetEventSlim _pingDone = new(false);
    private static readonly Action PingBack = () => _pingDone.Set();

    private static string DescribeCurrent()
    {
        var op = _running;
        return op is null ? "なし=ネイティブ側で停止の可能性" : Describe(op);
    }

    /// <summary>Dispatcher に溜まっている処理の量 (詰まり具合の目安)。</summary>
    private static void LogPendingCounts()
    {
        try
        {
            var q = _ui?.GetType()
                .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_ui);
            var n = q?.GetType()
                .GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(q);
            if (n is not null)
                Log($"  -> 待ち行列: {n} 件");
        }
        catch { }
    }

    /// <summary>停止を検出したときだけ呼ぶ (リフレクションは高価なので平常時は使わない)。</summary>
    private static string Describe(DispatcherOperation op)
    {
        try
        {
            if (MethodField?.GetValue(op) is Delegate d)
            {
                var t = d.Method.DeclaringType;
                var target = d.Target?.GetType().Name;
                return $"{t?.FullName}.{d.Method.Name}" + (target is null ? "" : $" [target={target}]");
            }
        }
        catch { }
        return "(不明)";
    }

    /// <summary>ログ1ファイルの上限。超えたら 1 世代だけ退避して作り直す。
    /// 固まりやすい環境ほどログが増えるので、上限が無いと際限なく育つ。</summary>
    private const long MaxLogBytes = 512 * 1024;

    private static readonly object LogLock = new();
    private static readonly System.Text.UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private static void Log(string line)
    {
        try
        {
            lock (LogLock)
            {
                var f = new FileInfo(_logPath);
                if (f.Exists && f.Length > MaxLogBytes)
                {
                    var old = _logPath + ".old";
                    File.Delete(old);
                    File.Move(_logPath, old);
                }
                // BOM 付きで書く: メモ帳等で開いたときに日本語が化けないようにする
                File.AppendAllText(_logPath, line + Environment.NewLine, Utf8Bom);
            }
        }
        catch
        {
            // ログの失敗でアプリを壊さない
        }
    }
}

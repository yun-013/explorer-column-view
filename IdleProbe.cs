using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ColumnView;

/// <summary>
/// 【一時的な調査用】アイドル時 CPU 消費の発生源を突き止めるための計測プローブ。
/// 環境変数 COLUMNVIEW_IDLE_PROBE=1 のときだけ動く。原因特定後に削除する。
/// </summary>
internal static class IdleProbe
{
    private static int _renderTicks;
    private static int _layoutUpdates;
    private static readonly ConcurrentDictionary<string, int> Posted = new();
    private static readonly ConcurrentDictionary<string, string> Stacks = new();
    private static readonly HashSet<string> StackDumped = new();
    private static readonly FieldInfo? MethodField =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static string _logPath = "";

    public static void StartIfRequested()
    {
        if (Environment.GetEnvironmentVariable("COLUMNVIEW_IDLE_PROBE") != "1")
            return;

        _logPath = Path.Combine(Path.GetTempPath(), "columnview-idleprobe.log");
        File.WriteAllText(_logPath, $"=== IdleProbe start {DateTime.Now:HH:mm:ss} ===\n");

        // 注意: CompositionTarget.Rendering を購読すると WPF が毎フレーム描画し続けてしまい、
        // 計測対象そのものを汚染する。ここでは購読しない。
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnOperationPosted;

        var report = new DispatcherTimer(DispatcherPriority.SystemIdle)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        report.Tick += (_, _) => Report();
        report.Start();
    }

    /// <summary>MainWindow から呼ぶ。レイアウト再計算の回数を数える。</summary>
    public static void AttachWindow(FrameworkElement root)
    {
        if (_logPath.Length == 0)
            return;
        root.LayoutUpdated += (_, _) => Interlocked.Increment(ref _layoutUpdates);
    }

    private static void OnOperationPosted(object? sender, DispatcherHookEventArgs e)
    {
        var name = "(unknown)";
        try
        {
            if (MethodField?.GetValue(e.Operation) is Delegate d)
            {
                name = $"{d.Method.DeclaringType?.FullName}.{d.Method.Name}";
                if (d.Target is not null)
                    name += $" [target={d.Target.GetType().FullName}]";
            }
        }
        catch { /* 調査用なので失敗は無視 */ }
        Posted.AddOrUpdate(name, 1, (_, c) => c + 1);

        // 名前ごとに最初の 1 回だけ呼び出し元スタックを控える (OperationPosted は投稿側スレッドで同期発火する)
        if (!Stacks.ContainsKey(name))
            Stacks.TryAdd(name, Environment.StackTrace);
    }

    private static void Report()
    {
        var renders = Interlocked.Exchange(ref _renderTicks, 0);
        var layouts = Interlocked.Exchange(ref _layoutUpdates, 0);

        var sb = new StringBuilder();
        sb.Append($"[{DateTime.Now:HH:mm:ss}] render={renders}/2s layout={layouts}/2s");

        var top = Posted.ToArray().OrderByDescending(kv => kv.Value).Take(8).ToArray();
        Posted.Clear();
        foreach (var kv in top)
            sb.Append($" | {kv.Key}={kv.Value}");
        sb.Append('\n');

        sb.Append("    focus=").Append(DescribeFocus())
          .Append(" fg=").Append(IsForeground())
          .Append(" uiaTree=").Append(MeasureTree()).Append('\n');
        sb.Append("    clocks: ").Append(DescribeClocks()).Append('\n');

        // 大量投稿している犯人のスタックを 1 回だけ出す
        foreach (var kv in top)
        {
            if (kv.Value < 400 || !StackDumped.Add(kv.Key))
                continue;
            if (Stacks.TryGetValue(kv.Key, out var st))
                sb.Append($"    !!! STACK for {kv.Key} ({kv.Value}/2s):\n{st}\n");
        }

        try { File.AppendAllText(_logPath, sb.ToString()); }
        catch { /* 調査用 */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static bool IsForeground()
    {
        var w = Application.Current?.Windows.OfType<Window>().FirstOrDefault();
        if (w is null)
            return false;
        return new System.Windows.Interop.WindowInteropHelper(w).Handle == GetForegroundWindow();
    }

    /// <summary>UIA クライアントから見える要素数を数える。走査コストはこの数にほぼ比例する。</summary>
    private static string MeasureTree()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = 0;
        var perWindow = new List<string>();
        var sb2 = new StringBuilder();
        foreach (var w in Application.Current?.Windows.OfType<Window>() ?? Enumerable.Empty<Window>())
        {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(w);
            if (peer is null)
                continue;
            var n = CountPeers(peer, 0);
            total += n;
            perWindow.Add($"{w.GetType().Name}={n}");
            if (w is MainWindow)
                Breakdown(peer, sb2);
        }
        return $"{total} ({string.Join(",", perWindow)}) in {sw.ElapsedMilliseconds}ms\n    subtrees: {sb2}";
    }

    /// <summary>要素数の大きい部分木を洗い出す (どこを削れば効くかの判断材料)。</summary>
    private static void Breakdown(System.Windows.Automation.Peers.AutomationPeer p, StringBuilder sb, int depth = 0)
    {
        if (depth > 4)
            return;
        List<System.Windows.Automation.Peers.AutomationPeer>? kids = null;
        try { kids = p.GetChildren(); } catch { }
        if (kids is null)
            return;
        foreach (var k in kids)
        {
            var n = CountPeers(k, 0);
            if (n < 15)
                continue;
            var owner = (k as System.Windows.Automation.Peers.FrameworkElementAutomationPeer)?.Owner as FrameworkElement;
            var label = owner?.Name is { Length: > 0 } nm ? nm : k.GetType().Name.Replace("AutomationPeer", "");
            sb.Append($"[{new string('.', depth)}{label}={n}]");
            Breakdown(k, sb, depth + 1);
        }
    }

    private static int CountPeers(System.Windows.Automation.Peers.AutomationPeer p, int depth)
    {
        if (depth > 40)
            return 1;
        var n = 1;
        try
        {
            var kids = p.GetChildren();
            if (kids is not null)
                foreach (var k in kids)
                    n += CountPeers(k, depth + 1);
        }
        catch { /* 調査用 */ }
        return n;
    }

    private static string DescribeFocus()
    {
        var k = System.Windows.Input.Keyboard.FocusedElement;
        var name = (k as FrameworkElement)?.Name;
        return $"{k?.GetType().Name ?? "null"}{(string.IsNullOrEmpty(name) ? "" : $"('{name}')")}";
    }

    /// <summary>WPF のタイミングツリー (TimeManager の根クロック) を辿り、生きているアニメを列挙する。</summary>
    private static string DescribeClocks()
    {
        try
        {
            var mcType = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext");
            var from = mcType?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var mc = from?.Invoke(null, new object[] { Dispatcher.CurrentDispatcher });
            if (mc is null)
                return "(MediaContext 取得失敗)";

            var tm = mcType!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(mc))
                .FirstOrDefault(v => v?.GetType().Name == "TimeManager");
            if (tm is null)
                return "(TimeManager なし = アニメ無し)";

            var root = tm.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(tm))
                .OfType<System.Windows.Media.Animation.ClockGroup>()
                .FirstOrDefault();
            if (root is null)
                return "(rootClock なし)";

            var sb = new StringBuilder();
            Walk(root, 0, sb);
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }
        catch (Exception ex)
        {
            return "(例外: " + ex.Message + ")";
        }
    }

    private static void Walk(System.Windows.Media.Animation.Clock c, int depth, StringBuilder sb)
    {
        if (depth > 6)
            return;
        if (depth > 0)
        {
            var t = c.Timeline;
            sb.Append($"[d{depth} {t.GetType().Name}");
            if (!string.IsNullOrEmpty(t.Name))
                sb.Append($" name={t.Name}");
            sb.Append($" state={c.CurrentState} repeat={t.RepeatBehavior} dur={t.Duration}] ");
        }
        if (c is System.Windows.Media.Animation.ClockGroup g)
            foreach (var child in g.Children)
                Walk(child, depth + 1, sb);
    }
}

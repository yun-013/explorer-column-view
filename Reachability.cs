using System.IO;

namespace ColumnView;

/// <summary>
/// ネットワーク上の場所 (UNC パス・ネットワークドライブ) に届くかを、UI スレッドを止めずに調べる。
/// 届かない共有への存在確認やシェルの問い合わせは、SMB の接続待ちで数十秒返ってこない。
/// UI スレッドでそれを踏むとアプリ全体が固まるので、共有のルートごとに裏で確かめて
/// 結果をしばらく覚えておき、UI スレッドからはその結果だけを見る。
/// </summary>
public static class Reachability
{
    /// <summary>確認を待つ上限の既定値。届く共有なら数 ms〜数百 ms で返る。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>確認結果を覚えておく時間。過ぎたら次に聞かれたときに確かめ直す。</summary>
    private static readonly TimeSpan RememberFor = TimeSpan.FromSeconds(30);

    /// <summary>確認がこれ以上返ってこなければ、終わるのを待たずに届かないと見なす。</summary>
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(1);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, (Task<bool> Task, DateTime StartedAt)> _running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (bool Reachable, DateTime At)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UNC パスか、ネットワークドライブ上のパスか (ネットワークには触れずに判定する)。</summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            try
            {
                // GetDriveType はドライブ文字の種類を見るだけで、接続先には問い合わせない
                return new DriveInfo(path[..1]).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>共有のルート ("\\host\share" / "Z:\")。取れなければ null。</summary>
    public static string? ShareRoot(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', 3);
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? $@"\\{parts[0]}\{parts[1]}" : null;
        }
        try
        {
            return Path.GetPathRoot(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>確認を裏で始める。同じキーの確認がまだ走っていればそれに相乗りする
    /// (届かない相手を待つスレッドを、聞かれるたびに増やさないため)。</summary>
    public static Task<bool> Probe(string key, Func<bool> probe)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(key, out var running))
                return running.Task;
            var task = Task.Run(() =>
            {
                try
                {
                    return probe();
                }
                catch
                {
                    return false;
                }
            });
            _running[key] = (task, DateTime.UtcNow);
            task.ContinueWith(_ =>
            {
                lock (_lock)
                    _running.Remove(key);
            }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>共有のルートに届くかを裏で確かめ、結果を覚えておく。ネットワーク外なら即 true。</summary>
    public static Task<bool> ProbeShare(string path)
    {
        if (!IsNetworkPath(path) || ShareRoot(path) is not { } root)
            return Task.FromResult(true);
        return Probe(root, () =>
        {
            var reachable = Directory.Exists(root);
            lock (_lock)
                _known[root] = (reachable, DateTime.UtcNow);
            return reachable;
        });
    }

    /// <summary>届くかを最長 <paramref name="timeout"/> だけ待って調べる。
    /// 最近届かなかった共有は待たずに false を返す (確かめ直しは裏で続ける)。</summary>
    public static async Task<bool> IsReachableAsync(string path, TimeSpan? timeout = null)
    {
        if (!IsNetworkPath(path))
            return true;
        var probe = ProbeShare(path);
        if (IsKnownUnreachable(path))
            return false;
        var finished = await Task.WhenAny(probe, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(false);
        return finished == probe && probe.Result;
    }

    /// <summary>覚えている結果だけで「届かない」と分かっているか (ネットワークに触れない)。
    /// 最近届かなかった共有か、確認が長く返ってこない共有なら true。
    /// 分からないときは false なので、UI スレッドで触る前のふるい分けに使う。</summary>
    public static bool IsKnownUnreachable(string? path)
    {
        if (!IsNetworkPath(path) || ShareRoot(path!) is not { } root)
            return false;
        lock (_lock)
        {
            if (_known.TryGetValue(root, out var known) && DateTime.UtcNow - known.At < RememberFor && !known.Reachable)
                return true;
            return _running.TryGetValue(root, out var running) && DateTime.UtcNow - running.StartedAt > SlowAfter;
        }
    }
}

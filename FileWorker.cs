using System.Collections.Concurrent;

namespace ColumnView;

/// <summary>
/// ファイル操作 (コピー・移動・削除・ごみ箱からの復元・ショートカット作成) を、
/// UI とは別の専用 STA スレッドで 1 件ずつ順番に実行する。
/// これらは数 GB のコピーや遅い共有では数十秒〜数分かかり、UI スレッドで同期的に
/// 走らせるとその間アプリ全体が固まる。シェルの部品 (IFileOperation・SHFileOperation・
/// Shell.Application) は STA を前提にしているので、スレッドプールではなく専用の STA を使う。
/// </summary>
public static class FileWorker
{
    private static readonly BlockingCollection<Action> _queue = new();
    private static readonly object _startLock = new();
    private static Thread? _thread;

    [ThreadStatic]
    private static nint _owner;

    /// <summary>実行中の操作の親ウィンドウ (進捗・確認ダイアログの持ち主)。操作の外では 0。</summary>
    public static nint Owner => _owner;

    /// <summary>work を専用スレッドで実行する。<paramref name="owner"/> はシェルのダイアログの
    /// 親にするウィンドウ (UI スレッドで <see cref="FileOps.ActiveWindow"/> などで取っておく)。</summary>
    public static Task<T> RunAsync<T>(nint owner, Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureThread();
        _queue.Add(() =>
        {
            _owner = owner;
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                _owner = 0;
            }
        });
        return done.Task;
    }

    private static void EnsureThread()
    {
        lock (_startLock)
        {
            if (_thread is not null)
                return;
            _thread = new Thread(() =>
            {
                // STA での待機中は CLR が COM のメッセージを汲むので、アパートメントは詰まらない
                foreach (var work in _queue.GetConsumingEnumerable())
                    work();
            })
            {
                IsBackground = true,
                Name = "ColumnView.FileWorker",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }
}

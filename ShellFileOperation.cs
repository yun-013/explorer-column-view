using System.IO;
using System.Runtime.InteropServices;

namespace ColumnView;

/// <summary>
/// IFileOperation (エクスプローラーと同じコピーエンジン) による移動 / コピー。
/// 全項目を 1 回の操作にまとめるので、進捗ダイアログも 1 つ、
/// 上書き確認の「すべての項目に適用」もまとめて効く。
/// 項目ごとの結果 (スキップ / 置換 / 両方残した場合の新しい名前) を
/// 進捗シンクで受け取り、取り消し用に正確な (元, 先) を返す。
/// </summary>
public static class ShellFileOperation
{
    public const uint FOF_NOCONFIRMATION = 0x10;
    public const uint FOF_NOCONFIRMMKDIR = 0x200;
    public const uint FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    public const uint DefaultFlags = FOF_NOCONFIRMMKDIR | FOF_NO_CONNECTED_ELEMENTS;

    private const int COPYENGINE_S_USER_IGNORED = 0x00270005;
    private const int COPYENGINE_S_PENDING = 0x0027000B;
    private const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>1 項目の指示。NewName=null なら同じ名前のまま DestDir へ。</summary>
    public readonly record struct Item(string Source, string DestDir, string? NewName);

    /// <summary>
    /// items をまとめて移動 / コピーする。戻り値は実際に処理できた (元, 作られた先)。
    /// error は失敗時のみ (ユーザーのキャンセルは null)。
    /// </summary>
    public static List<(string Source, string Dest)> Run(
        IReadOnlyList<Item> items, bool copy, nint ownerHwnd, out string? error, uint flags = DefaultFlags)
    {
        error = null;
        var sink = new Sink(items.Select(i => i.Source));
        if (items.Count == 0)
            return new();

        IFileOperation? op = null;
        try
        {
            op = (IFileOperation)new FileOperationComObject();
            op.SetOperationFlags(flags);
            if (ownerHwnd != 0)
                op.SetOwnerWindow(ownerHwnd);
            op.Advise(sink, out var cookie);

            foreach (var item in items)
            {
                var src = CreateItem(item.Source);
                var dir = CreateItem(item.DestDir);
                if (copy)
                    op.CopyItem(src, dir, item.NewName, null);
                else
                    op.MoveItem(src, dir, item.NewName, null);
            }

            var hr = op.PerformOperations();
            op.Unadvise(cookie);
            if (hr < 0 && hr != HRESULT_ERROR_CANCELLED)
                error = $"{(copy ? "コピー" : "移動")}できませんでした: {Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}"}";
            else if (sink.FirstError is { } itemError)
                error = itemError;
        }
        catch (Exception ex)
        {
            error = $"{(copy ? "コピー" : "移動")}できませんでした: {ex.Message}";
        }
        finally
        {
            if (op is not null)
                Marshal.ReleaseComObject(op);
        }
        return sink.Performed;
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var item));
        return item;
    }

    private static string? PathOf(IShellItem? item)
    {
        if (item is null)
            return null;
        try
        {
            item.GetDisplayName(SIGDN_FILESYSPATH, out var p);
            return p;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>項目ごとの結果を集める。フォルダの中身の通知は無視し、指示した最上位の項目だけ記録する。</summary>
    [ComVisible(true)]
    private sealed class Sink : IFileOperationProgressSink
    {
        private readonly HashSet<string> _sources;
        // 同じ項目に複数回通知が来る (確認待ちの「保留」→ 確定) ので、最後の結果で上書きする
        private readonly Dictionary<string, string> _done = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();
        public string? FirstError { get; private set; }

        public List<(string Source, string Dest)> Performed
            => _order.Where(_done.ContainsKey).Select(s => (s, _done[s])).ToList();

        public Sink(IEnumerable<string> sources)
            => _sources = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);

        private void Post(IShellItem item, IShellItem destFolder, string? newName, int hr, IShellItem? created)
        {
            if (PathOf(item) is not { } src || !_sources.Contains(src))
                return;
            if (hr == COPYENGINE_S_PENDING)
                return; // 上書き確認の返事待ち。あとで確定の通知が来る
            if (hr < 0 || hr == COPYENGINE_S_USER_IGNORED)
            {
                // 失敗 / スキップ: 取り消し対象にしない (スキップした先は元からある別物)
                _done.Remove(src);
                if (hr < 0)
                    FirstError ??= $"{Path.GetFileName(src)}: {Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}"}";
                return;
            }
            // 成功 (置換・両方残す = 新しい名前・同じドライブ内の移動 DONT_PROCESS_CHILDREN を含む)
            var dest = PathOf(created)
                ?? (PathOf(destFolder) is { } dir ? Path.Combine(dir, newName ?? Path.GetFileName(src)) : null);
            if (dest is null)
                return;
            if (!_order.Contains(src, StringComparer.OrdinalIgnoreCase))
                _order.Add(src);
            _done[src] = dest;
        }

        public int PostMoveItem(uint f, IShellItem item, IShellItem dest, string? newName, int hr, IShellItem? created)
        {
            Post(item, dest, newName, hr, created);
            return 0;
        }

        public int PostCopyItem(uint f, IShellItem item, IShellItem dest, string? newName, int hr, IShellItem? created)
        {
            Post(item, dest, newName, hr, created);
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int hr) => 0;
        public int PreRenameItem(uint f, IShellItem item, string? newName) => 0;
        public int PostRenameItem(uint f, IShellItem item, string? newName, int hr, IShellItem? created) => 0;
        public int PreMoveItem(uint f, IShellItem item, IShellItem dest, string? newName) => 0;
        public int PreCopyItem(uint f, IShellItem item, IShellItem dest, string? newName) => 0;
        public int PreDeleteItem(uint f, IShellItem item) => 0;
        public int PostDeleteItem(uint f, IShellItem item, int hr, IShellItem? created) => 0;
        public int PreNewItem(uint f, IShellItem dest, string? newName) => 0;
        public int PostNewItem(uint f, IShellItem dest, string? newName, string? template, uint attrs, int hr, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint soFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    // ---- COM 定義 (shobjidl_core.h) ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, nint pbc, ref Guid riid, out IShellItem ppv);

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperationComObject { }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog([MarshalAs(UnmanagedType.IUnknown)] object popd);
        void SetProperties([MarshalAs(UnmanagedType.IUnknown)] object pproparray);
        void SetOwnerWindow(nint hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}

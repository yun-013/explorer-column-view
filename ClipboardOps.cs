using System.IO;
using System.Windows;

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
        try
        {
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch
        {
            return false; // クリップボードが他プロセスに占有されている
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
        try
        {
            Clipboard.Clear();
        }
        catch
        {
            // 占有中なら無視 (次の貼り付けが失敗するだけ)
        }
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

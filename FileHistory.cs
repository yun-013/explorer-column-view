using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColumnView;

// ============================================================================
// ファイル操作の履歴 (取り消し / やり直し)
//
// 1 本の時系列 (新しい順) に「実行済み」と「取り消し済み」の項目が並ぶ。
// 取り消し済みは常に先頭側にまとまっていて、その下が実行済み。
//   [取り消し済み (やり直せる)] … [実行済み (取り消せる)] …
// 新しい操作をすると取り消し済みの分は捨てる (一般的なエディタと同じ直線の履歴)。
//
// 各項目は「今の状態から反対側へ移る操作 (IUndoOp)」を 1 つ持つ。実行済みなら取り消し用、
// 取り消し済みならやり直し用。操作を実行すると、できた分の逆操作 (Inverse) と
// できなかった分 (Remaining) が返るので、一部だけ失敗しても履歴から何も失わない。
// ============================================================================

/// <summary>操作が動かす 1 項目。From=null は「新しく作られた」、To=null は「ごみ箱へ」。</summary>
public readonly record struct HistoryMove(string? From, string? To);

/// <summary>取り消し / やり直しを 1 回実行した結果。</summary>
public sealed class OpResult
{
    public HashSet<string> Affected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Error { get; set; }

    /// <summary>実行できた部分の逆操作 (これを実行すると今回の実行前へ戻る)。何もできなければ null。</summary>
    public IUndoOp? Inverse { get; set; }

    /// <summary>実行できなかったが、もう一度試せば実行できるかもしれない部分 (キャンセル・使用中など)。</summary>
    public IUndoOp? Remaining { get; set; }

    /// <summary>対象が見つからず、もう実行しようがない項目 (アプリの外で移動・削除された等)。</summary>
    public List<HistoryMove> Lost { get; } = new();

    public bool IsClean => Error is null && Remaining is null && Lost.Count == 0;

    public static OpResult Empty => new();
}

/// <summary>履歴の 1 手が持つ「反対側へ移る操作」。データだけを持ち、履歴ファイルへそのまま保存できる。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(RenameOp), "rename")]
[JsonDerivedType(typeof(MoveOp), "move")]
[JsonDerivedType(typeof(RecycleOp), "recycle")]
[JsonDerivedType(typeof(RestoreOp), "restore")]
[JsonDerivedType(typeof(CompositeOp), "composite")]
public interface IUndoOp
{
    /// <summary>この操作を実行すると起きる移動 (表示用)。</summary>
    IEnumerable<HistoryMove> GetMoves();

    /// <summary>実行する (ファイル操作の専用スレッドで呼ぶ)。</summary>
    OpResult Execute();
}

internal static class UndoOps
{
    /// <summary>null を除いて 1 つの操作にまとめる (0 個なら null)。</summary>
    public static IUndoOp? Combine(IEnumerable<IUndoOp?> ops)
    {
        var list = ops.OfType<IUndoOp>().ToList();
        return list.Count switch
        {
            0 => null,
            1 => list[0],
            _ => new CompositeOp { Ops = list },
        };
    }

    public static void AddParent(HashSet<string> set, string? path)
    {
        if (path is not null && Path.GetDirectoryName(Trim(path)) is { Length: > 0 } dir)
            set.Add(dir);
    }

    public static string Trim(string path)
        => path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

/// <summary>名前の変更を元へ戻す: 今の名前 (NewPath) → 元の名前 (OldPath)。</summary>
public sealed class RenameOp : IUndoOp
{
    public RenameOp() { }

    public RenameOp(string oldPath, string newPath)
    {
        OldPath = oldPath;
        NewPath = newPath;
    }

    /// <summary>実行後の名前 (戻す先)。</summary>
    public string OldPath { get; set; } = "";

    /// <summary>今の名前。</summary>
    public string NewPath { get; set; } = "";

    public IEnumerable<HistoryMove> GetMoves() => new[] { new HistoryMove(NewPath, OldPath) };

    public OpResult Execute()
    {
        var r = new OpResult();
        UndoOps.AddParent(r.Affected, OldPath);
        try
        {
            if (Directory.Exists(NewPath))
                Directory.Move(NewPath, OldPath);
            else if (File.Exists(NewPath))
                File.Move(NewPath, OldPath);
            else
            {
                r.Lost.Add(new HistoryMove(NewPath, OldPath));
                return r;
            }
            r.Inverse = new RenameOp(NewPath, OldPath);
        }
        catch (Exception ex)
        {
            r.Error = "名前を変更できませんでした: " + ex.Message;
            r.Remaining = this;
        }
        return r;
    }
}

/// <summary>1 項目の移動 (Dest にあるものを Source へ)。</summary>
public sealed record MovePair(string Source, string Dest);

/// <summary>移動 (切り取り貼り付け / D&amp;D / 退避ごみ箱からの出し入れ): Dest にあるものを Source へ戻す。</summary>
public sealed class MoveOp : IUndoOp
{
    public MoveOp() { }

    /// <param name="pairs">(Source = 戻す先, Dest = 今ある場所)。ファイル操作の performed をそのまま渡せる。</param>
    public MoveOp(IEnumerable<(string Source, string Dest)> pairs)
        => Pairs = pairs.Select(p => new MovePair(p.Source, p.Dest)).ToList();

    public List<MovePair> Pairs { get; set; } = new();

    public IEnumerable<HistoryMove> GetMoves() => Pairs.Select(p => new HistoryMove(p.Dest, p.Source));

    public OpResult Execute()
    {
        var r = new OpResult();
        var moves = new List<(string From, string To)>();
        var retry = new List<MovePair>();
        foreach (var p in Pairs)
        {
            UndoOps.AddParent(r.Affected, p.Source);
            UndoOps.AddParent(r.Affected, p.Dest);
            if (!UndoOps.Exists(p.Dest))
            {
                r.Lost.Add(new HistoryMove(p.Dest, p.Source));
                continue;
            }
            try
            {
                // 戻す先の親が消えていても戻せるように作り直す (掃除済みの退避フォルダへのやり直し等)
                if (Path.GetDirectoryName(UndoOps.Trim(p.Source)) is { Length: > 0 } parent)
                    Directory.CreateDirectory(parent);
                moves.Add((p.Dest, p.Source));
            }
            catch (Exception ex)
            {
                r.Error ??= "移動できませんでした: " + ex.Message;
                retry.Add(p);
            }
        }

        // エクスプローラーと同じく 1 回のシェル操作でまとめて動かす (ダイアログも 1 つ)
        string? moveError = null;
        var performed = moves.Count > 0
            ? FileOps.MoveAll(moves, out moveError)
            : new List<(string Source, string Dest)>();
        if (moveError is not null)
            r.Error ??= moveError;

        // performed の Source は「動かした元 (= p.Dest)」。逆操作はそのまま MoveOp にすればよい
        var done = new HashSet<string>(performed.Select(x => x.Source), StringComparer.OrdinalIgnoreCase);
        retry.AddRange(Pairs.Where(p => moves.Any(m => string.Equals(m.From, p.Dest, StringComparison.OrdinalIgnoreCase))
                                        && !done.Contains(p.Dest)));
        if (performed.Count > 0)
            r.Inverse = new MoveOp(performed);
        if (retry.Count > 0)
        {
            r.Remaining = new MoveOp { Pairs = retry };
            r.Error ??= "一部を移動できませんでした (キャンセルされたか、使用中の可能性があります)";
        }
        return r;
    }
}

/// <summary>「これらをごみ箱へ入れる」操作。コピー / 新規フォルダの取り消し、削除のやり直しに使う。
/// ごみ箱の無い場所 (NAS 等) はアプリの退避ごみ箱へ移動し、完全削除にはしない。</summary>
public sealed class RecycleOp : IUndoOp
{
    public RecycleOp() { }

    public RecycleOp(IEnumerable<string> paths) => Paths = paths.ToList();

    public List<string> Paths { get; set; } = new();

    public IEnumerable<HistoryMove> GetMoves() => Paths.Select(p => new HistoryMove(p, null));

    public OpResult Execute()
    {
        var r = new OpResult();
        var existing = new List<string>();
        foreach (var p in Paths)
        {
            UndoOps.AddParent(r.Affected, p);
            if (UndoOps.Exists(p))
                existing.Add(p);
            else
                r.Lost.Add(new HistoryMove(p, null));
        }

        var recyclable = existing.Where(FileOps.SupportsRecycleBin).ToList();
        var trashable = existing.Where(p => !FileOps.SupportsRecycleBin(p)).ToList();
        var retry = new List<string>();
        IUndoOp? restore = null;
        IUndoOp? untrash = null;

        if (recyclable.Count > 0)
        {
            FileOps.Delete(recyclable, permanent: false, 0, out var e);
            r.Error ??= e;
            // キャンセルされた項目はそのまま残っている。消えたものだけをごみ箱入りと見なす
            var gone = recyclable.Where(p => !UndoOps.Exists(p)).ToList();
            retry.AddRange(recyclable.Except(gone));
            if (gone.Count > 0)
                restore = new RestoreOp(gone);
        }
        if (trashable.Count > 0)
        {
            var performed = FileOps.MoveToAppTrash(trashable, out var e);
            r.Error ??= e;
            var moved = new HashSet<string>(performed.Select(x => x.Source), StringComparer.OrdinalIgnoreCase);
            retry.AddRange(trashable.Where(p => !moved.Contains(UndoOps.Trim(p))));
            if (performed.Count > 0)
                untrash = new MoveOp(performed);
        }

        r.Inverse = UndoOps.Combine(new[] { restore, untrash });
        if (retry.Count > 0)
        {
            r.Remaining = new RecycleOp(retry);
            r.Error ??= "一部をごみ箱へ移動できませんでした";
        }
        return r;
    }
}

/// <summary>「これらをごみ箱から元の場所へ戻す」操作。削除の取り消し、コピー等のやり直しに使う。</summary>
public sealed class RestoreOp : IUndoOp
{
    public RestoreOp() { }

    public RestoreOp(IEnumerable<string> paths) => Paths = paths.ToList();

    public List<string> Paths { get; set; } = new();

    public IEnumerable<HistoryMove> GetMoves() => Paths.Select(p => new HistoryMove(null, p));

    public OpResult Execute()
    {
        var r = new OpResult();
        foreach (var p in Paths)
            UndoOps.AddParent(r.Affected, p);

        var (restored, failed) = RecycleBin.Restore(Paths);
        if (restored.Count > 0)
            r.Inverse = new RecycleOp(restored);
        if (failed.Count > 0)
        {
            r.Remaining = new RestoreOp(failed);
            r.Error ??= "ごみ箱から戻せなかった項目があります";
        }
        foreach (var p in Paths.Except(restored, StringComparer.OrdinalIgnoreCase)
                               .Except(failed, StringComparer.OrdinalIgnoreCase))
            r.Lost.Add(new HistoryMove(null, p)); // ごみ箱に見当たらない (空にした等)
        return r;
    }
}

/// <summary>複数の操作を 1 手として扱う (ごみ箱と退避ごみ箱が混在した削除など)。</summary>
public sealed class CompositeOp : IUndoOp
{
    public List<IUndoOp> Ops { get; set; } = new();

    public IEnumerable<HistoryMove> GetMoves() => Ops.SelectMany(o => o.GetMoves());

    public OpResult Execute()
    {
        var r = new OpResult();
        var inverses = new List<IUndoOp?>();
        var remaining = new List<IUndoOp?>();
        foreach (var op in Ops)
        {
            var sub = op.Execute();
            r.Affected.UnionWith(sub.Affected);
            r.Error ??= sub.Error;
            r.Lost.AddRange(sub.Lost);
            inverses.Add(sub.Inverse);
            remaining.Add(sub.Remaining);
        }
        // 逆操作は逆順に並べる (後からした部分を先に戻す)
        inverses.Reverse();
        r.Inverse = UndoOps.Combine(inverses);
        r.Remaining = UndoOps.Combine(remaining);
        return r;
    }
}

/// <summary>履歴の操作の種類 (アイコンと文言の出し分け用)。</summary>
public enum HistoryKind { Move, Copy, Link, Delete, Rename, NewFolder }

/// <summary>履歴の 1 手。</summary>
public sealed class HistoryEntry : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public HistoryKind Kind { get; set; }

    /// <summary>「3 項目: Downloads → Archive」のような一行の要約 (操作した時点の内容)。</summary>
    public string Summary { get; set; } = "";

    private bool _isDone = true;
    /// <summary>true = 実行済み (取り消せる)、false = 取り消し済み (やり直せる)。</summary>
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (Set(ref _isDone, value))
                Raise(nameof(StateText));
        }
    }

    /// <summary>反対側へ移る操作 (実行済みなら取り消し用、取り消し済みならやり直し用)。
    /// 対象がすべて見つからなくなった手では null (状態だけ切り替える)。</summary>
    public IUndoOp? Op { get; set; }

    private string? _note;
    /// <summary>最後の取り消し / やり直しで起きた問題 (一部だけ戻せなかった等)。</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
                Raise(nameof(HasNote));
        }
    }

    // ---- 表示用 (保存しない) ----

    [JsonIgnore] public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>前回の起動から引き継いだ手か。</summary>
    [JsonIgnore] public bool FromPreviousSession { get; set; }

    [JsonIgnore]
    public string Title => Kind switch
    {
        HistoryKind.Move => "移動",
        HistoryKind.Copy => "コピー",
        HistoryKind.Link => "ショートカット作成",
        HistoryKind.Delete => "削除",
        HistoryKind.Rename => "名前の変更",
        HistoryKind.NewFolder => "新しいフォルダ",
        _ => "操作",
    };

    [JsonIgnore]
    public string Glyph => Kind switch
    {
        // ドラッグのゴーストと同じ字形にそろえる (移動 = 進む矢印、コピー、リンク)
        HistoryKind.Move => "",
        HistoryKind.Copy => "",
        HistoryKind.Link => "",
        HistoryKind.Delete => "",
        HistoryKind.Rename => "",
        HistoryKind.NewFolder => "",
        _ => "",
    };

    [JsonIgnore]
    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm:ss") : Time.ToString("M/d HH:mm");

    [JsonIgnore] public string StateText => IsDone ? "" : "取り消し済み";

    /// <summary>操作した向き (元 → 先) の項目一覧。</summary>
    [JsonIgnore]
    public IReadOnlyList<HistoryMove> Items
        => Op is null
            ? Array.Empty<HistoryMove>()
            : IsDone
                ? Op.GetMoves().Select(m => new HistoryMove(m.To, m.From)).ToList()
                : Op.GetMoves().ToList();

    private bool _inRange;
    /// <summary>「ここまで戻す / やり直す」にマウスを載せたとき、巻き込まれる手を示す。</summary>
    [JsonIgnore]
    public bool InRange
    {
        get => _inRange;
        set => Set(ref _inRange, value);
    }

    private bool _isExpanded;
    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private IReadOnlyList<object>? _rows;
    /// <summary>詳細表示の行 (履歴パネルが開いたときに作る)。</summary>
    [JsonIgnore]
    public IReadOnlyList<object>? Rows
    {
        get => _rows;
        set => Set(ref _rows, value);
    }

    private bool _showCurrentMarker;
    /// <summary>この手の上に「ここが今の状態」の区切りを出す (上に取り消し済みの手があるとき)。</summary>
    [JsonIgnore]
    public bool ShowCurrentMarker
    {
        get => _showCurrentMarker;
        set => Set(ref _showCurrentMarker, value);
    }

    private bool _showSessionMarker;
    /// <summary>この手の上に「前回の起動までの操作」の区切りを出す。</summary>
    [JsonIgnore]
    public bool ShowSessionMarker
    {
        get => _showSessionMarker;
        set => Set(ref _showSessionMarker, value);
    }

    /// <summary>新しい操作から履歴の 1 手を作る。</summary>
    /// <param name="undo">この操作を取り消す操作。</param>
    /// <param name="shown">要約に使う (元, 先)。元=null は新規作成、先=null はごみ箱。</param>
    public static HistoryEntry Create(HistoryKind kind, IUndoOp undo, IReadOnlyList<(string? From, string? To)> shown)
        => new() { Kind = kind, Op = undo, Summary = Summarize(kind, shown) };

    private static string Summarize(HistoryKind kind, IReadOnlyList<(string? From, string? To)> shown)
    {
        if (shown.Count == 0)
            return "";
        static string Name(string? p) => p is null ? "" : Path.GetFileName(UndoOps.Trim(p)) is { Length: > 0 } n ? n : p;
        static string? Dir(string? p) => p is null ? null : Path.GetDirectoryName(UndoOps.Trim(p));
        static string DirName(string? dir) => dir is null ? "" : Path.GetFileName(dir) is { Length: > 0 } n ? n : dir;

        var first = shown[0];
        if (kind == HistoryKind.Rename)
            return $"{Name(first.From)} → {Name(first.To)}";

        var name = Name(first.To ?? first.From);
        var what = shown.Count == 1 ? name : $"{name} ほか {shown.Count - 1} 項目";

        var fromDirs = shown.Select(s => Dir(s.From)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var toDirs = shown.Select(s => Dir(s.To)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = fromDirs.Count == 1 ? DirName(fromDirs[0]) : "複数の場所";
        var to = toDirs.Count == 1 ? DirName(toDirs[0]) : "複数の場所";
        return kind switch
        {
            HistoryKind.Delete => $"{what}  ({from})",
            HistoryKind.NewFolder => $"{what}  ({to})",
            _ => $"{what}:  {from} → {to}",
        };
    }
}

/// <summary>全ウィンドウ共有のファイル操作履歴 (新しい順)。UI スレッドからだけ触る。</summary>
public static class FileHistory
{
    /// <summary>新しい順。先頭側に取り消し済み、その下に実行済み。</summary>
    public static ObservableCollection<HistoryEntry> Entries { get; } = new();

    /// <summary>件数や状態が変わった (保存・ボタンの有効/無効の更新用)。</summary>
    public static event Action? Changed;

    private static int Capacity => Math.Clamp(AppSettings.Current.HistoryCapacity, 10, 1000);

    /// <summary>直前に取り消せる手 (実行済みのうち一番新しいもの)。</summary>
    public static HistoryEntry? LatestDone => Entries.FirstOrDefault(e => e.IsDone);

    /// <summary>次にやり直せる手 (取り消し済みのうち一番古いもの)。</summary>
    public static HistoryEntry? OldestUndone => Entries.TakeWhile(e => !e.IsDone).LastOrDefault();

    /// <summary>新しい操作を記録する (取り消し済みの分は捨てる)。</summary>
    public static void Push(HistoryEntry entry)
    {
        while (Entries.Count > 0 && !Entries[0].IsDone)
            Entries.RemoveAt(0);
        Entries.Insert(0, entry);
        while (Entries.Count > Capacity)
            Entries.RemoveAt(Entries.Count - 1);
        OnChanged();
    }

    /// <summary>target まで取り消すときに順に処理する手 (新しい順)。target が実行済みでなければ空。</summary>
    public static List<HistoryEntry> UndoChain(HistoryEntry target)
    {
        var index = Entries.IndexOf(target);
        if (index < 0 || !target.IsDone)
            return new();
        return Entries.Take(index + 1).Where(e => e.IsDone).ToList();
    }

    /// <summary>target までやり直すときに順に処理する手 (古い順)。target が取り消し済みでなければ空。</summary>
    public static List<HistoryEntry> RedoChain(HistoryEntry target)
    {
        var index = Entries.IndexOf(target);
        if (index < 0 || target.IsDone)
            return new();
        return Entries.Skip(index).TakeWhile(e => !e.IsDone).Reverse().ToList();
    }

    /// <summary>手を 1 つ実行した結果を履歴へ反映する。続けて次の手へ進んでよければ null、
    /// 止めるべき問題があればその説明を返す。</summary>
    public static string? Apply(HistoryEntry entry, OpResult r)
    {
        var verb = entry.IsDone ? "元に戻せませんでした" : "やり直せませんでした";
        string? problem = null;

        if (r.Inverse is null && r.Remaining is not null)
        {
            // 何もできなかった (キャンセル・使用中など): この手はそのまま。もう一度試せる
            entry.Op = r.Remaining;
            problem = $"{entry.Title}: {r.Error ?? verb}";
            entry.Note = problem;
        }
        else if (r.Remaining is not null)
        {
            // 一部だけできた: できた分を反対側の手として切り出し、残りはこの手に残す
            var index = Entries.IndexOf(entry);
            var split = new HistoryEntry
            {
                Kind = entry.Kind,
                Time = entry.Time,
                Summary = entry.Summary + " (一部)",
                IsDone = !entry.IsDone,
                Op = r.Inverse,
                FromPreviousSession = entry.FromPreviousSession,
            };
            entry.Op = r.Remaining;
            entry.Summary += " (残り)";
            problem = $"{entry.Title}: 一部だけ{(entry.IsDone ? "元に戻しました" : "やり直しました")}。{r.Error}";
            entry.Note = problem;
            // 取り消し済みは上側に並ぶ。取り消したならこの手の上、やり直したならこの手の下に置く
            Entries.Insert(entry.IsDone ? index : index + 1, split);
            Raise(entry);
        }
        else
        {
            // 全部できた (見つからない項目は除く)
            entry.Op = r.Inverse;
            entry.IsDone = !entry.IsDone;
            entry.Note = null;
            if (r.Lost.Count > 0)
            {
                problem = $"{entry.Title}: {r.Lost.Count} 個の項目が見つからず、{(entry.IsDone ? "やり直せ" : "戻せ")}ませんでした";
                entry.Note = problem;
            }
            else if (r.Error is not null)
            {
                problem = $"{entry.Title}: {r.Error}";
                entry.Note = problem;
            }
            Raise(entry);
        }
        OnChanged();
        return problem;
    }

    private static void Raise(HistoryEntry entry)
    {
        // Items / Summary は Op の差し替えで変わるので、行を作り直させる
        var i = Entries.IndexOf(entry);
        if (i >= 0)
            Entries[i] = entry;
    }

    private static void OnChanged()
    {
        RefreshMarkers();
        Changed?.Invoke();
        HistoryStore.ScheduleSave();
    }

    /// <summary>「ここが今の状態」「前回の起動まで」の区切りを付け直す。</summary>
    private static void RefreshMarkers()
    {
        var anyUndone = Entries.Count > 0 && !Entries[0].IsDone;
        var latestDone = LatestDone;
        var firstPrevious = Entries.FirstOrDefault(e => e.FromPreviousSession);
        foreach (var e in Entries)
        {
            e.ShowCurrentMarker = anyUndone && ReferenceEquals(e, latestDone);
            e.ShowSessionMarker = ReferenceEquals(e, firstPrevious);
        }
    }

    /// <summary>起動時に前回の履歴を読み込む。</summary>
    public static void Load()
    {
        foreach (var e in HistoryStore.Load())
        {
            e.FromPreviousSession = true;
            Entries.Add(e);
        }
        // 読み込んだ並びが壊れていても「取り消し済みが先頭側」を保つ
        var undone = Entries.Where(e => !e.IsDone).ToList();
        var firstDone = Entries.TakeWhile(e => !e.IsDone).Count();
        if (undone.Count != firstDone)
            foreach (var e in Entries.Skip(firstDone).Where(e => !e.IsDone).ToList())
                Entries.Remove(e);
        RefreshMarkers();
        Changed?.Invoke();
    }
}

/// <summary>履歴を設定と同じ場所の history.json に保存する (再起動後も確認・巻き戻しできるように)。</summary>
internal static class HistoryStore
{
    private static readonly string FilePath = Path.Combine(
        AppSettings.DevProfileDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColumnView"),
        "history.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object _writeLock = new();
    private static bool _scheduled;

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception)
        {
            // 壊れた履歴は読まない (次の保存で上書きされる)
        }
        return new();
    }

    /// <summary>変更をまとめて少し後に保存する (連続した変更で何度も書かない)。</summary>
    public static void ScheduleSave()
    {
        if (_scheduled || System.Windows.Application.Current is not { } app)
            return;
        _scheduled = true;
        app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            _scheduled = false;
            SaveNow();
        });
    }

    /// <summary>今すぐ保存する (終了時用)。中身は UI スレッドで文字列にし、書き込みは裏で行う。</summary>
    public static void SaveNow(bool wait = false)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(FileHistory.Entries.ToList(), Options);
        }
        catch (Exception)
        {
            return;
        }
        var task = Task.Run(() =>
        {
            lock (_writeLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch (Exception)
                {
                    // 保存できなくても動作は続ける (次の変更で再挑戦)
                }
            }
        });
        if (wait)
            task.Wait(TimeSpan.FromSeconds(2));
    }
}

/// <summary>ごみ箱の項目をシェルの「元に戻す」動詞で復元する。</summary>
public static class RecycleBin
{
    private const int SsfBitBucket = 10; // ごみ箱

    /// <summary>元のフルパスが一致する項目を復元する。
    /// Restored = 戻せたもの、Failed = ごみ箱にはあったが戻せなかったもの (それ以外はごみ箱に見当たらない)。</summary>
    public static (List<string> Restored, List<string> Failed) Restore(IReadOnlyList<string> originalPaths)
    {
        var remaining = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
        var restored = new List<string>();
        var failed = new List<string>();
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType
                || Activator.CreateInstance(shellType) is not { } shellObj)
                return (restored, failed);
            dynamic shell = shellObj;
            var bin = shell.NameSpace(SsfBitBucket);
            if (bin is null)
                return (restored, failed);

            // 同名を複数回削除した場合に最近のものを優先するため、列挙の末尾から照合する
            var items = new List<dynamic>();
            foreach (var item in bin.Items())
                items.Add(item);

            for (var i = items.Count - 1; i >= 0 && remaining.Count > 0; i--)
            {
                var item = items[i];
                string name = bin.GetDetailsOf(item, 0);     // 名前 (拡張子非表示設定だと拡張子なし)
                string origDir = bin.GetDetailsOf(item, 1);  // 元の場所
                if (MatchTarget(remaining, origDir, name) is not { } target)
                    continue;
                remaining.Remove(target);
                if (InvokeRestore(item))
                    restored.Add(target);
                else
                    failed.Add(target);
            }
        }
        catch (Exception)
        {
            // シェルにアクセスできなければ復元なしで返す
        }
        return (restored, failed);
    }

    /// <summary>「拡張子を表示しない」設定でも一致できるよう、拡張子なしの名前も許容して照合する。</summary>
    private static string? MatchTarget(HashSet<string> remaining, string origDir, string name)
    {
        foreach (var t in remaining)
        {
            if (!string.Equals(Path.GetDirectoryName(t.TrimEnd('\\')), origDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var fileName = Path.GetFileName(t.TrimEnd('\\'));
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(fileName), name, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    private static bool InvokeRestore(dynamic item)
    {
        try
        {
            foreach (var verb in item.Verbs())
            {
                string label = ((string)verb.Name).Replace("&", "");
                if (label.StartsWith("元に戻す", StringComparison.Ordinal)
                    || label.StartsWith("Restore", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // この項目は復元できなかった
        }
        return false;
    }
}

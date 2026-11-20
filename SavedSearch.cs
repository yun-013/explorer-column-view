using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ColumnView;

/// <summary>
/// 保存された検索 (.search-ms) の中身。「スマホ連携」の接続デバイス フォルダの
/// 「画像」「カメラ」などはこれを指すショートカットになっている。
/// 種類 (画像・動画など) と検索範囲 (含める / 除くフォルダ) だけを読み、
/// アプリ自身の走査で再現する (Windows Search のインデックスには頼らない)。
/// それ以外の条件 (日付・キーワード等) は読まないので、結果はその分だけ広めになる。
/// </summary>
public sealed class SavedSearch
{
    /// <summary>列ヘッダーに出す名前 (ファイル名から拡張子を除いたもの)。</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<Scope> Includes { get; init; }

    /// <summary>除外するフォルダ (配下ごと)。</summary>
    public required IReadOnlyList<string> Excludes { get; init; }

    /// <summary>対象の拡張子 (小文字・ドット付き)。null なら種類で絞らない。</summary>
    public HashSet<string>? Extensions { get; init; }

    public readonly record struct Scope(string Path, bool Recursive);

    /// <summary>項目が条件に合うか。種類で絞る検索はファイルだけが対象。</summary>
    public bool Matches(string name, bool isDirectory)
        => Extensions is null || (!isDirectory && Extensions.Contains(System.IO.Path.GetExtension(name)));

    /// <summary>.search-ms を読む。範囲が 1 つも実パスに直せなければ null。
    /// 範囲の解決にシェルを使うので、裏スレッドから呼ぶこと。</summary>
    public static SavedSearch? Load(string path)
    {
        try
        {
            var query = Child(XDocument.Load(path).Root, "query");
            if (query is null)
                return null;

            var kinds = Children(Child(query, "kindList"), "kind")
                .Select(k => (string?)k.Attribute("name"))
                .OfType<string>()
                .Where(k => k.Length > 0)
                .ToList();

            var scope = Child(query, "scope");
            var includes = Children(scope, "include")
                .Select(e => (Path: ResolveScopePath((string?)e.Attribute("path")),
                              Recursive: !string.Equals((string?)e.Attribute("nonRecursive"), "true", StringComparison.OrdinalIgnoreCase)))
                .Where(s => s.Path is not null)
                // ローカルで実在しない範囲は捨てる (ローカルと確かめられない範囲はここでは
                // 確かめない — 信頼できる相手かを呼び出し側が判断するまで触れないため)
                .Where(s => !NetworkTrust.IsLocalPath(s.Path) || Directory.Exists(s.Path))
                .Select(s => new Scope(s.Path!, s.Recursive))
                .ToList();
            if (includes.Count == 0)
                return null;
            var excludes = Children(scope, "exclude")
                .Select(e => ResolveScopePath((string?)e.Attribute("path")))
                .OfType<string>()
                .ToList();

            return new SavedSearch
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Includes = includes,
                Excludes = excludes,
                // "item" は「すべての種類」(接続デバイスの「ダウンロード」等)
                Extensions = kinds.Count == 0 || kinds.Contains("item", StringComparer.OrdinalIgnoreCase)
                    ? null
                    : ExtensionsOf(kinds),
            };
        }
        catch
        {
            return null;
        }
    }

    // 名前空間の有無に左右されないよう、要素はローカル名で探す
    private static XElement? Child(XElement? parent, string name)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name)
        => parent?.Elements().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    /// <summary>範囲のパスを実パスに直す。ネットワークには触れない (信頼できるかは呼び出し側が
    /// 実パスで判断する)。"::{CLSID}\storage" のようなシェルの名前は、実フォルダを
    /// 持つ登録済みのフォルダ (接続デバイス フォルダ・クラウド同期フォルダ等) だけを
    /// レジストリから直す。シェルに解析させると、名前によってはその場で相手のサーバーへ
    /// 繋ぎに行くため使わない。直せない名前の範囲は捨てる。</summary>
    private static string? ResolveScopePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith("::{", StringComparison.Ordinal))
        {
            var end = path.IndexOf('}');
            if (end < 0 || DelegateFolderTarget(path[2..(end + 1)]) is not { } root)
                return null;
            var rest = path[(end + 1)..].TrimStart('\\');
            return rest.Length == 0 ? root : System.IO.Path.Combine(root, rest);
        }
        return System.IO.Path.IsPathFullyQualified(path)
            ? System.IO.Path.TrimEndingDirectorySeparator(path)
            : null;
    }

    /// <summary>実フォルダを指す登録済みのシェルフォルダ (CLSID\Instance\InitPropertyBag の
    /// TargetFolderPath) の実パス。そうでない CLSID なら null。</summary>
    private static string? DelegateFolderTarget(string clsid)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var bag = hive.OpenSubKey($@"Software\Classes\CLSID\{clsid}\Instance\InitPropertyBag");
                if (bag?.GetValue("TargetFolderPath") is string target && target.Length > 0)
                    return System.IO.Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(target));
            }
            catch
            {
                // 読めなければ次へ
            }
        }
        return null;
    }

    /// <summary>種類名 (picture・video 等) から拡張子の集合を作る。
    /// 対応表はエクスプローラーと同じ KindMap (拡張子 → 種類) を引く。</summary>
    private static HashSet<string> ExtensionsOf(IEnumerable<string> kinds)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
            if (KindMap.Value.TryGetValue(kind, out var extensions))
                result.UnionWith(extensions);
        return result;
    }

    private static readonly Lazy<Dictionary<string, HashSet<string>>> KindMap = new(() =>
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\KindMap");
            if (key is null)
                return map;
            foreach (var extension in key.GetValueNames())
            {
                if (key.GetValue(extension) is not string value)
                    continue;
                foreach (var kind in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!map.TryGetValue(kind, out var set))
                        map[kind] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(extension);
                }
            }
        }
        catch
        {
            // 読めなければ該当なしになる (種類を無視して全件を並べるより安全)
        }
        return map;
    });
}

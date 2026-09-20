using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ColumnView;

/// <summary>
/// 絵の出ないファイルの「中身」をテキストとして取り出す。
/// Office 書類 (docx / xlsx / pptx) は中身が zip の XML なので自前で読む
/// (Office が入っていない環境でも動き、外部ライブラリも要らない)。
/// 書庫は中のファイル一覧、CSV/TSV は桁を揃えた表、ショートカットはリンク先を見せる。
/// </summary>
public static class DocumentPreview
{
    /// <summary>取り出すテキストの上限 (これ以上は切り上げる)。</summary>
    private const int MaxChars = 200_000;
    private const int MaxRows = 500;
    private const int MaxColumns = 40;
    private const int MaxZipEntries = 500;
    private const int MaxSlides = 50;
    private const int MaxSheets = 20;
    private const int MaxSharedStrings = 100_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm",
        ".zip", ".csv", ".tsv", ".lnk",
    };

    public static bool Handles(string extension) => Extensions.Contains(extension);

    /// <summary>種類に応じた中身のテキスト。読めなければ null (従来のサムネイル表示に任せる)。</summary>
    public static string? Read(string path)
    {
        var ext = Path.GetExtension(path);
        try
        {
            return ext.ToLowerInvariant() switch
            {
                ".docx" or ".docm" => FromWord(path),
                ".xlsx" or ".xlsm" => FromExcel(path),
                ".pptx" or ".pptm" => FromPowerPoint(path),
                ".zip" => FromArchive(path),
                ".csv" => FromDelimited(path, ','),
                ".tsv" => FromDelimited(path, '\t'),
                ".lnk" => FromShortcut(path),
                _ => null,
            };
        }
        catch
        {
            return null; // 壊れている・暗号化されている等
        }
    }

    // ---- Word ----

    private static string? FromWord(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null)
            return null;

        var text = new StringBuilder();
        bool inText = false;
        using var reader = OpenXml(entry);
        while (text.Length < MaxChars && reader.Read())
        {
            // 文字は <w:t> の中身。ReadElementContentAsString で読むと閉じタグの次まで
            // 進んでしまい、段落の終わりを取りこぼすので、テキスト節点を拾う形にする
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (inText)
                    text.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t": inText = !reader.IsEmptyElement; break;
                    case "tab": text.Append('\t'); break;
                    case "br" or "cr": text.Append('\n'); break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "t": inText = false; break;
                    case "p": text.Append('\n'); break;   // 段落
                    // 表: セル内の段落の改行は区切りに置き換える (改行とタブが二重にならないように)
                    case "tc": TrimEnd(text, '\n'); text.Append('\t'); break;
                    case "tr": TrimEnd(text, '\t'); text.Append('\n'); break;
                }
            }
        }
        return Trim(AlignTables(text.ToString()));
    }

    /// <summary>タブ区切りで出てきた表の部分だけ桁をそろえる (本文の行はそのまま)。</summary>
    private static string AlignTables(string text)
    {
        var result = new StringBuilder();
        var table = new List<string[]>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.Contains('\t'))
            {
                table.Add(line.Split('\t'));
                continue;
            }
            if (table.Count > 0)
            {
                result.Append(Table.Format(table));
                table.Clear();
            }
            result.Append(line).Append('\n');
        }
        if (table.Count > 0)
            result.Append(Table.Format(table));
        return result.ToString();
    }

    // ---- Excel ----

    private static string? FromExcel(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var result = new StringBuilder();

        foreach (var (name, entry) in EnumerateSheets(zip).Take(MaxSheets))
        {
            if (result.Length > MaxChars)
                break;
            if (result.Length > 0)
                result.Append('\n');
            result.Append("── ").Append(name).Append(" ──\n");
            result.Append(Table.Format(ReadSheetRows(entry, shared)));
        }
        return result.Length == 0 ? null : Trim(result.ToString());
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return strings;

        using var reader = OpenXml(entry);
        while (strings.Count < MaxSharedStrings && reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                continue;
            // <si> 1 つが 1 文字列 (書式が混ざると <r><t>…</t></r> に分かれる)
            var current = new StringBuilder();
            using var item = reader.ReadSubtree();
            while (item.Read())
            {
                if (item.NodeType == XmlNodeType.Element && item.LocalName == "t" && !item.IsEmptyElement)
                    current.Append(item.ReadElementContentAsString());
            }
            strings.Add(current.ToString());
        }
        return strings;
    }

    /// <summary>シートを (表示名, 実体) の組で、ブックに並んでいる順に返す。</summary>
    private static List<(string Name, ZipArchiveEntry Entry)> EnumerateSheets(ZipArchive zip)
    {
        var sheets = new List<(string, ZipArchiveEntry)>();
        var relations = ReadRelationships(zip);
        var workbook = zip.GetEntry("xl/workbook.xml");
        if (workbook is not null)
        {
            using var reader = OpenXml(workbook);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
                    continue;
                var name = reader.GetAttribute("name") ?? "シート";
                var id = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                if (id is not null && relations.TryGetValue(id, out var target)
                    && zip.GetEntry("xl/" + target.TrimStart('/')) is { } entry)
                    sheets.Add((name, entry));
            }
        }
        // 関連付けが読めない場合は sheet1.xml から順に拾う
        if (sheets.Count == 0)
        {
            for (int i = 1; zip.GetEntry($"xl/worksheets/sheet{i}.xml") is { } entry; i++)
                sheets.Add(($"シート{i}", entry));
        }
        return sheets;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null)
            return map;
        using var reader = OpenXml(entry);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                map[id] = target;
        }
        return map;
    }

    private static List<string[]> ReadSheetRows(ZipArchiveEntry entry, List<string> shared)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        using var reader = OpenXml(entry);
        while (rows.Count < MaxRows && reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                int column = ColumnIndex(reader.GetAttribute("r"));
                var type = reader.GetAttribute("t");
                var value = ReadCellValue(reader, type, shared);
                // 空のセルを飛ばさず、列の位置をそろえる
                if (column >= 0)
                {
                    while (cells.Count < Math.Min(column, MaxColumns))
                        cells.Add("");
                }
                if (cells.Count < MaxColumns)
                    cells.Add(value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                rows.Add(cells.ToArray());
                cells.Clear();
            }
        }
        return rows;
    }

    private static string ReadCellValue(XmlReader reader, string? type, List<string> shared)
    {
        if (reader.IsEmptyElement)
            return "";
        var text = new StringBuilder();
        // ReadSubtree で <c> の中だけを読む (自前で Read すると次のセルまで食べてしまう)
        using (var cell = reader.ReadSubtree())
        {
            while (cell.Read())
            {
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName is "v" or "t")
                    text.Append(cell.ReadElementContentAsString());
            }
        }
        var value = text.ToString();
        // t="s" は共有文字列表への番号
        if (type == "s" && int.TryParse(value, out int index) && index >= 0 && index < shared.Count)
            return shared[index];
        return value;
    }

    /// <summary>"B7" のようなセル番地から 0 始まりの列番号を得る (読めなければ -1)。</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return -1;
        int column = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
                column = column * 26 + (c - 'A' + 1);
            else if (c is >= 'a' and <= 'z')
                column = column * 26 + (c - 'a' + 1);
            else
                break;
        }
        return column - 1;
    }

    // ---- PowerPoint ----

    private static string? FromPowerPoint(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.Name))
            .Take(MaxSlides)
            .ToList();
        if (slides.Count == 0)
            return null;

        var text = new StringBuilder();
        int number = 0;
        foreach (var slide in slides)
        {
            number++;
            if (text.Length > MaxChars)
                break;
            text.Append(text.Length > 0 ? "\n" : "").Append($"── スライド {number} ──\n");
            using var reader = OpenXml(slide);
            bool inText = false;
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    if (inText)
                        text.Append(reader.Value);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                    inText = !reader.IsEmptyElement;
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.LocalName == "t")
                        inText = false;
                    else if (reader.LocalName == "p")
                        text.Append('\n');
                }
            }
        }
        return Trim(text.ToString());
    }

    private static int SlideNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
    }

    // ---- 書庫 ----

    private static string FromArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var rows = new List<string[]> { new[] { "名前", "サイズ", "更新日時" } };
        long total = 0;
        int shown = 0;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (shown >= MaxZipEntries)
                continue;
            shown++;
            // フォルダー自体の項目 (末尾が /) は大きさを出さない
            bool isFolder = entry.Name.Length == 0;
            rows.Add(new[]
            {
                entry.FullName,
                isFolder ? "" : FormatSize(entry.Length),
                entry.LastWriteTime.LocalDateTime.ToString("yyyy/MM/dd HH:mm"),
            });
        }
        var header = $"{zip.Entries.Count} 項目 / 展開後 {FormatSize(total)}\n\n";
        var footer = shown < zip.Entries.Count ? $"\n… 先頭 {shown} 件を表示" : "";
        return header + Table.Format(rows) + footer;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    // ---- CSV / TSV ----

    private static string? FromDelimited(string path, char separator)
    {
        var text = QuickLookWindow.ReadTextHead(path);
        if (text is null)
            return null;
        var rows = ParseDelimited(text, separator);
        return rows.Count == 0 ? text : Table.Format(rows);
    }

    /// <summary>引用符付き ("a,b" や "" による引用符自身) を解釈して行と列に分ける。</summary>
    private static List<string[]> ParseDelimited(string text, char separator)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length && rows.Count < MaxRows; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"')
                    cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                    quoted = false;
            }
            else if (c == '"' && cell.Length == 0)
                quoted = true;
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                cells.Add(cell.ToString());
                cell.Clear();
                rows.Add(cells.ToArray());
                cells.Clear();
            }
            else
                cell.Append(c);
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add(cells.ToArray());
        }
        return rows;
    }

    // ---- ショートカット ----

    private static string? FromShortcut(string path)
    {
        var link = Shortcut.Read(path);
        if (link is null)
            return null;
        var lines = new List<string> { $"リンク先: {link.Value.Target}" };
        if (!string.IsNullOrWhiteSpace(link.Value.Arguments))
            lines.Add($"引数: {link.Value.Arguments}");
        if (!string.IsNullOrWhiteSpace(link.Value.WorkingDirectory))
            lines.Add($"作業フォルダー: {link.Value.WorkingDirectory}");
        if (!string.IsNullOrWhiteSpace(link.Value.Description))
            lines.Add($"コメント: {link.Value.Description}");
        if (link.Value.Target.Length > 0 && !File.Exists(link.Value.Target) && !Directory.Exists(link.Value.Target))
            lines.Add("\n(リンク先が見つかりません)");
        return string.Join("\n", lines);
    }

    // ---- 共通 ----

    /// <summary>末尾に続く文字を取り除く (表のセル・行の区切りを重ねないため)。</summary>
    private static void TrimEnd(StringBuilder text, char c)
    {
        while (text.Length > 0 && text[^1] == c)
            text.Length--;
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry) =>
        // 外部参照 (DTD) は一切たどらない
        XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

    private static string? Trim(string text)
    {
        // 空段落が続くと間延びするので 2 行までに詰める
        var trimmed = text.Replace("\r", "").Trim('\n', ' ', '\t');
        while (trimmed.Contains("\n\n\n"))
            trimmed = trimmed.Replace("\n\n\n", "\n\n");
        if (trimmed.Length >= MaxChars)
            trimmed = trimmed[..MaxChars] + "\n\n… (以降は省略)";
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>等幅フォントで桁が揃うように整形する。全角文字は 2 桁分として数える。</summary>
    private static class Table
    {
        private const int MaxCellWidth = 40;

        public static string Format(List<string[]> rows)
        {
            if (rows.Count == 0)
                return "";
            int columns = Math.Min(rows.Max(r => r.Length), MaxColumns);
            var widths = new int[columns];
            foreach (var row in rows)
            {
                for (int i = 0; i < columns && i < row.Length; i++)
                    widths[i] = Math.Max(widths[i], Width(Clip(row[i])));
            }

            var text = new StringBuilder();
            foreach (var row in rows)
            {
                for (int i = 0; i < columns; i++)
                {
                    var cell = Clip(i < row.Length ? row[i] : "");
                    text.Append(cell);
                    if (i < columns - 1)
                        text.Append(new string(' ', widths[i] - Width(cell) + 2));
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        private static string Clip(string cell)
        {
            cell = cell.Replace("\r", "").Replace("\n", " ").Replace("\t", " ");
            return cell.Length > MaxCellWidth ? cell[..MaxCellWidth] + "…" : cell;
        }

        /// <summary>等幅フォントでの見た目の桁数 (全角・絵文字は 2)。</summary>
        private static int Width(string text)
        {
            int width = 0;
            foreach (var c in text)
                width += IsWide(c) ? 2 : 1;
            return width;
        }

        private static bool IsWide(char c) =>
            c is >= 'ᄀ' and <= 'ᅟ'      // ハングル字母
            or >= '⺀' and <= '꓏'        // CJK 部首〜漢字・かな
            or >= '가' and <= '힣'        // ハングル
            or >= '豈' and <= '﫿'        // CJK 互換漢字
            or >= '︰' and <= '﹯'        // CJK 互換記号
            or >= '＀' and <= '｠'        // 全角英数・記号
            or >= '￠' and <= '￦';       // 全角通貨記号
    }
}

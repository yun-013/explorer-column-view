using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ColumnView;

/// <summary>
/// 最終列 (最右) の幅をファイル名に合わせて自動調整するための実測ヘルパー。
/// 全件を FormattedText で実測すると大フォルダーで重くなるため、
/// 文字数ベースの概算で候補を上位数件に絞ってから実測する 2 段構えにしている。
/// </summary>
public static class ColumnMetrics
{
    /// <summary>通常列の既定幅。自動調整の下限でもある。</summary>
    public const double DefaultWidth = 216;

    /// <summary>検索結果列の既定幅 (ファイル名と場所が読めるよう広め)。</summary>
    public const double SearchWidth = 300;

    /// <summary>自動調整の上限。これより長い名前は従来どおり省略記号で切る。</summary>
    public const double MaxWidth = 400;

    /// <summary>名前テキスト以外が行と列に占める幅
    /// (行余白 10 + 行パディング 14 + アイコン列 30 + 名前余白 4 + バッジ/シェブロン ~29 + 縦スクロールバー等)。</summary>
    private const double Chrome = 100;

    /// <summary>行のファイル名と揃える書体 (Window の SystemFonts.MessageFontFamily / 13px)。</summary>
    private const double NameFontSize = 13.0;

    /// <summary>検索結果の「場所」行の書体サイズ (FileItemTemplate と揃える)。</summary>
    private const double LocationFontSize = 10.5;

    /// <summary>精密測定する候補数。概算で長い順にこの件数だけ実測し、全件測定を避ける。</summary>
    private const int PreciseCount = 8;

    private static readonly Typeface Face = new(
        SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>項目一覧に合わせた列幅を返す (min〜MaxWidth に収める)。UI スレッドから呼ぶこと。</summary>
    public static double Fit(IReadOnlyList<FileSystemItem> items, double min)
    {
        if (items.Count == 0)
            return min;

        // 1 パス目: 文字種の重み付き文字数 (全角≒1em / 半角≒0.62em) で概算し、上位候補だけ残す
        var texts = new string[PreciseCount];
        var sizes = new double[PreciseCount];
        var ests = new double[PreciseCount];
        var count = 0;

        void Consider(string? text, double fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return;
            var est = Estimate(text) * fontSize;
            if (count == PreciseCount && est <= ests[count - 1])
                return;
            // 挿入ソートで概算幅の降順に上位 PreciseCount 件のみ保持
            var at = count < PreciseCount ? count : PreciseCount - 1;
            while (at > 0 && ests[at - 1] < est)
            {
                texts[at] = texts[at - 1];
                sizes[at] = sizes[at - 1];
                ests[at] = ests[at - 1];
                at--;
            }
            texts[at] = text;
            sizes[at] = fontSize;
            ests[at] = est;
            if (count < PreciseCount)
                count++;
        }

        foreach (var item in items)
        {
            Consider(item.Name, NameFontSize);
            Consider(item.Location, LocationFontSize); // 検索結果のみ非 null
        }

        // 2 パス目: 候補のみ実測して最大値を採用
        var dpi = Application.Current?.MainWindow is { } w
            ? VisualTreeHelper.GetDpi(w).PixelsPerDip : 1.0;
        double widest = 0;
        for (var i = 0; i < count; i++)
        {
            var ft = new FormattedText(texts[i], CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Face, sizes[i], Brushes.Black, dpi);
            widest = Math.Max(widest, ft.WidthIncludingTrailingWhitespace);
        }

        return Math.Clamp(Math.Ceiling(widest) + Chrome, min, MaxWidth);
    }

    /// <summary>em 単位の概算幅。候補の絞り込みにだけ使うので精度は要らない。</summary>
    private static double Estimate(string text)
    {
        double width = 0;
        foreach (var ch in text)
            width += ch <= 0x7F ? 0.62 : 1.0;
        return width;
    }
}

using System.Windows;

namespace ColumnView;

/// <summary>お気に入り並べ替え時の挿入位置インジケーター (項目の上端 / 下端に線を出す)。</summary>
public enum InsertSide { None, Before, After }

/// <summary>ドロップで実際に行う操作。</summary>
public enum DropMode { Move, Copy, Link }

/// <summary>ドロップ先が返した効果を、ゴースト表示用に畳んだもの。</summary>
/// <remarks>
/// OLE の建前では <see cref="System.Windows.DragDropEffects"/> は「実際に起きる 1 つの効果」だが、
/// 実装によっては許可マスクをそのまま返してくる (エクスプローラーで Copy|Move を実測)。
/// どちらになるかはドロップした瞬間にドロップ先が決めるので、こちらからは断定できない。
/// その状態を <see cref="Ambiguous"/> として区別し、嘘の操作名を出さないようにする。
/// </remarks>
public enum DropIntent { None, Copy, Move, Link, Ambiguous }

/// <summary>
/// ドラッグ中にドロップ先フォルダを強調するための添付プロパティ。
/// ListBoxItem に設定し、ItemContainerStyle のトリガーで背景を切り替える。
/// </summary>
public static class DragDropHelper
{
    public static readonly DependencyProperty DropHighlightProperty =
        DependencyProperty.RegisterAttached(
            "DropHighlight", typeof(bool), typeof(DragDropHelper),
            new PropertyMetadata(false));

    public static bool GetDropHighlight(DependencyObject o) => (bool)o.GetValue(DropHighlightProperty);
    public static void SetDropHighlight(DependencyObject o, bool value) => o.SetValue(DropHighlightProperty, value);

    /// <summary>お気に入りを並べ替える際、この項目のどちら側に差し込むかを示す。</summary>
    public static readonly DependencyProperty DropInsertProperty =
        DependencyProperty.RegisterAttached(
            "DropInsert", typeof(InsertSide), typeof(DragDropHelper),
            new PropertyMetadata(InsertSide.None));

    public static InsertSide GetDropInsert(DependencyObject o) => (InsertSide)o.GetValue(DropInsertProperty);
    public static void SetDropInsert(DependencyObject o, InsertSide value) => o.SetValue(DropInsertProperty, value);
}

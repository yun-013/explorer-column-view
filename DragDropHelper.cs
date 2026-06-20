using System.Windows;

namespace ColumnView;

/// <summary>お気に入り並べ替え時の挿入位置インジケーター (項目の上端 / 下端に線を出す)。</summary>
public enum InsertSide { None, Before, After }

/// <summary>
/// ドラッグ中にドロップ先フォルダーを強調するための添付プロパティ。
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

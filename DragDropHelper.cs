using System.Windows;

namespace ColumnView;

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
}

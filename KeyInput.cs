using System.Windows.Input;

namespace ColumnView;

internal static class KeyInput
{
    /// <summary>キー判定に使う実際のキー。
    /// 日本語入力 (IME) がオンのとき、スペースは IME が先に受け取るため e.Key は Key.ImeProcessed になり、
    /// 押されたキーは e.ImeProcessedKey にしか入らない。そのまま e.Key を見ると IME オンの間だけ
    /// スペースでのプレビューが効かなくなるので、スペースは本来のキーとして扱う。
    /// (ほかのキーは IME の変換中に確定・取り消しへ使われるので、ここでは読み替えない)</summary>
    public static Key EffectiveKey(this KeyEventArgs e)
        => e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Space ? Key.Space : e.Key;
}

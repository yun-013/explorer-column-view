using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColumnView;

public class AppSettings
{
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; } = true;
    public bool ShowHidden { get; set; }
    public List<string> Favorites { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColumnView", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception)
        {
            // 壊れた設定ファイルは既定値で上書き
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // 保存失敗は致命的ではないので無視 (次回起動時は既定値)
        }
    }

    public bool IsFavorite(string path)
        => Favorites.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>追加したら true、削除したら false を返す</summary>
    public bool ToggleFavorite(string path)
    {
        var existing = Favorites.FirstOrDefault(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Favorites.Remove(existing);
            Save();
            return false;
        }
        Favorites.Add(path);
        Save();
        return true;
    }

    /// <summary>お気に入り <paramref name="source"/> を <paramref name="target"/> の直前 / 直後へ移動する。</summary>
    public bool MoveFavorite(string source, string target, bool insertAfter)
    {
        var srcIndex = Favorites.FindIndex(f => string.Equals(f, source, StringComparison.OrdinalIgnoreCase));
        if (srcIndex < 0)
            return false;

        var item = Favorites[srcIndex];
        Favorites.RemoveAt(srcIndex);

        var dstIndex = Favorites.FindIndex(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
        if (dstIndex < 0)
        {
            // 並べ替え対象が見つからない (= source 自身など): 元に戻して何もしない
            Favorites.Insert(srcIndex, item);
            return false;
        }
        if (insertAfter)
            dstIndex++;

        Favorites.Insert(dstIndex, item);
        Save();
        return true;
    }
}

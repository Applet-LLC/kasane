using System.Text.Json;

namespace Kasane.Tray;

/// <summary>自動で OFF にしたキーボードのIDを、設定フォルダの tray-autooff.json に残す。壊れていたら「記録なし」として扱う。</summary>
public sealed class JsonAutoOffStore : IAutoOffStore
{
    private sealed class State { public List<uint> Devices { get; set; } = new(); }

    private readonly string _path;

    public JsonAutoOffStore(string? path = null)
    {
        _path = path ?? Path.Combine(Kasane.Model.HelperConfig.BaseDirectory, "tray-autooff.json");
    }

    public IReadOnlyList<uint> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<uint>();
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            return state?.Devices ?? new List<uint>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<uint>();
        }
    }

    public void Save(IReadOnlyList<uint> uniqIds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new State { Devices = uniqIds.Distinct().ToList() }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録できないと、次の起動で戻せない。トレイは続ける（記録できなかったことは、呼び出し側が知らない）。
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

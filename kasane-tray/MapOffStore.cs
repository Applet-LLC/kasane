using System.Text.Json;

namespace Kasane.Tray;

/// <summary>
/// 前面のアプリの規則でリマップを OFF にしたキーボードの ID を、%LOCALAPPDATA%\applet\kasane\tray-mapoff.json に残す
/// （<see cref="JsonPauseStateStore"/> と同じ形）。壊れていたら「記録なし」として扱う。
/// </summary>
public sealed class JsonMapOffStore : IMapOffStore
{
    private sealed class State { public List<uint> MapOffDevices { get; set; } = new(); }

    private readonly string _path;

    public JsonMapOffStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "tray-mapoff.json");
    }

    public IReadOnlyList<uint> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<uint>();
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            return state?.MapOffDevices ?? new List<uint>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<uint>();
        }
    }

    public void Save(IReadOnlyList<uint> uniqIds)
    {
        if (uniqIds.Count == 0)
        {
            try { File.Delete(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new State { MapOffDevices = uniqIds.Distinct().ToList() }));
    }
}

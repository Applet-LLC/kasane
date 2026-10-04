using System.Text.Json;

namespace Kasane.Tray;

/// <summary>止めたキーボードのIDを、%LOCALAPPDATA%\applet\kasane\tray-state.json に残す。壊れていたら「記録なし」として扱う。</summary>
public sealed class JsonPauseStateStore : IPauseStateStore
{
    private sealed class State { public List<uint> PausedDevices { get; set; } = new(); }

    private readonly string _path;

    public JsonPauseStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "tray-state.json");
    }

    public IReadOnlyList<uint> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<uint>();
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            return state?.PausedDevices ?? new List<uint>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<uint>();
        }
    }

    public void Save(IReadOnlyList<uint> uniqIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new State { PausedDevices = uniqIds.Distinct().ToList() }));
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

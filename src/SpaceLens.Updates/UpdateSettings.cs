using System.Text.Json;

namespace SpaceLens.Updates;

public sealed class UpdateSettings(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "settings.json");
    public bool Automatic { get; private set; } = true;

    public void Load()
    {
        if (!File.Exists(path)) return;
        try { Automatic = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path))?.AutomaticUpdates ?? false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Automatic = false; }
    }

    public void Save(bool automatic)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(automatic)));
            File.Move(temporary, path, overwrite: true);
            Automatic = automatic;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Preferences(bool AutomaticUpdates);
}

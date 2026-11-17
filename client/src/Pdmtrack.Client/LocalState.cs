using System.Security.Cryptography;
using System.Text.Json;

namespace Pdmtrack.Client;

/// <summary>
/// Cosa e' stato scaricato nella cartella di lavoro (LocalRoot\.pdmtrack\state.json):
/// serve a distinguere un file modificato in locale da uno solo vecchio.
/// </summary>
public sealed class LocalState
{
    public sealed record Entry(string Filename, string Path, int Version, string Sha256);

    public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FileOf(string root) => System.IO.Path.Combine(root, ".pdmtrack", "state.json");

    public static LocalState Load(string root)
    {
        var f = FileOf(root);
        if (!File.Exists(f)) return new();
        var s = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(f)) ?? new();
        s.Files = new(s.Files, StringComparer.OrdinalIgnoreCase);
        return s;
    }

    public void Save(string root)
    {
        var f = FileOf(root);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
        File.WriteAllText(f, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static void SetReadOnly(string path, bool readOnly)
    {
        var a = File.GetAttributes(path);
        File.SetAttributes(path, readOnly ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly);
    }

    public static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
}

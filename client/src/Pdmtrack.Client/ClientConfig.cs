using System.Text.Json;

namespace Pdmtrack.Client;

/// <summary>Configurazione del client, in %APPDATA%\Pdmtrack\config.json (o nel file indicato da PDM_CONFIG).</summary>
public sealed class ClientConfig
{
    /// <summary>Indirizzo dell'API, per esempio http://pdm-server:5000</summary>
    public string ServerUrl { get; set; } = "http://localhost:5122";
    /// <summary>Cartella di lavoro locale: rispecchia il vault, cosi' SolidWorks trova i riferimenti.</summary>
    public string LocalRoot { get; set; } = OperatingSystem.IsWindows() ? @"C:\PDM" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PDM");
    /// <summary>Prefisso dei percorsi nel vault (files.folder_path), sostituito da LocalRoot.</summary>
    public string VaultPrefix { get; set; } = @"\\PDM\";
    /// <summary>Solo sviluppo: utente inviato in X-Pdm-User invece del login Windows.</summary>
    public string? DevUser { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Environment.GetEnvironmentVariable("PDM_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pdmtrack", "config.json");

    public static ClientConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path)) ?? new() : new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>\\PDM\Pompe\PG-25\ + 111-0001.SLDPRT -> C:\PDM\Pompe\PG-25\111-0001.SLDPRT</summary>
    public string LocalPath(string folderPath, string filename)
    {
        var rel = folderPath.StartsWith(VaultPrefix, StringComparison.OrdinalIgnoreCase)
            ? folderPath[VaultPrefix.Length..]
            : folderPath.TrimStart('\\');
        var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([LocalRoot, .. parts, filename]);
    }

    /// <summary>C:\PDM\Pompe\PG-25\x.SLDPRT -> \\PDM\Pompe\PG-25\</summary>
    public string VaultFolder(string localFile)
    {
        var dir = Path.GetRelativePath(LocalRoot, Path.GetDirectoryName(Path.GetFullPath(localFile))!);
        if (dir.StartsWith("..")) throw new PdmException($"Il file deve stare dentro la cartella di lavoro {LocalRoot}");
        var rel = dir == "." ? "" : dir.Replace(Path.DirectorySeparatorChar, '\\') + "\\";
        return VaultPrefix + rel;
    }
}

public sealed class PdmException(string message) : Exception(message);

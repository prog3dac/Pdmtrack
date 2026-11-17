namespace Pdmtrack.Client;

/// <summary>pdm: client da riga di comando del vault Pdmtrack.</summary>
public static class Cli
{
    private const string Help = """
        pdm - client del vault Pdmtrack

        Uso:
          pdm config [--server URL] [--root CARTELLA] [--prefix \\PDM\] [--dev-user LOGIN]
          pdm whoami
          pdm status
          pdm get CODICE [--rev REVISIONE] [--force]     scarica il file e tutti i suoi riferimenti
          pdm checkout CODICE [CODICE...] [--force]      blocca e rende scrivibili (tutti o nessuno)
          pdm checkin CODICE [-m COMMENTO] [--refs FILE1,FILE2:VERSIONE]
          pdm undo CODICE                                annulla il check-out e ripristina il file
          pdm add FILE --code CODICE --desc DESCRIZIONE [--material M] [--mass KG] [--ref CODICE_MODELLO]
                  [--refs FILE1,FILE2] [-m COMMENTO]

        La cartella di lavoro (predefinita C:\PDM) rispecchia il vault: \\PDM\Pompe\x.SLDPRT -> C:\PDM\Pompe\x.SLDPRT.
        Senza check-out i file sono in sola lettura. Un file modificato in locale non viene mai sovrascritto senza --force.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help") { Console.WriteLine(Help); return 0; }
        var (pos, opt) = Parse(args[1..]);
        var cfg = ClientConfig.Load();
        try
        {
            if (args[0] == "config")
            {
                if (opt.TryGetValue("server", out var s)) cfg.ServerUrl = s;
                if (opt.TryGetValue("root", out var r)) cfg.LocalRoot = Path.GetFullPath(r);
                if (opt.TryGetValue("prefix", out var p)) cfg.VaultPrefix = p;
                if (opt.TryGetValue("dev-user", out var d)) cfg.DevUser = d.Length == 0 ? null : d;
                cfg.Save();
                Console.WriteLine($"Server {cfg.ServerUrl} · cartella {cfg.LocalRoot} · prefisso {cfg.VaultPrefix}" +
                                  (cfg.DevUser is null ? "" : $" · utente di sviluppo {cfg.DevUser}") +
                                  $"\nSalvato in {ClientConfig.DefaultPath}");
                return 0;
            }
            using var http = VaultClient.CreateHttp(cfg);
            var c = new VaultClient(http, cfg, Console.Out);
            switch (args[0])
            {
                case "whoami":
                    var me = await c.Me();
                    Console.WriteLine($"{me.Name} ({me.Login}, {me.Role})");
                    break;
                case "status": await c.Status(); break;
                case "get":
                    await c.GetLatest(One(pos), opt.GetValueOrDefault("rev"), opt.ContainsKey("force"));
                    break;
                case "checkout":
                    if (pos.Count == 0) throw new PdmException("Indica almeno un codice");
                    await c.CheckOut(pos, opt.ContainsKey("force"));
                    break;
                case "checkin":
                    await c.CheckIn(One(pos), opt.GetValueOrDefault("m"), Refs(opt.GetValueOrDefault("refs")));
                    break;
                case "undo": await c.UndoCheckOut(One(pos)); break;
                case "add":
                    await c.Add(One(pos), Need(opt, "code"), Need(opt, "desc"), opt.GetValueOrDefault("material"),
                        opt.TryGetValue("mass", out var m) ? decimal.Parse(m.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) : null,
                        opt.GetValueOrDefault("ref"), Refs(opt.GetValueOrDefault("refs")), opt.GetValueOrDefault("m"));
                    break;
                default:
                    Console.Error.WriteLine($"Comando sconosciuto: {args[0]}\n\n{Help}");
                    return 2;
            }
            return 0;
        }
        catch (PdmException e) { Console.Error.WriteLine("Errore: " + e.Message); return 1; }
        catch (HttpRequestException e) { Console.Error.WriteLine($"Server {cfg.ServerUrl} non raggiungibile: {e.Message}"); return 1; }
    }

    /// <summary>Argomenti posizionali e opzioni (--nome valore, -m valore, --flag).</summary>
    public static (List<string> pos, Dictionary<string, string> opt) Parse(string[] args)
    {
        var pos = new List<string>();
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-') && args[i].Length > 1)
            {
                var key = args[i].TrimStart('-');
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--") && key is not ("force");
                opt[key] = hasValue ? args[++i] : "";
            }
            else pos.Add(args[i]);
        }
        return (pos, opt);
    }

    /// <summary>"a.SLDPRT,b.SLDPRT:3" -> riferimenti (versione facoltativa)</summary>
    public static List<FileRef>? Refs(string? s) => s is null ? null
        : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x =>
        {
            var p = x.Split(':');
            return new FileRef(p[0], p.Length > 1 ? int.Parse(p[1]) : null);
        }).ToList();

    private static string One(List<string> pos) =>
        pos.Count == 1 ? pos[0] : throw new PdmException("Indica un solo codice articolo (o file per add)");

    private static string Need(Dictionary<string, string> opt, string key) =>
        opt.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new PdmException($"Opzione --{key} obbligatoria");
}

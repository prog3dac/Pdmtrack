using Pdmtrack.Client;

namespace Pdmtrack.Api.Tests;

/// <summary>Il client pdm contro l'API reale: due utenti con due cartelle di lavoro separate.</summary>
public class ClientTests(PdmApiFactory app) : IClassFixture<PdmApiFactory>, IDisposable
{
    private readonly List<string> _roots = [];

    private (VaultClient client, ClientConfig cfg, StringWriter log) User(string login)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pdm-{login}-{Guid.NewGuid():N}");
        _roots.Add(root);
        var cfg = new ClientConfig { LocalRoot = root, DevUser = login };
        var log = new StringWriter();
        return (new VaultClient(app.CreateClient(), cfg, log), cfg, log);
    }

    private static string Write(ClientConfig cfg, string rel, string content)
    {
        var path = Path.Combine([cfg.LocalRoot, .. rel.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) LocalState.SetReadOnly(path, false);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Ciclo_completo_tra_due_utenti()
    {
        var (rossi, rcfg, _) = User("mrossi");
        var (cavani, ccfg, clog) = User("acavani");

        // M. Rossi aggiunge una parte e un assieme che la usa
        var part = Write(rcfg, "Pompe/Prova/160-0001.SLDPRT", "parte v1");
        await rossi.Add(part, "160-0001", "Perno di prova", "C45", 0.12m, null, null, null);
        Assert.True(LocalState.IsReadOnly(part));
        var asm = Write(rcfg, "Pompe/Prova/160-0010.SLDASM", "assieme v1");
        await rossi.Add(asm, "160-0010", "Gruppo di prova", null, null, null, [new Pdmtrack.Client.FileRef("160-0001.SLDPRT")], null);
        var item = (await rossi.Item("160-0010")).Item;
        Assert.Equal(@"\\PDM\Pompe\Prova\", item.FolderPath);

        // A. Cavani scarica l'assieme: arrivano entrambi i file, in sola lettura, nella stessa struttura
        Assert.Equal(2, await cavani.GetLatest("160-0010"));
        var cPart = rcfgPath(ccfg, "160-0001.SLDPRT");
        Assert.Equal("parte v1", File.ReadAllText(cPart));
        Assert.True(LocalState.IsReadOnly(cPart));

        // Check-out, modifica, check-in
        await cavani.CheckOut(["160-0001"]);
        Assert.False(LocalState.IsReadOnly(cPart));
        Write(ccfg, "Pompe/Prova/160-0001.SLDPRT", "parte v2");
        Assert.Equal(2, await cavani.CheckIn("160-0001", "Smusso 1x45"));
        Assert.True(LocalState.IsReadOnly(cPart));

        // Check-in senza modifiche = annulla check-out
        await cavani.CheckOut(["160-0001"]);
        Assert.Null(await cavani.CheckIn("160-0001", null));
        Assert.Null((await cavani.Item("160-0001")).Item.LockedBy);

        // M. Rossi: la sua copia v1 nota viene aggiornata, una modifica locale non viene toccata
        Assert.Equal(1, await rossi.GetLatest("160-0010"));
        Assert.Equal("parte v2", File.ReadAllText(part));
        Write(rcfg, "Pompe/Prova/160-0001.SLDPRT", "modifica non salvata nel vault");
        Assert.Equal(0, await rossi.GetLatest("160-0010"));
        Assert.Equal("modifica non salvata nel vault", File.ReadAllText(part));
        Assert.Equal(1, await rossi.GetLatest("160-0010", force: true));
        Assert.Equal("parte v2", File.ReadAllText(part));

        // Check-in dell'assieme: i riferimenti seguono l'ultima versione della parte
        await rossi.CheckOut(["160-0010"]);
        Write(rcfg, "Pompe/Prova/160-0010.SLDASM", "assieme v2");
        Assert.Equal(2, await rossi.CheckIn("160-0010", "Nuova quota"));
        var exact = await app.As("mrossi").GetStringAsync("/api/items/160-0010/tree"); // versioni registrate
        Assert.Contains(System.Text.Json.JsonDocument.Parse(exact).RootElement.EnumerateArray(),
            r => r.GetProperty("filename").GetString() == "160-0001.SLDPRT" && r.GetProperty("version").GetInt32() == 2);

        // Annulla check-out: il file locale torna alla versione del vault
        await cavani.CheckOut(["160-0001"]);
        Write(ccfg, "Pompe/Prova/160-0001.SLDPRT", "prova da buttare");
        await cavani.UndoCheckOut("160-0001");
        Assert.Equal("parte v2", File.ReadAllText(cPart));
        Assert.True(LocalState.IsReadOnly(cPart));
        Assert.Null((await cavani.Item("160-0001")).Item.LockedBy);

        // Check-out di un file bloccato da altri: errore chiaro, nessun blocco parziale
        await rossi.CheckOut(["160-0001"]);
        var ex = await Assert.ThrowsAsync<PdmException>(() => cavani.CheckOut(["160-0010", "160-0001"]));
        Assert.Contains("altro utente", ex.Message);
        Assert.Null((await cavani.Item("160-0010")).Item.LockedBy);

        // Stato della cartella di lavoro
        Write(ccfg, "Pompe/Prova/nuovo.SLDPRT", "mai aggiunto");
        await cavani.Status();
        Assert.Contains("nuovo.SLDPRT: non nel vault", clog.ToString());
    }

    private static string rcfgPath(ClientConfig cfg, string filename) => cfg.LocalPath(@"\\PDM\Pompe\Prova\", filename);

    [Fact]
    public async Task Errori_leggibili()
    {
        var (c, cfg, _) = User("acavani");
        var ex = await Assert.ThrowsAsync<PdmException>(() => c.GetLatest("999-9999"));
        Assert.Contains("non trovato", ex.Message);
        var outside = Path.Combine(Path.GetTempPath(), $"fuori-{Guid.NewGuid():N}.SLDPRT");
        File.WriteAllText(outside, "x");
        try
        {
            var ex2 = await Assert.ThrowsAsync<PdmException>(() => c.Add(outside, "170-0001", "x", null, null, null, null, null));
            Assert.Contains("cartella di lavoro", ex2.Message);
        }
        finally { File.Delete(outside); }
        var (nobody, _, _) = User("sconosciuto");
        Assert.Contains("non registrato", (await Assert.ThrowsAsync<PdmException>(() => nobody.Me())).Message);
    }

    [Fact]
    public void Percorsi_e_opzioni()
    {
        var cfg = new ClientConfig { LocalRoot = Path.Combine(Path.GetTempPath(), "PDM") };
        var local = cfg.LocalPath(@"\\PDM\Pompe\PG-25\", "111-0001.SLDPRT");
        Assert.Equal(Path.Combine(cfg.LocalRoot, "Pompe", "PG-25", "111-0001.SLDPRT"), local);
        Assert.Equal(@"\\PDM\Pompe\PG-25\", cfg.VaultFolder(local));
        Assert.Equal(@"\\PDM\", cfg.VaultFolder(Path.Combine(cfg.LocalRoot, "x.SLDPRT")));
        var refs = Cli.Refs("a.SLDPRT, b.SLDPRT:3")!;
        Assert.Equal([new Pdmtrack.Client.FileRef("a.SLDPRT"), new Pdmtrack.Client.FileRef("b.SLDPRT", 3)], refs);
        var (pos, opt) = Cli.Parse(["120-0001", "-m", "Diametro 20", "--force"]);
        Assert.Equal(["120-0001"], pos);
        Assert.Equal("Diametro 20", opt["m"]);
        Assert.True(opt.ContainsKey("force"));
    }

    public void Dispose()
    {
        foreach (var r in _roots.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories)) LocalState.SetReadOnly(f, false);
            Directory.Delete(r, true);
        }
    }
}

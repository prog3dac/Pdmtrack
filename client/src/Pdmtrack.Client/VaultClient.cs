using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pdmtrack.Client;

// Risposte dell'API usate dal client
public record Me(string Login, string Name, string Role);
public record ItemRow(string Code, string Description, string Type, string Revision, string State, string? Filename,
                      string? FolderPath, int? CurrentVersion, string? LockedBy);
public record VersionRow(int Version, string Revision, string Sha256);
public record ItemDetail(ItemRow Item, List<VersionRow> Versions);
public record TreeRow(int Level, string Filename, string FolderPath, int Version, string Sha256, long SizeBytes);
public record FileRef(string Filename, int? Version = null);

/// <summary>
/// Operazioni sul vault dal punto di vista della cartella di lavoro locale.
/// Regola di sicurezza: un file locale modificato non viene mai sovrascritto senza --force.
/// </summary>
public sealed class VaultClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient _http;
    private readonly ClientConfig _cfg;
    private readonly TextWriter _out;

    public VaultClient(HttpClient http, ClientConfig cfg, TextWriter output)
    {
        _http = http; _cfg = cfg; _out = output;
        if (cfg.DevUser is { Length: > 0 } u) _http.DefaultRequestHeaders.Add("X-Pdm-User", u);
    }

    /// <summary>Client HTTP per l'uso reale: login Windows (Kerberos/NTLM) con l'utente corrente.</summary>
    public static HttpClient CreateHttp(ClientConfig cfg) =>
        new(new HttpClientHandler { UseDefaultCredentials = true }) { BaseAddress = new Uri(cfg.ServerUrl), Timeout = TimeSpan.FromMinutes(30) };

    // ---- HTTP ---------------------------------------------------------------------------------

    private async Task<T> Read<T>(HttpResponseMessage r)
    {
        await Ensure(r);
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task Ensure(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return;
        string msg = $"Errore {(int)r.StatusCode}";
        try { msg = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString() ?? msg; }
        catch { /* corpo non JSON */ }
        if (r.StatusCode == HttpStatusCode.Unauthorized) msg = "Accesso non riuscito: controlla di essere in rete aziendale con il tuo account Windows";
        throw new PdmException(msg);
    }

    private async Task<T> Get<T>(string path) => await Read<T>(await _http.GetAsync("/api" + path));
    private async Task Post(string path, object? body) =>
        await Ensure(await _http.PostAsJsonAsync("/api" + path, body, Json));
    private async Task<T> Post<T>(string path, object? body) =>
        await Read<T>(await _http.PostAsJsonAsync("/api" + path, body, Json));

    private static string Esc(string code) => Uri.EscapeDataString(code);

    public Task<Me> Me() => Get<Me>("/me");
    public Task<ItemDetail> Item(string code) => Get<ItemDetail>($"/items/{Esc(code)}");
    /// <summary>Senza revisione: versioni correnti di tutti i file (get latest). Con revisione: versioni registrate.</summary>
    public Task<List<TreeRow>> Tree(string code, string? revision = null) =>
        Get<List<TreeRow>>($"/items/{Esc(code)}/tree" + (revision is null ? "?latest=true" : $"?revision={Uri.EscapeDataString(revision)}"));

    private async Task Upload(string path, string sha)
    {
        if ((await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{sha}"))).IsSuccessStatusCode) return;
        await using var s = File.OpenRead(path);
        await Ensure(await _http.PutAsync($"/api/blobs/{sha}", new StreamContent(s)));
    }

    // ---- Comandi ------------------------------------------------------------------------------

    /// <summary>Scarica nella cartella locale tutti i file che servono ad aprire l'articolo (o la sua revisione).</summary>
    public async Task<int> GetLatest(string code, string? revision = null, bool force = false, bool onlyRoot = false)
    {
        var state = LocalState.Load(_cfg.LocalRoot);
        var rows = await Tree(code, revision);
        if (onlyRoot) rows = rows.Where(r => r.Level == 0).ToList();
        int downloaded = 0;
        foreach (var row in rows)
        {
            var path = _cfg.LocalPath(row.FolderPath, row.Filename);
            if (File.Exists(path))
            {
                var local = LocalState.Sha256Of(path);
                if (local == row.Sha256)
                {
                    state.Files[row.Filename] = new(row.Filename, path, row.Version, row.Sha256);
                    continue;
                }
                var known = state.Files.TryGetValue(row.Filename, out var e) && e.Sha256 == local;
                if (!known && !force)
                {
                    _out.WriteLine($"  ! {row.Filename}: modificato in locale, non sovrascritto (usa --force per sostituirlo)");
                    continue;
                }
            }
            await Download(row, path);
            state.Files[row.Filename] = new(row.Filename, path, row.Version, row.Sha256);
            _out.WriteLine($"  ↓ {row.Filename} v{row.Version}");
            downloaded++;
        }
        state.Save(_cfg.LocalRoot);
        _out.WriteLine($"{code}: {rows.Count} file, {downloaded} scaricati");
        return downloaded;
    }

    private async Task Download(TreeRow row, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".pdmdownload";
        using (var r = await _http.GetAsync($"/api/blobs/{row.Sha256}", HttpCompletionOption.ResponseHeadersRead))
        {
            if (r.StatusCode == HttpStatusCode.NotFound)
                throw new PdmException($"{row.Filename} v{row.Version}: contenuto assente dallo storage del server");
            await Ensure(r);
            await using var f = File.Create(tmp);
            await r.Content.CopyToAsync(f);
        }
        if (LocalState.Sha256Of(tmp) != row.Sha256)
        {
            File.Delete(tmp);
            throw new PdmException($"{row.Filename}: contenuto scaricato corrotto (hash diverso)");
        }
        if (File.Exists(path)) LocalState.SetReadOnly(path, false);
        File.Move(tmp, path, overwrite: true);
        LocalState.SetReadOnly(path, true); // senza check-out SolidWorks lo apre in sola lettura
    }

    /// <summary>Blocca i file (tutti o nessuno), scarica l'ultima versione e li rende scrivibili.</summary>
    public async Task CheckOut(IReadOnlyList<string> codes, bool force = false)
    {
        var r = await Post<JsonElement>("/checkout", new { codes });
        foreach (var code in codes)
        {
            await GetLatest(code, force: force, onlyRoot: true);
            var item = (await Item(code)).Item;
            var path = _cfg.LocalPath(item.FolderPath!, item.Filename!);
            if (File.Exists(path)) LocalState.SetReadOnly(path, false);
        }
        _out.WriteLine($"Check-out eseguito: {string.Join(", ", codes)} ({r.GetProperty("locked").GetInt32()} nuovi blocchi)");
    }

    /// <summary>
    /// Carica il file locale come nuova versione. Senza --refs, per assiemi e disegni riusa i riferimenti
    /// della versione precedente aggiornati all'ultima versione (in fase 2 li leggera' l'add-in).
    /// </summary>
    public async Task<int?> CheckIn(string code, string? comment, IReadOnlyList<FileRef>? refs = null)
    {
        var d = await Item(code);
        var path = _cfg.LocalPath(d.Item.FolderPath!, d.Item.Filename!);
        if (!File.Exists(path)) throw new PdmException($"File locale non trovato: {path}");
        var sha = LocalState.Sha256Of(path);
        if (d.Versions.Count > 0 && d.Versions[0].Sha256 == sha)
        {
            await Post($"/items/{Esc(code)}/undo-checkout", null);
            LocalState.SetReadOnly(path, true);
            _out.WriteLine($"{code}: nessuna modifica, check-out annullato");
            return null;
        }
        if (refs is null && d.Item.Type is "ASM" or "DRW")
            refs = (await Tree(code)).Where(r => r.Level == 1).Select(r => new FileRef(r.Filename)).ToList();

        await Upload(path, sha);
        var size = new FileInfo(path).Length;
        var v = (await Post<JsonElement>($"/items/{Esc(code)}/checkin",
            new { sha256 = sha, size, comment, refs })).GetProperty("version").GetInt32();
        LocalState.SetReadOnly(path, true);
        var state = LocalState.Load(_cfg.LocalRoot);
        state.Files[d.Item.Filename!] = new(d.Item.Filename!, path, v, sha);
        state.Save(_cfg.LocalRoot);
        _out.WriteLine($"{code}: check-in v{v}" + (refs is { Count: > 0 } ? $" con {refs.Count} riferimenti" : ""));
        return v;
    }

    /// <summary>Rilascia il lock e riporta il file locale alla versione del vault.</summary>
    public async Task UndoCheckOut(string code)
    {
        await Post($"/items/{Esc(code)}/undo-checkout", null);
        await GetLatest(code, force: true, onlyRoot: true);
        _out.WriteLine($"{code}: check-out annullato, file locale riportato alla versione del vault");
    }

    /// <summary>Aggiunge al vault un file che sta gia' nella cartella di lavoro.</summary>
    public async Task Add(string localFile, string code, string description, string? material, decimal? massKg,
                          string? refCode, IReadOnlyList<FileRef>? refs, string? comment)
    {
        var full = Path.GetFullPath(localFile);
        if (!File.Exists(full)) throw new PdmException($"File non trovato: {full}");
        var type = Path.GetExtension(full).ToUpperInvariant() switch
        {
            ".SLDPRT" => "PRT", ".SLDASM" => "ASM", ".SLDDRW" => "DRW",
            _ => throw new PdmException("Solo file .sldprt, .sldasm, .slddrw"),
        };
        var folder = _cfg.VaultFolder(full);
        var sha = LocalState.Sha256Of(full);
        await Upload(full, sha);
        await Post("/items", new
        {
            code, description, type, filename = Path.GetFileName(full), folder_path = folder, sha256 = sha,
            size = new FileInfo(full).Length, material, mass_kg = massKg, ref_code = refCode, comment, refs,
        });
        LocalState.SetReadOnly(full, true);
        var state = LocalState.Load(_cfg.LocalRoot);
        state.Files[Path.GetFileName(full)] = new(Path.GetFileName(full), full, 1, sha);
        state.Save(_cfg.LocalRoot);
        _out.WriteLine($"{code}: aggiunto al vault in {folder} (v1)");
    }

    /// <summary>Stato della cartella di lavoro: file modificati, non nel vault, in check-out a me.</summary>
    public async Task Status()
    {
        var me = await Me();
        var state = LocalState.Load(_cfg.LocalRoot);
        var mine = (await Get<List<ItemRow>>("/items?limit=1000")).Where(i => i.LockedBy == me.Name).ToList();
        _out.WriteLine($"Utente: {me.Name} ({me.Role}) · cartella {_cfg.LocalRoot}");
        _out.WriteLine(mine.Count == 0 ? "Nessun file in check-out" : "In check-out a te:");
        foreach (var i in mine) _out.WriteLine($"  {i.Code,-14} {i.Filename}");
        if (!Directory.Exists(_cfg.LocalRoot)) return;
        var exts = new[] { ".sldprt", ".sldasm", ".slddrw" };
        foreach (var f in Directory.EnumerateFiles(_cfg.LocalRoot, "*.*", SearchOption.AllDirectories)
                     .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("~$"))
                     .Order())
        {
            var name = Path.GetFileName(f);
            if (!state.Files.TryGetValue(name, out var e)) _out.WriteLine($"  ? {name}: non nel vault (usa pdm add)");
            else if (LocalState.Sha256Of(f) != e.Sha256) _out.WriteLine($"  M {name}: modificato in locale (v{e.Version})");
        }
    }
}

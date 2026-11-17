using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pdmtrack.Api.Tests;

public class ApiTests(PdmApiFactory app) : IClassFixture<PdmApiFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Problem(HttpResponseMessage r, HttpStatusCode expected)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"atteso {expected}, ricevuto {(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString()!;
    }

    private static (byte[] bytes, string sha) Content(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static async Task Upload(HttpClient c, byte[] bytes, string sha) =>
        Assert.True((await c.PutAsync($"/api/blobs/{sha}", new ByteArrayContent(bytes))).IsSuccessStatusCode);

    [Fact]
    public async Task Senza_login_401()
    {
        var r = await app.CreateClient().GetAsync("/api/items");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Utente_di_dominio_riconosciuto()
    {
        var me = await Json(await app.As(@"DOMINIO\ACavani").GetAsync("/api/me"));
        Assert.Equal("acavani", me.GetProperty("login").GetString());
        Assert.Equal("designer", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Utente_non_registrato_403()
    {
        var detail = await Problem(await app.As("sconosciuto").GetAsync("/api/me"), HttpStatusCode.Forbidden);
        Assert.Contains("non registrato", detail);
    }

    [Fact]
    public async Task Ricerca_articoli()
    {
        var items = await Json(await app.As("acavani").GetAsync("/api/items?q=albero"));
        var codes = items.EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Equal(["120-0001", "120-0001-D", "120-0002", "130-0001"], codes); // anche "Gruppo tenuta albero"
        var locked = items.EnumerateArray().First().GetProperty("locked_by").GetString();
        Assert.Equal("A. Cavani", locked);
    }

    [Fact]
    public async Task Scheda_con_revisioni()
    {
        var d = await Json(await app.As("acavani").GetAsync("/api/items/111-0001"));
        var revs = d.GetProperty("revisions").EnumerateArray()
            .Select(r => r.GetProperty("revision").GetString() + "=v" + r.GetProperty("version").GetInt32()).ToList();
        Assert.Equal(["A=v8", "B=v11"], revs);
        Assert.Equal(11, d.GetProperty("versions")[0].GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.As("acavani").GetAsync("/api/items/999-9999")).StatusCode);
    }

    [Fact]
    public async Task Distinta_where_used_e_albero()
    {
        var c = app.As("acavani");
        var bom = await Json(await c.GetAsync("/api/items/100-0001/bom"));
        Assert.Equal(8.258m, Math.Round(bom.GetProperty("mass_kg").GetDecimal(), 3));
        var wu = await Json(await c.GetAsync("/api/items/150-0002/where-used"));
        Assert.Equal(["110-0001", "190-0001"], wu.EnumerateArray().Select(x => x.GetProperty("parent_code").GetString()));
        var tree = await Json(await c.GetAsync("/api/items/111-0001/tree?revision=A"));
        Assert.Equal(8, tree[0].GetProperty("version").GetInt32());
        Assert.Equal(5, (await Json(await c.GetAsync("/api/items/110-0001/tree"))).GetArrayLength());
    }

    [Fact]
    public async Task Caricamento_contenuto_verifica_hash()
    {
        var c = app.As("acavani");
        var (bytes, sha) = Content("contenuto di prova");
        var wrong = new string('0', 64);
        Assert.Contains("Hash non corrispondente",
            await Problem(await c.PutAsync($"/api/blobs/{wrong}", new ByteArrayContent(bytes)), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Created, (await c.PutAsync($"/api/blobs/{sha}", new ByteArrayContent(bytes))).StatusCode);
        Assert.False((await Json(await c.PutAsync($"/api/blobs/{sha}", new ByteArrayContent(bytes)))).GetProperty("stored").GetBoolean());
        Assert.Equal(bytes, await c.GetByteArrayAsync($"/api/blobs/{sha}"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/blobs/{wrong}")).StatusCode);
    }

    [Fact]
    public async Task Ciclo_check_in_check_out()
    {
        // 111-0002 e' in check-out a M. Rossi nel seed
        var rossi = app.As("mrossi");
        var cavani = app.As("acavani");
        var (bytes, sha) = Content("coperchio v7");

        var noBlob = await rossi.PostAsJsonAsync("/api/items/111-0002/checkin", new { sha256 = sha, size = bytes.Length });
        Assert.Contains("Contenuto non caricato", await Problem(noBlob, HttpStatusCode.Conflict));

        await Upload(rossi, bytes, sha);
        var other = await cavani.PostAsJsonAsync("/api/items/111-0002/checkin", new { sha256 = sha, size = bytes.Length });
        Assert.Contains("non e' in check-out a questo utente", await Problem(other, HttpStatusCode.Conflict));

        var v = await Json(await rossi.PostAsJsonAsync("/api/items/111-0002/checkin",
            new { sha256 = sha, size = bytes.Length, comment = "Nuovo trattamento" }));
        Assert.Equal(7, v.GetProperty("version").GetInt32());

        var lockedByOther = await cavani.PostAsJsonAsync("/api/checkout", new { codes = new[] { "120-0001", "120-0001-D" } });
        Assert.Equal(1, (await Json(lockedByOther)).GetProperty("locked").GetInt32()); // 120-0001 era gia' suo

        var conflict = await rossi.PostAsJsonAsync("/api/checkout", new { codes = new[] { "111-0002", "120-0001-D" } });
        Assert.Contains("altro utente", await Problem(conflict, HttpStatusCode.Conflict));
        var item = await Json(await rossi.GetAsync("/api/items/111-0002"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("item").GetProperty("locked_by").ValueKind); // tutto o niente

        Assert.Equal(HttpStatusCode.NoContent, (await cavani.PostAsync("/api/items/120-0001-D/undo-checkout", null)).StatusCode);
        Assert.Contains("Nessun articolo",
            await Problem(await cavani.PostAsJsonAsync("/api/checkout", new { codes = Array.Empty<string>() }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound,
            (await cavani.PostAsJsonAsync("/api/checkout", new { codes = new[] { "999-9999" } })).StatusCode);
    }

    [Fact]
    public async Task Ruoli_su_rilascio_e_sblocco()
    {
        var r = await app.As("acavani").PostAsJsonAsync("/api/items/100-0001/transition", new { to = "rel" });
        Assert.Contains("riservata", await Problem(r, HttpStatusCode.Forbidden));
        var blocked = await app.As("lbianchi").PostAsJsonAsync("/api/items/100-0001/transition", new { to = "rel" });
        Assert.Contains("Rilascio bloccato", await Problem(blocked, HttpStatusCode.Conflict));
        var unlock = await app.As("mrossi").PostAsJsonAsync("/api/items/120-0001/force-unlock", new { reason = "prova" });
        Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
    }

    [Fact]
    public async Task Nuovo_assieme_con_riferimenti()
    {
        var c = app.As("mrossi");
        var (bytes, sha) = Content("gruppo albero");
        await Upload(c, bytes, sha);
        var body = new
        {
            code = "140-0001", description = "Gruppo albero", type = "asm", filename = "140-0001.SLDASM",
            folder_path = @"\\PDM\Pompe\PG-25\", sha256 = sha, size = bytes.Length,
            refs = new object[] { new { filename = "120-0002.sldprt" }, new { filename = "150-0002.SLDPRT", version = 1 } },
        };
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/items", body)).StatusCode);
        var tree = await Json(await c.GetAsync("/api/items/140-0001/tree"));
        Assert.Equal(3, tree.GetArrayLength());
        var dup = await c.PostAsJsonAsync("/api/items", body with { });
        Assert.Contains("Codice articolo", await Problem(dup, HttpStatusCode.Conflict));
        var dupName = await c.PostAsJsonAsync("/api/items", new
        {
            code = "140-0002", description = "Doppione", type = "ASM", filename = "140-0001.sldasm",
            folder_path = @"\\PDM\", sha256 = sha, size = bytes.Length,
        });
        Assert.Contains("Esiste gia'", await Problem(dupName, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Modifica_descrizione()
    {
        var c = app.As("acavani");
        var r = await c.PatchAsJsonAsync("/api/items/150-0003", new { description = "Rondella elastica M8" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var d = await Json(await c.GetAsync("/api/items/150-0003"));
        Assert.Equal("Rondella elastica M8", d.GetProperty("item").GetProperty("description").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PatchAsJsonAsync("/api/items/999-9999", new { description = "x" })).StatusCode);
    }
}

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Pdmtrack.Api;

// ---- Modelli ---------------------------------------------------------------------------------

public record UserDto(Guid Id, string Login, string Name, string Role);
public record ItemRow(Guid Id, string Code, string Description, string Type, string Revision, string State,
                      string? Material, decimal MassKg, string? Filename, string? FolderPath, int? CurrentVersion,
                      long? SizeBytes, string? LockedBy, DateTime? LockedAt, string? RefCode);
public record RevisionRow(string Revision, int? Version, DateTime CreatedAt, string? ReleasedBy, DateTime? ReleasedAt,
                          DateTime? ObsoletedAt);
public record EventRow(string Text, string? User, DateTime CreatedAt);
public record VersionRow(int Version, string Revision, string Sha256, long SizeBytes, string? Comment, string? CreatedBy,
                         DateTime CreatedAt);
public record ItemDetail(ItemRow Item, IEnumerable<RevisionRow> Revisions, IEnumerable<VersionRow> Versions,
                         IEnumerable<EventRow> Events);
public record BomRow(int Level, string Path, string Code, string Description, decimal Qty, decimal TotalQty,
                     string State, decimal UnitMassKg);
public record Bom(string Code, decimal MassKg, IEnumerable<BomRow> Lines);
public record WhereUsedRow(string ParentCode, string ParentDescription, string ParentState, decimal Qty);
public record TreeRow(int Level, string Filename, string FolderPath, int Version, string Sha256, long SizeBytes);

public record FileRef(string Filename, int? Version);
public record ItemPatch(string? Description, string? Material, decimal? MassKg);
public record NewFile(string Code, string Description, string Type, string Filename, string FolderPath, string Sha256,
                      long Size, string? Material, decimal? MassKg, string? RefCode, string? Comment,
                      List<FileRef>? Refs);
public record CheckoutRequest(List<string> Codes);
public record CheckinRequest(string Sha256, long Size, string? Comment, List<FileRef>? Refs);
public record ForceUnlockRequest(string Reason);
public record TransitionRequest(string To);

// ---- Endpoint --------------------------------------------------------------------------------

public static class Endpoints
{
    private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    internal static string Login(this HttpContext ctx) => PdmAuth.Login(ctx.User);

    public static Task<UserDto> Me(HttpContext ctx, PdmDb db) =>
        db.Run(ctx.Login(), (c, tx) => c.QuerySingleAsync<UserDto>(
            "select id, login, name, role::text as role from current_app_user()", transaction: tx));

    private const string ItemSelect = """
        select i.id, i.code, i.description, i.type::text as type, i.revision, i.state::text as state, i.material,
               i.mass_kg, f.filename, f.folder_path, f.current_version, f.size_bytes, u.name as locked_by,
               f.locked_at, r.code as ref_code
        from items i
        left join files f on f.item_id = i.id
        left join app_users u on u.id = f.locked_by
        left join items r on r.id = i.ref_item_id
        """;

    private static async Task<Guid> IdOf(NpgsqlConnection c, NpgsqlTransaction tx, string code) =>
        await c.QuerySingleOrDefaultAsync<Guid?>("select id from items where code = @code", new { code }, tx)
        ?? throw ApiException.NotFound("Articolo " + code);

    private static async Task RequireBlob(IBlobStore blobs, string sha256, CancellationToken ct)
    {
        if (!Sha.IsMatch(sha256)) throw ApiException.Bad("sha256 non valido");
        if (!await blobs.ExistsAsync(sha256, ct))
            throw new ApiException(StatusCodes.Status409Conflict, "Contenuto non caricato: esegui prima PUT /api/blobs/" + sha256);
    }

    private static string? RefsJson(List<FileRef>? refs) => refs is null ? null
        : JsonSerializer.Serialize(refs.Select(r => new { filename = r.Filename, version = r.Version }));

    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items", (HttpContext ctx, PdmDb db, string? q, string? type, string? state, int? limit) =>
            db.Run(ctx.Login(), (c, tx) => c.QueryAsync<ItemRow>(ItemSelect + "\n" + """
                 where (@q::text is null or i.code ilike '%' || @q || '%' or i.description ilike '%' || @q || '%')
                   and (@type::text is null or i.type::text = upper(@type))
                   and (@state::text is null or i.state::text = lower(@state))
                 order by i.code limit @limit
                 """, new { q, type, state, limit = Math.Clamp(limit ?? 200, 1, 1000) }, tx)));

        api.MapGet("/items/{code}", (HttpContext ctx, PdmDb db, string code) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                var item = await c.QuerySingleOrDefaultAsync<ItemRow>(ItemSelect + " where i.code = @code", new { code }, tx)
                           ?? throw ApiException.NotFound("Articolo " + code);
                var revisions = await c.QueryAsync<RevisionRow>("""
                    select r.revision, v.version, r.created_at, u.name as released_by, r.released_at, r.obsoleted_at
                    from item_revisions r left join file_versions v on v.id = r.file_version_id
                    left join app_users u on u.id = r.released_by
                    where r.item_id = @id order by length(r.revision), r.revision
                    """, new { id = item.Id }, tx);
                var versions = await c.QueryAsync<VersionRow>("""
                    select v.version, v.revision, v.sha256, v.size_bytes, v.comment, u.name as created_by, v.created_at
                    from file_versions v join files f on f.id = v.file_id left join app_users u on u.id = v.created_by
                    where f.item_id = @id order by v.version desc
                    """, new { id = item.Id }, tx);
                var events = await c.QueryAsync<EventRow>("""
                    select e.text, u.name as user, e.created_at from item_events e
                    left join app_users u on u.id = e.user_id
                    where e.item_id = @id order by e.created_at desc, e.id desc limit 100
                    """, new { id = item.Id }, tx);
                return new ItemDetail(item, revisions, versions, events);
            }));

        api.MapGet("/items/{code}/bom", (HttpContext ctx, PdmDb db, string code) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                var id = await IdOf(c, tx, code);
                var lines = await c.QueryAsync<BomRow>("""
                    select level, path, code, description, qty, total_qty, state::text as state, unit_mass_kg
                    from bom_explode(@id)
                    """, new { id }, tx);
                var mass = await c.QuerySingleAsync<decimal>("select item_mass(@id)", new { id }, tx);
                return new Bom(code, mass, lines);
            }));

        api.MapGet("/items/{code}/where-used", (HttpContext ctx, PdmDb db, string code) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                var id = await IdOf(c, tx, code);
                return await c.QueryAsync<WhereUsedRow>("""
                    select parent_code, parent_description, parent_state::text as parent_state, qty
                    from where_used where item_id = @id order by parent_code
                    """, new { id }, tx);
            }));

        // latest=true: versioni correnti di tutti i file (get latest); altrimenti le versioni esatte
        // registrate nella revisione (o nella versione corrente se la revisione non e' indicata)
        api.MapGet("/items/{code}/tree", (HttpContext ctx, PdmDb db, string code, string? revision, bool? latest) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                var id = await IdOf(c, tx, code);
                if (latest == true && revision is not null) throw ApiException.Bad("Usa revision oppure latest, non entrambi");
                var fn = latest == true ? "latest_tree(@id)" : "revision_tree(@id, @revision)";
                return await c.QueryAsync<TreeRow>(
                    $"select level, filename, folder_path, version, sha256, size_bytes from {fn} order by level, filename",
                    new { id, revision }, tx);
            }));

        api.MapMethods("/items/{code}", ["PATCH"], (HttpContext ctx, PdmDb db, string code, ItemPatch patch) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                var n = await c.ExecuteAsync("""
                    update items set description = coalesce(@Description, description),
                                     material = coalesce(@Material, material),
                                     mass_kg = coalesce(@MassKg, mass_kg)
                    where code = @code
                    """, new { patch.Description, patch.Material, patch.MassKg, code }, tx);
                if (n == 0) throw ApiException.NotFound("Articolo " + code);
                return Results.NoContent();
            }));

        return api;
    }

    public static RouteGroupBuilder MapVaultEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/items", async (HttpContext ctx, PdmDb db, IBlobStore blobs, NewFile f, CancellationToken ct) =>
        {
            await RequireBlob(blobs, f.Sha256, ct);
            await db.Run(ctx.Login(), (c, tx) => c.ExecuteScalarAsync<Guid>("""
                select add_file(@Code, @Description, upper(@Type)::item_type, @Filename, @FolderPath, @Sha256, @Size,
                                @Material, coalesce(@MassKg, 0), @RefCode, @Comment, @refs::jsonb)
                """, new { f.Code, f.Description, f.Type, f.Filename, f.FolderPath, f.Sha256, f.Size, f.Material,
                           f.MassKg, f.RefCode, f.Comment, refs = RefsJson(f.Refs) }, tx), ct);
            return Results.Created($"/api/items/{f.Code}", null);
        });

        api.MapPost("/checkout", (HttpContext ctx, PdmDb db, CheckoutRequest r) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                if (r.Codes is not { Count: > 0 }) throw ApiException.Bad("Nessun articolo indicato");
                var ids = (await c.QueryAsync<(string code, Guid id)>(
                    "select code, id from items where code = any(@codes)", new { codes = r.Codes.ToArray() }, tx)).ToList();
                var missing = r.Codes.Except(ids.Select(x => x.code)).ToList();
                if (missing.Count > 0) throw ApiException.NotFound("Articolo " + string.Join(", ", missing));
                var locked = await c.ExecuteScalarAsync<int>("select check_out_many(@ids)",
                    new { ids = ids.Select(x => x.id).ToArray() }, tx);
                return new { locked };
            }));

        api.MapPost("/items/{code}/checkin", async (HttpContext ctx, PdmDb db, IBlobStore blobs, string code,
                                                    CheckinRequest r, CancellationToken ct) =>
        {
            await RequireBlob(blobs, r.Sha256, ct);
            var version = await db.Run(ctx.Login(), async (c, tx) =>
                await c.ExecuteScalarAsync<int>("select check_in(@id, @Sha256, @Size, @Comment, @refs::jsonb)",
                    new { id = await IdOf(c, tx, code), r.Sha256, r.Size, r.Comment, refs = RefsJson(r.Refs) }, tx), ct);
            return new { version };
        });

        api.MapPost("/items/{code}/undo-checkout", (HttpContext ctx, PdmDb db, string code) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                await c.ExecuteAsync("select undo_check_out(@id)", new { id = await IdOf(c, tx, code) }, tx);
                return Results.NoContent();
            }));

        api.MapPost("/items/{code}/force-unlock", (HttpContext ctx, PdmDb db, string code, ForceUnlockRequest r) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                await c.ExecuteAsync("select force_unlock(@id, @Reason)", new { id = await IdOf(c, tx, code), r.Reason }, tx);
                return Results.NoContent();
            }));

        api.MapPost("/items/{code}/transition", (HttpContext ctx, PdmDb db, string code, TransitionRequest r) =>
            db.Run(ctx.Login(), async (c, tx) =>
            {
                await c.ExecuteAsync("select transition_item(@id, @To)", new { id = await IdOf(c, tx, code), r.To }, tx);
                return Results.NoContent();
            }));

        return api;
    }

    public static RouteGroupBuilder MapBlobEndpoints(this RouteGroupBuilder api)
    {
        // Caricamento del contenuto: il server ricalcola l'hash e rifiuta se non corrisponde
        api.MapPut("/blobs/{sha256}", async (HttpContext ctx, IBlobStore blobs, string sha256, CancellationToken ct) =>
        {
            if (!Sha.IsMatch(sha256)) throw ApiException.Bad("sha256 non valido");
            if (await blobs.ExistsAsync(sha256, ct)) return Results.Ok(new { stored = false });
            var tmp = Path.GetTempFileName();
            try
            {
                long length;
                string actual;
                await using (var f = File.Create(tmp))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        await f.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                    length = f.Length;
                    actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                if (actual != sha256) throw ApiException.Bad($"Hash non corrispondente: ricevuto {actual}");
                await using var read = File.OpenRead(tmp);
                await blobs.PutAsync(sha256, read, length, ct);
                return Results.Created($"/api/blobs/{sha256}", new { stored = true });
            }
            finally { File.Delete(tmp); }
        });

        api.MapMethods("/blobs/{sha256}", ["HEAD"], async (IBlobStore blobs, string sha256, CancellationToken ct) =>
            Sha.IsMatch(sha256) && await blobs.ExistsAsync(sha256, ct) ? Results.Ok() : Results.NotFound());

        api.MapGet("/blobs/{sha256}", async (IBlobStore blobs, string sha256, CancellationToken ct) =>
        {
            if (!Sha.IsMatch(sha256)) throw ApiException.Bad("sha256 non valido");
            var s = await blobs.OpenReadAsync(sha256, ct);
            return s is null ? Results.NotFound() : Results.Stream(s, "application/octet-stream");
        });

        return api;
    }
}

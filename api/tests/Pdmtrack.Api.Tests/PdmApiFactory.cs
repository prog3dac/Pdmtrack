using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Pdmtrack.Api.Tests;

/// <summary>
/// Crea un database di prova da db/migrations e db/seed.sql e avvia l'API collegata con l'utente
/// pdm_api (membro di pdm_app), come in produzione. Connessione amministrativa da PDM_TEST_ADMIN.
/// </summary>
public sealed class PdmApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string Admin = Environment.GetEnvironmentVariable("PDM_TEST_ADMIN")
        ?? "Host=localhost;Username=postgres;Password=postgres";
    private readonly string _db = "pdmtrack_api_test_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _vault = Path.Combine(Path.GetTempPath(), "pdm-vault-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await Exec(admin, $"create database {_db}");
        }
        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql").Order()
            .Append(Path.Combine(root, "db", "seed.sql"));
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Database = _db }.ConnectionString);
        await conn.OpenAsync();
        foreach (var f in files) await Exec(conn, await File.ReadAllTextAsync(f));
        await Exec(conn, """
            do $$ begin
              if not exists (select 1 from pg_roles where rolname = 'pdm_api') then
                create role pdm_api login password 'pdm_api' in role pdm_app;
              end if;
            end $$;
            insert into app_users (login, name, role) values ('admin', 'Amministratore', 'admin');
            """);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var api = new NpgsqlConnectionStringBuilder(Admin) { Database = _db, Username = "pdm_api", Password = "pdm_api" };
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pdm", api.ConnectionString);
        builder.UseSetting("Pdm:Auth:Mode", "Dev");
        builder.UseSetting("Pdm:Storage:Kind", "FileSystem");
        builder.UseSetting("Pdm:Storage:Path", _vault);
    }

    public HttpClient As(string user)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Pdm-User", user);
        return c;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        await Exec(admin, $"drop database if exists {_db} with (force)");
        if (Directory.Exists(_vault)) Directory.Delete(_vault, true);
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "db", "migrations"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Cartella db/migrations non trovata");
    }
}

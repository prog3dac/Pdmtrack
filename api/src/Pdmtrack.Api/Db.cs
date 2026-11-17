using System.Data;
using Npgsql;

namespace Pdmtrack.Api;

/// <summary>
/// Accesso al database. Ogni operazione gira in una transazione che si apre con
/// set_config('pdm.user_login', ...): e' cosi' che le funzioni SQL sanno chi e' l'utente.
/// L'API si collega con un utente membro del ruolo pdm_app (vedi db/README.md).
/// </summary>
public sealed class PdmDb(IConfiguration config) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(
        config.GetConnectionString("Pdm") ?? throw new InvalidOperationException("ConnectionStrings:Pdm mancante"));

    public async Task<T> Run<T>(string login, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
                                CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var cmd = new NpgsqlCommand("select set_config('pdm.user_login', $1, true)", conn, tx))
        {
            cmd.Parameters.AddWithValue(login);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

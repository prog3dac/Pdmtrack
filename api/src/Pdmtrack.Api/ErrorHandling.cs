using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Pdmtrack.Api;

/// <summary>Traduce gli errori delle funzioni SQL in risposte HTTP con il messaggio in italiano.</summary>
public static class ErrorHandling
{
    public static void Handle(IApplicationBuilder app) => app.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, detail) = ex switch
        {
            PostgresException { SqlState: "42501" } pg => (StatusCodes.Status403Forbidden, pg.MessageText),
            PostgresException { SqlState: "P0001" } pg => (StatusCodes.Status409Conflict, pg.MessageText),
            PostgresException { SqlState: "23505" } pg => (StatusCodes.Status409Conflict, Duplicate(pg)),
            PostgresException { SqlState: "23514" or "22P02" or "23503" or "23502" } pg => (StatusCodes.Status400BadRequest, pg.MessageText),
            BadHttpRequestException b => (b.StatusCode, b.Message),
            ApiException a => (a.Status, a.Message),
            _ => (StatusCodes.Status500InternalServerError, "Errore interno"),
        };
        if (status >= 500)
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pdmtrack.Errori")
               .LogError(ex, "Errore non gestito su {Path}", ctx.Request.Path);
        await Results.Problem(detail: detail, statusCode: status).ExecuteAsync(ctx);
    });

    private static string Duplicate(PostgresException pg) => pg.ConstraintName switch
    {
        "files_filename_unique" => "Esiste gia' un file con questo nome nel vault",
        "items_code_key" => "Codice articolo gia' esistente",
        _ => pg.MessageText,
    };
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static ApiException NotFound(string what) => new(StatusCodes.Status404NotFound, what + " non trovato");
    public static ApiException Bad(string message) => new(StatusCodes.Status400BadRequest, message);
}

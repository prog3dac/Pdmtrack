using Pdmtrack.Api;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true; // mass_kg -> MassKg

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPdmAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy); // tutto richiede login
builder.Services.AddSingleton<PdmDb>();
builder.Services.AddBlobStore(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.UseExceptionHandler(ErrorHandling.Handle);
// Interfaccia web (web/, esportata in wwwroot): servita dalla stessa origine dell'API
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapGet("/me", Endpoints.Me);
api.MapItemEndpoints();
api.MapVaultEndpoints();
api.MapBlobEndpoints();

app.Run();

public partial class Program; // per WebApplicationFactory nei test

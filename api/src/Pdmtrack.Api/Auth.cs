using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;

namespace Pdmtrack.Api;

public static class PdmAuth
{
    public const string DevScheme = "PdmDev";
    public const string DevHeader = "X-Pdm-User";

    /// <summary>
    /// Pdm:Auth:Mode = "Windows" (predefinito: Kerberos/NTLM con l'account di dominio)
    /// oppure "Dev" (utente nell'intestazione X-Pdm-User, solo in Development e nei test).
    /// </summary>
    public static IServiceCollection AddPdmAuthentication(this IServiceCollection services,
        IConfiguration config, IHostEnvironment env)
    {
        var mode = config["Pdm:Auth:Mode"] ?? "Windows";
        if (mode.Equals("Dev", StringComparison.OrdinalIgnoreCase))
        {
            if (!(env.IsDevelopment() || env.IsEnvironment("Testing")))
                throw new InvalidOperationException("Pdm:Auth:Mode=Dev consentito solo in Development o Testing");
            services.AddAuthentication(DevScheme)
                    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, null);
        }
        else
        {
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        }
        return services;
    }

    /// <summary>"DOMINIO\acavani" o "acavani@dominio.local" -> "acavani" (confrontato con app_users.login)</summary>
    public static string Login(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name ?? throw new UnauthorizedAccessException();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        return name.Trim().ToLowerInvariant();
    }
}

public sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
                                   UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[PdmAuth.DevHeader].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], PdmAuth.DevScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), PdmAuth.DevScheme)));
    }
}

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fleans.Plugins.CamundaExternalTask;

public static class CamundaBasicAuthentication
{
    /// <summary>
    /// Requires HTTP basic authentication with one fixed user, as Camunda's
    /// <c>ProcessEngineAuthenticationFilter</c> does: the Java client's <c>BasicAuthProvider</c> and
    /// the JS client's <c>BasicAuthInterceptor</c> work unchanged. A missing or wrong credential gets
    /// <c>401</c> with <c>WWW-Authenticate: Basic</c>. For anything richer, use ASP.NET authentication
    /// and <c>.RequireAuthorization()</c> on the route group instead.
    /// </summary>
    public static RouteGroupBuilder RequireBasicAuthentication(
        this RouteGroupBuilder group, string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var expected = Hash($"{username}:{password}");

        return group.AddEndpointFilter(async (context, next) =>
        {
            if (TryGetCredentials(context.HttpContext.Request, out var credentials)
                && CryptographicOperations.FixedTimeEquals(Hash(credentials), expected))
                return await next(context);

            context.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"default\"";
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        });
    }

    private static bool TryGetCredentials(HttpRequest request, out string credentials)
    {
        credentials = "";
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is null)
            return false;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Hashing first makes the comparison constant-time regardless of the credential's length.
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

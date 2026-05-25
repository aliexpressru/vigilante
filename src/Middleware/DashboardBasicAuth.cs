using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;

namespace Vigilante.Middleware;

internal static class DashboardBasicAuth
{
    public const string Realm = "Vigilante";
    public const string SessionCookieName = "Vigilante.Session";
    private const string SessionProtectorPurpose = "Vigilante.Dashboard.Session";

    public static bool IsEnabled(string? dashboardPassword) =>
        !string.IsNullOrWhiteSpace(dashboardPassword);

    public static bool IsPublicPath(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/login.html", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSwaggerPath(PathString path) =>
        path.HasValue && path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);

    public static bool IsSwaggerOpenApiDocument(PathString path) =>
        IsSwaggerPath(path)
        && path.Value!.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldRedirectSwaggerToLogin(HttpRequest request) =>
        IsSwaggerPath(request.Path)
        && !IsSwaggerOpenApiDocument(request.Path)
        && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));

    public static string BuildLoginRedirectUrl(HttpRequest request)
    {
        var returnUrl = $"{request.PathBase}{request.Path}{request.QueryString}";
        if (string.IsNullOrEmpty(returnUrl))
        {
            return "/login.html";
        }

        return $"/login.html?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    public static bool IsBrowserNavigation(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        if (request.Headers.Accept.Count == 0)
        {
            return true;
        }

        foreach (var mediaType in request.Headers.Accept)
        {
            if (string.IsNullOrEmpty(mediaType))
            {
                continue;
            }

            if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("*/*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryValidatePassword(string? providedPassword, string expectedPassword) =>
        providedPassword is not null && FixedTimeEquals(providedPassword, expectedPassword);

    public static bool TryValidateAuthorizationHeader(string? authorizationHeader, string expectedPassword)
    {
        if (string.IsNullOrEmpty(authorizationHeader)
            || !AuthenticationHeaderValue.TryParse(authorizationHeader, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        var providedPassword = separatorIndex >= 0
            ? decoded[(separatorIndex + 1)..]
            : decoded;

        return TryValidatePassword(providedPassword, expectedPassword);
    }

    public static bool TryValidateSessionCookie(HttpRequest request, IDataProtectionProvider dataProtectionProvider)
    {
        if (!request.Cookies.TryGetValue(SessionCookieName, out var cookie) || string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        try
        {
            var payload = dataProtectionProvider
                .CreateProtector(SessionProtectorPurpose)
                .Unprotect(cookie);

            return string.Equals(payload, "authenticated", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string ProtectSession(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider
            .CreateProtector(SessionProtectorPurpose)
            .Protect("authenticated");

    public static CookieOptions CreateSessionCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = false,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true
    };

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

internal sealed class DashboardBasicAuthMiddleware(
    RequestDelegate next,
    IOptions<VigilanteOptions> options,
    IDataProtectionProvider dataProtectionProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var password = options.Value.DashboardPassword;
        if (!DashboardBasicAuth.IsEnabled(password) || DashboardBasicAuth.IsPublicPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (DashboardBasicAuth.TryValidateSessionCookie(context.Request, dataProtectionProvider)
            || DashboardBasicAuth.TryValidateAuthorizationHeader(context.Request.Headers.Authorization, password!))
        {
            await next(context);
            return;
        }

        if (DashboardBasicAuth.ShouldRedirectSwaggerToLogin(context.Request))
        {
            context.Response.Redirect(DashboardBasicAuth.BuildLoginRedirectUrl(context.Request));
            return;
        }

        if (DashboardBasicAuth.IsBrowserNavigation(context.Request))
        {
            context.Response.Redirect(DashboardBasicAuth.BuildLoginRedirectUrl(context.Request));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message = "Unauthorized" });
    }
}

internal static class DashboardBasicAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseDashboardBasicAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<DashboardBasicAuthMiddleware>();
}

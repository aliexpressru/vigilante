using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Middleware;
using Vigilante.Models.Requests;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(
    IOptions<VigilanteOptions> options,
    IDataProtectionProvider dataProtectionProvider) : ControllerBase
{
    [HttpPost("login")]
    public IActionResult Login([FromBody] V1DashboardLoginRequest request)
    {
        var expectedPassword = options.Value.DashboardPassword;
        if (!DashboardBasicAuth.IsEnabled(expectedPassword))
        {
            return NotFound();
        }

        if (!DashboardBasicAuth.TryValidatePassword(request.Password, expectedPassword!))
        {
            return Unauthorized();
        }

        var token = DashboardBasicAuth.ProtectSession(dataProtectionProvider);
        Response.Cookies.Append(DashboardBasicAuth.SessionCookieName, token, DashboardBasicAuth.CreateSessionCookieOptions());

        return Ok(new { success = true });
    }

    [HttpPost("logout")]
    public IActionResult LogoutPost()
    {
        ClearSessionCookie();
        return Ok(new { success = true });
    }

    /// <summary>Browser-friendly logout (address bar or link).</summary>
    [HttpGet("logout")]
    public IActionResult LogoutGet()
    {
        ClearSessionCookie();
        return Redirect("/login.html");
    }

    private void ClearSessionCookie() =>
        Response.Cookies.Delete(DashboardBasicAuth.SessionCookieName, DashboardBasicAuth.CreateSessionCookieOptions());
}

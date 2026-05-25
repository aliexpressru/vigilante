using System.Text;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Vigilante.Middleware;

namespace Aer.Vigilante.Tests.Middleware;

[TestFixture]
public class DashboardBasicAuthTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsEnabled_WhenPasswordMissing_ReturnsFalse(string? password)
    {
        Assert.That(DashboardBasicAuth.IsEnabled(password), Is.False);
    }

    [Test]
    public void IsEnabled_WhenPasswordSet_ReturnsTrue()
    {
        Assert.That(DashboardBasicAuth.IsEnabled("secret"), Is.True);
    }

    [TestCase("/health")]
    [TestCase("/health/ready")]
    [TestCase("/metrics")]
    [TestCase("/login.html")]
    [TestCase("/api/v1/auth/login")]
    [TestCase("/api/v1/auth/logout")]
    public void IsPublicPath_ForPublicPaths_ReturnsTrue(string path)
    {
        Assert.That(DashboardBasicAuth.IsPublicPath(path), Is.True);
    }

    [TestCase("/")]
    [TestCase("/api/v1/cluster/status")]
    [TestCase("/swagger")]
    public void IsPublicPath_ForProtectedPaths_ReturnsFalse(string path)
    {
        Assert.That(DashboardBasicAuth.IsPublicPath(path), Is.False);
    }

    [Test]
    public void TryValidatePassword_WithValidPassword_ReturnsTrue()
    {
        Assert.That(DashboardBasicAuth.TryValidatePassword("secret", "secret"), Is.True);
    }

    [Test]
    public void TryValidatePassword_WithInvalidPassword_ReturnsFalse()
    {
        Assert.That(DashboardBasicAuth.TryValidatePassword("wrong", "secret"), Is.False);
    }

    [Test]
    public void TryValidateAuthorizationHeader_WithValidPassword_ReturnsTrue()
    {
        var header = BasicHeader("any-user", "secret");

        Assert.That(DashboardBasicAuth.TryValidateAuthorizationHeader(header, "secret"), Is.True);
    }

    [Test]
    public void TryValidateAuthorizationHeader_WithInvalidPassword_ReturnsFalse()
    {
        var header = BasicHeader("any-user", "wrong");

        Assert.That(DashboardBasicAuth.TryValidateAuthorizationHeader(header, "secret"), Is.False);
    }

    [Test]
    public void TryValidateAuthorizationHeader_WhenHeaderMissing_ReturnsFalse()
    {
        Assert.That(DashboardBasicAuth.TryValidateAuthorizationHeader(null, "secret"), Is.False);
    }

    [Test]
    public void IsBrowserNavigation_ForBrowserGet_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";

        Assert.That(DashboardBasicAuth.IsBrowserNavigation(context.Request), Is.True);
    }

    [TestCase("/swagger")]
    [TestCase("/swagger/v1/swagger.json")]
    public void IsSwaggerPath_ForSwaggerRoutes_ReturnsTrue(string path)
    {
        Assert.That(DashboardBasicAuth.IsSwaggerPath(path), Is.True);
    }

    [Test]
    public void IsSwaggerOpenApiDocument_ForSwaggerJson_ReturnsTrue()
    {
        Assert.That(DashboardBasicAuth.IsSwaggerOpenApiDocument("/swagger/v1/swagger.json"), Is.True);
    }

    [Test]
    public void ShouldRedirectSwaggerToLogin_ForSwaggerJson_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/swagger/v1/swagger.json";

        Assert.That(DashboardBasicAuth.ShouldRedirectSwaggerToLogin(context.Request), Is.False);
    }

    [Test]
    public void IsBrowserNavigation_ForJsonApiPost_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Accept = "application/json";

        Assert.That(DashboardBasicAuth.IsBrowserNavigation(context.Request), Is.False);
    }

    private static string BasicHeader(string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return $"Basic {token}";
    }
}

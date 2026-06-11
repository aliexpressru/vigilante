using System.Text;
using FluentAssertions;
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
        DashboardBasicAuth.IsEnabled(password).Should().BeFalse();
    }

    [Test]
    public void IsEnabled_WhenPasswordSet_ReturnsTrue()
    {
        DashboardBasicAuth.IsEnabled("secret").Should().BeTrue();
    }

    [TestCase("/health")]
    [TestCase("/health/ready")]
    [TestCase("/metrics")]
    [TestCase("/login.html")]
    [TestCase("/api/v1/auth/login")]
    [TestCase("/api/v1/auth/logout")]
    public void IsPublicPath_ForPublicPaths_ReturnsTrue(string path)
    {
        DashboardBasicAuth.IsPublicPath(path).Should().BeTrue();
    }

    [TestCase("/")]
    [TestCase("/api/v1/cluster/status")]
    [TestCase("/swagger")]
    public void IsPublicPath_ForProtectedPaths_ReturnsFalse(string path)
    {
        DashboardBasicAuth.IsPublicPath(path).Should().BeFalse();
    }

    [Test]
    public void TryValidatePassword_WithValidPassword_ReturnsTrue()
    {
        DashboardBasicAuth.TryValidatePassword("secret", "secret").Should().BeTrue();
    }

    [Test]
    public void TryValidatePassword_WithInvalidPassword_ReturnsFalse()
    {
        DashboardBasicAuth.TryValidatePassword("wrong", "secret").Should().BeFalse();
    }

    [Test]
    public void TryValidateAuthorizationHeader_WithValidPassword_ReturnsTrue()
    {
        var header = BasicHeader("any-user", "secret");

        DashboardBasicAuth.TryValidateAuthorizationHeader(header, "secret").Should().BeTrue();
    }

    [Test]
    public void TryValidateAuthorizationHeader_WithInvalidPassword_ReturnsFalse()
    {
        var header = BasicHeader("any-user", "wrong");

        DashboardBasicAuth.TryValidateAuthorizationHeader(header, "secret").Should().BeFalse();
    }

    [Test]
    public void TryValidateAuthorizationHeader_WhenHeaderMissing_ReturnsFalse()
    {
        DashboardBasicAuth.TryValidateAuthorizationHeader(null, "secret").Should().BeFalse();
    }

    [Test]
    public void IsBrowserNavigation_ForBrowserGet_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";

        DashboardBasicAuth.IsBrowserNavigation(context.Request).Should().BeTrue();
    }

    [TestCase("/swagger")]
    [TestCase("/swagger/v1/swagger.json")]
    public void IsSwaggerPath_ForSwaggerRoutes_ReturnsTrue(string path)
    {
        DashboardBasicAuth.IsSwaggerPath(path).Should().BeTrue();
    }

    [Test]
    public void IsSwaggerOpenApiDocument_ForSwaggerJson_ReturnsTrue()
    {
        DashboardBasicAuth.IsSwaggerOpenApiDocument("/swagger/v1/swagger.json").Should().BeTrue();
    }

    [Test]
    public void ShouldRedirectSwaggerToLogin_ForSwaggerJson_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/swagger/v1/swagger.json";

        DashboardBasicAuth.ShouldRedirectSwaggerToLogin(context.Request).Should().BeFalse();
    }

    [Test]
    public void IsBrowserNavigation_ForJsonApiPost_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Accept = "application/json";

        DashboardBasicAuth.IsBrowserNavigation(context.Request).Should().BeFalse();
    }

    private static string BasicHeader(string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return $"Basic {token}";
    }
}

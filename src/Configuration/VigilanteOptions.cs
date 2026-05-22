namespace Vigilante.Configuration;

public class VigilanteOptions
{
    /// <summary>
    /// When set, HTTP Basic authentication is required for the dashboard, API, and Swagger.
    /// Any username is accepted; this value is validated as the password.
    /// </summary>
    public string? DashboardPassword { get; set; }
}

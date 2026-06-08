namespace Vigilante.Extensions;

internal static class EnvironmentExtensions
{
    extension(Environment)
    {
        public static string GetVigilanteVersion()
        {
            return Environment.GetEnvironmentVariable("VIGILANTE_VERSION") ?? "dev";
        }

        public static string? GetHostname()
        {
            return Environment.GetEnvironmentVariable("HOSTNAME");
        }
    }
}

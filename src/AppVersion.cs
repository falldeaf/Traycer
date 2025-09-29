using System;
using System.Reflection;

namespace Traycer
{
    internal static class AppVersion
    {
        private static readonly Lazy<string> _informationalVersion = new(GetInformationalVersion);
        private static readonly Lazy<Version> _semanticVersion = new(() => ParseVersion(_informationalVersion.Value));

        public static string InformationalVersion => _informationalVersion.Value;
        public static Version SemanticVersion => _semanticVersion.Value;
        public static string NormalizedVersion => SemanticVersion.ToString();

        private static string GetInformationalVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational!;
            }

            var fallback = assembly.GetName().Version;
            return fallback?.ToString() ?? "0.0.0";
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Version(0, 0, 0);
            }

            var trimmed = value;
            var plus = trimmed.IndexOf('+');
            if (plus >= 0)
            {
                trimmed = trimmed[..plus];
            }

            var dash = trimmed.IndexOf('-');
            if (dash >= 0)
            {
                trimmed = trimmed[..dash];
            }

            return Version.TryParse(trimmed, out var parsed) ? parsed : new Version(0, 0, 0);
        }
    }
}

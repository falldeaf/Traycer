using System;

namespace Traycer
{
    internal static class AppConstants
    {
        public const string AppName = "Traycer";
        public const string AppDisplayName = "Traycer HUD";
        public const string PublisherName = "Thomas Mardis";
        public const string RepositoryOwner = "thomas-mardis";
        public const string RepositoryName = "Traycer";
        public const string PackageIdentifier = "ThomasMardis.Traycer";
        public static readonly Uri ReleasesPage = new($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/");
        public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
        public const string InstallerAssetPrefix = "TraycerSetup_";
        public const string InstallerAssetSuffix = ".exe";
    }
}


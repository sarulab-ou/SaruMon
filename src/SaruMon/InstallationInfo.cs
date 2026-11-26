using System.Security;
using Microsoft.Win32;
using SaruMon.Updates;

namespace SaruMon;

/// <summary>Keeps the installed-app version current after a ZIP self-update.</summary>
internal static class InstallationInfo
{
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruMon";
    internal const string MarkerFileName = "SaruMon.install.ini";
    internal const string ProductMarker = "SaruMon-Local-Install-v1";

    internal static void RefreshVersion()
    {
        try
        {
            // NSIS writes this per-user key in the 64-bit registry view. Never create
            // a missing registration: portable copies must remain portable.
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = currentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is null) return;
            RefreshVersion(Environment.ProcessPath, UpdateService.CurrentVersion,
                key.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                key.GetValue("DisplayVersion", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                File.ReadAllText,
                version => key.SetValue("DisplayVersion", version, RegistryValueKind.String));
        }
        catch (Exception exception) when (IsMetadataFailure(exception))
        {
            // An unavailable installation record must never prevent monitoring.
        }
    }

    // Dependencies are explicit so installation decisions and failure paths can be
    // tested without reading or writing the user's actual uninstall registration.
    internal static bool RefreshVersion(string? executablePath, string? appVersion,
        string? registeredLocation, string? displayedVersion,
        Func<string, string> readMarker, Action<string> writeDisplayVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(registeredLocation)
                || !Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(registeredLocation)
                || !Path.GetFileName(executablePath).Equals("SaruMon.exe", StringComparison.OrdinalIgnoreCase)
                || !ReleaseVersion.TryParse(appVersion, out var version)) return false;

            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (executableDirectory is null || !Path.TrimEndingDirectorySeparator(executableDirectory).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredLocation)), StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(displayedVersion, version!.Text, StringComparison.Ordinal)) return false;
            var markerPath = Path.Combine(executableDirectory, MarkerFileName);
            if (!HasProductMarker(readMarker(markerPath))) return false;
            writeDisplayVersion(version.Text);
            return true;
        }
        catch (Exception exception) when (IsMetadataFailure(exception))
        {
            return false;
        }
    }

    internal static bool HasProductMarker(string contents)
    {
        var inInstallSection = false;
        string? product = null;
        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim().TrimStart('\uFEFF');
            if (text.Length == 0 || text.StartsWith(';') || text.StartsWith('#')) continue;
            if (text.StartsWith('['))
            {
                inInstallSection = text.Equals("[Install]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inInstallSection) continue;
            var separator = text.IndexOf('=');
            if (separator < 0 || !text[..separator].Trim().Equals("Product", StringComparison.OrdinalIgnoreCase)) continue;
            // A duplicate identity is ambiguous, even if one occurrence matches.
            if (product is not null) return false;
            product = text[(separator + 1)..].Trim();
        }
        return string.Equals(product, ProductMarker, StringComparison.Ordinal);
    }

    private static bool IsMetadataFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}

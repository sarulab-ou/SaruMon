using System.Security;
using SaruMon;

namespace SaruMon.Tests;

internal static class InstallationTests
{
    internal static int Run()
    {
        var count = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Installation test failed: " + message);
            count++;
        }

        const string marker = "[Install]\r\nProduct=SaruMon-Local-Install-v1\r\nVersion=0.1.0\r\nDesktopShortcut=1\r\n";
        var directory = Path.Combine(Path.GetTempPath(), "SaruMon-installation-metadata-tests");
        var executable = Path.Combine(directory, "SaruMon.exe");
        string? written = null;
        string? markerReadPath = null;
        string ReadMarker(string path) { markerReadPath = path; return marker; }
        void WriteVersion(string value) { written = value; }
        bool Refresh(string? path, string? version, string? location, string? displayed = "0.1.0",
            Func<string, string>? read = null, Action<string>? write = null)
        {
            written = null;
            markerReadPath = null;
            return InstallationInfo.RefreshVersion(path, version, location, displayed, read ?? ReadMarker, write ?? WriteVersion);
        }

        Assert(Refresh(executable, "0.1.1", directory) && written == "0.1.1", "refresh an installed app after a ZIP update");
        Assert(markerReadPath == Path.Combine(directory, InstallationInfo.MarkerFileName), "read marker beside the running executable");
        Assert(Refresh(executable.ToUpperInvariant(), "0.1.1", directory.ToLowerInvariant() + Path.DirectorySeparatorChar), "case and trailing separator normalization");
        Assert(Refresh(executable, "0.1.1", Path.Combine(directory, "nested", "..")), "normalize parent segments");
        Assert(!Refresh(Path.Combine(directory + "-portable", "SaruMon.exe"), "0.1.1", directory)
            && written is null && markerReadPath is null, "portable copy cannot alter another installation");
        Assert(!Refresh(Path.Combine(directory, "nested", "SaruMon.exe"), "0.1.1", directory)
            && written is null, "child directory is not the registered install directory");
        Assert(!Refresh(executable, "0.1.1", null) && written is null, "missing registration remains missing");
        Assert(!Refresh(executable, "0.1.1", "relative-folder") && written is null, "reject relative registered path");
        Assert(!Refresh("SaruMon.exe", "0.1.1", directory) && written is null, "reject relative process path");
        Assert(!Refresh(null, "0.1.1", directory) && written is null, "missing process path is harmless");
        Assert(!Refresh(Path.Combine(directory, "Another.exe"), "0.1.1", directory) && written is null, "check application filename");
        Assert(!Refresh(executable, "not-a-version", directory) && written is null, "do not write malformed version");
        Assert(!Refresh(executable, null, directory) && written is null, "missing version is harmless");
        Assert(!Refresh(executable, "0.1.1", directory, "0.1.1")
            && written is null && markerReadPath is null, "already current version needs no write");
        Assert(!Refresh(executable, "0.1.1", directory, read: _ => "[Install]\nProduct=AnotherProduct")
            && written is null, "wrong product marker cannot update registry");
        Assert(!Refresh(executable, "0.1.1", directory, read: _ => throw new FileNotFoundException())
            && written is null, "missing marker is nonfatal");
        Assert(!Refresh(executable, "0.1.1", directory, read: _ => throw new IOException())
            && written is null, "marker I/O error is nonfatal");
        Assert(!Refresh(executable, "0.1.1", directory, write: _ => throw new UnauthorizedAccessException()), "registry access denial is nonfatal");
        Assert(!Refresh(executable, "0.1.1", directory, write: _ => throw new SecurityException()), "registry security failure is nonfatal");
        Assert(!Refresh(executable, "0.1.1", directory, write: _ => throw new IOException()), "registry I/O failure is nonfatal");
        Assert(InstallationInfo.HasProductMarker("\uFEFF; comment\n[install]\n PRODUCT = SaruMon-Local-Install-v1\nVersion=old\nDesktopShortcut=1"), "INI whitespace, BOM, comments, and key case");
        Assert(!InstallationInfo.HasProductMarker("[Other]\nProduct=SaruMon-Local-Install-v1"), "product must be in Install section");
        Assert(!InstallationInfo.HasProductMarker("Product=SaruMon-Local-Install-v1\n[Install]\nVersion=0.1.1"), "unsectioned product is not valid");
        Assert(!InstallationInfo.HasProductMarker(marker + "Product=Other"), "reject duplicate conflicting product");
        Assert(!InstallationInfo.HasProductMarker(marker + "Product=SaruMon-Local-Install-v1"), "reject duplicate product");
        Assert(!InstallationInfo.HasProductMarker("[Install]\nProduct=sarumon-local-install-v1"), "product value must match exactly");
        Assert(InstallationInfo.HasProductMarker(marker), "installer version and shortcut metadata remain accepted unchanged");
        return count;
    }
}

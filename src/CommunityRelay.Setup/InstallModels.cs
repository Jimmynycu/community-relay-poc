using System.Text.Json.Serialization;

namespace CommunityRelay.Setup;

internal sealed record InstallRequest(
    string InstallDirectory,
    bool CreateStartMenuShortcut,
    bool CreateDesktopShortcut,
    bool VerificationMode);

internal sealed record InstallResult(
    string InstallDirectory,
    string ApplicationPath,
    string Version,
    int PayloadFileCount,
    IReadOnlyList<string> Shortcuts);

internal sealed class PayloadManifest
{
    public string Version { get; set; } = string.Empty;

    public List<PayloadFile> Files { get; set; } = [];
}

internal sealed class PayloadFile
{
    public string Path { get; set; } = string.Empty;

    public long Length { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class InstalledManifest
{
    public int SchemaVersion { get; set; }

    public string Product { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string InstallId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string InstallDirectory { get; set; } = string.Empty;

    public DateTimeOffset InstalledAtUtc { get; set; }

    public List<string> Files { get; set; } = [];

    public List<string> Shortcuts { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool VerificationInstall { get; set; }
}

internal sealed class InstallMarker
{
    public int SchemaVersion { get; set; }

    public string Product { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string InstallId { get; set; } = string.Empty;

    public string InstallDirectory { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}

internal sealed class CommandLineOptions
{
    public string InstallDirectory { get; set; } = InstallerEngine.DefaultInstallDirectory;

    public bool InstallDirectorySpecified { get; set; }

    public bool Silent { get; set; }

    public bool VerificationMode { get; set; }

    public bool CreateStartMenuShortcut { get; set; } = true;

    public bool CreateDesktopShortcut { get; set; }

    public bool LaunchAfterInstall { get; set; } = true;

    public bool ShowHelp { get; set; }
}

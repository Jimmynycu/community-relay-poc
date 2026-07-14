using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommunityRelay.Setup;

internal static class InstallerEngine
{
    internal const string ProductName = "Community Relay POC";
    internal const string ProductId = "community-relay-poc.windows";
    internal const int InstallSchemaVersion = 1;
    internal const string ApplicationFileName = "CommunityRelay.exe";
    internal const string InstalledManifestFileName = "install.json";
    internal const string UninstallerFileName = "Uninstall Community Relay.ps1";
    internal const string VerificationResultFileName = "verification-result.json";
    internal const string MarkerFileName = ".community-relay-install";

    private const string PayloadResourceName = "CommunityRelay.Setup.Payload.zip";
    private const string PayloadManifestResourceName = "CommunityRelay.Setup.Payload.manifest.json";
    private const string UninstallerResourceName = "CommunityRelay.Setup.Uninstall.ps1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "CommunityRelayPOC");

    public static InstallResult Install(InstallRequest request, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installRoot = NormalizeInstallRoot(request.InstallDirectory);
        report?.Invoke("Reading the embedded SHA-256 integrity inventory...");
        var payloadManifest = LoadPayloadManifest();
        ValidatePayloadManifest(payloadManifest);

        PathSafety.AssertNoReparsePointsInExistingAncestry(installRoot);
        var target = InspectInstallTarget(installRoot, payloadManifest);
        var installId = target.OwnedInstallation?.InstallId ?? Guid.NewGuid();

        var parent = Directory.GetParent(installRoot)?.FullName
            ?? throw new InvalidDataException("The installation folder must have a parent directory.");
        PathSafety.AssertNoReparsePointsInExistingAncestry(parent);
        Directory.CreateDirectory(parent);
        PathSafety.AssertNoReparsePointsInExistingAncestry(parent);

        var leafName = Path.GetFileName(installRoot);
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(parent, $".{leafName}.stage-{transactionId}");
        var backupRoot = Path.Combine(parent, $".{leafName}.backup-{transactionId}");
        if (Directory.Exists(stagingRoot) || File.Exists(stagingRoot) ||
            Directory.Exists(backupRoot) || File.Exists(backupRoot))
        {
            throw new IOException("A unique installation transaction directory could not be allocated.");
        }

        var previouslyOwnedShortcuts = target.OwnedInstallation?.Manifest.Shortcuts?.ToList() ?? [];
        var selectedShortcuts = request.VerificationMode
            ? previouslyOwnedShortcuts.ToList()
            : ShortcutService.GetSelectedShortcutPaths(
                request.CreateStartMenuShortcut,
                request.CreateDesktopShortcut).ToList();
        if (!request.VerificationMode)
        {
            ShortcutService.ValidateNoUnownedShortcutCollisions(
                selectedShortcuts,
                previouslyOwnedShortcuts);
        }

        HashSet<string> trackedFiles;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            PathSafety.AssertNoReparsePointsInTree(stagingRoot);

            report?.Invoke("Staging and hash-checking the complete self-contained application...");
            ExtractAndValidatePayload(stagingRoot, payloadManifest);

            trackedFiles = payloadManifest.Files
                .Select(file => NormalizeRelativePath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            trackedFiles.Add(UninstallerFileName);
            trackedFiles.Add(MarkerFileName);
            if (request.VerificationMode)
            {
                trackedFiles.Add(VerificationResultFileName);
            }

            if (target.OwnedInstallation is not null)
            {
                CopyUnknownFilesFromExistingInstall(
                    installRoot,
                    stagingRoot,
                    target.OwnedInstallation.Manifest,
                    trackedFiles);
            }

            WriteUninstallerResource(
                Path.Combine(stagingRoot, UninstallerFileName),
                trackedFiles);

            var marker = new InstallMarker
            {
                SchemaVersion = InstallSchemaVersion,
                Product = ProductName,
                ProductId = ProductId,
                InstallId = installId.ToString("D"),
                InstallDirectory = installRoot,
                Version = payloadManifest.Version
            };
            WriteJson(Path.Combine(stagingRoot, MarkerFileName), marker);

            if (request.VerificationMode)
            {
                WriteJson(
                    Path.Combine(stagingRoot, VerificationResultFileName),
                    new
                    {
                        verified = true,
                        version = payloadManifest.Version,
                        payloadFiles = payloadManifest.Files.Count,
                        verifiedAtUtc = DateTimeOffset.UtcNow,
                        transaction = "staged-and-atomic"
                    });
            }

            var installedManifest = new InstalledManifest
            {
                SchemaVersion = InstallSchemaVersion,
                Product = ProductName,
                ProductId = ProductId,
                InstallId = installId.ToString("D"),
                Version = payloadManifest.Version,
                InstallDirectory = installRoot,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                Files = trackedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                Shortcuts = selectedShortcuts,
                VerificationInstall = request.VerificationMode
            };
            WriteJson(Path.Combine(stagingRoot, InstalledManifestFileName), installedManifest);

            VerifyInstalledPayload(stagingRoot, payloadManifest);
            ValidateStagedMetadata(stagingRoot, installRoot, installId, payloadManifest.Version);
            PathSafety.AssertNoReparsePointsInTree(stagingRoot);
        }
        catch
        {
            PathSafety.TryDeleteTreeNoReparse(stagingRoot);
            throw;
        }

        ShortcutSnapshot? shortcutSnapshot = null;
        try
        {
            if (!request.VerificationMode)
            {
                shortcutSnapshot = ShortcutService.CaptureSnapshot(
                    selectedShortcuts.Concat(previouslyOwnedShortcuts));
            }
        }
        catch
        {
            PathSafety.TryDeleteTreeNoReparse(stagingRoot);
            throw;
        }

        var oldMoved = false;
        var newActivated = false;
        try
        {
            report?.Invoke("Atomically activating the verified installation...");
            if (target.Exists)
            {
                Directory.Move(installRoot, backupRoot);
                oldMoved = true;
            }

            Directory.Move(stagingRoot, installRoot);
            newActivated = true;
            PathSafety.AssertNoReparsePointsInTree(installRoot);
            LoadAndValidateOwnedInstallation(installRoot, payloadManifest);

            if (!request.VerificationMode)
            {
                report?.Invoke("Applying the selected product shortcuts...");
                ShortcutService.ApplySelection(
                    installRoot,
                    request.CreateStartMenuShortcut,
                    request.CreateDesktopShortcut,
                    previouslyOwnedShortcuts);
            }

            if (request.VerificationMode)
            {
                VerifyInstalledPayload(installRoot, payloadManifest);
            }
        }
        catch (Exception commitFailure)
        {
            Exception? rollbackFailure = null;
            try
            {
                if (newActivated && Directory.Exists(installRoot))
                {
                    Directory.Move(installRoot, stagingRoot);
                    newActivated = false;
                }

                if (oldMoved && Directory.Exists(backupRoot))
                {
                    Directory.Move(backupRoot, installRoot);
                    oldMoved = false;
                }

                shortcutSnapshot?.Restore();
                PathSafety.TryDeleteTreeNoReparse(stagingRoot);
            }
            catch (Exception exception)
            {
                rollbackFailure = exception;
            }

            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Installation failed and automatic rollback also encountered an error.",
                    commitFailure,
                    rollbackFailure);
            }

            throw new InvalidOperationException(
                "Installation failed before commit completed; the previous installation was restored.",
                commitFailure);
        }

        if (Directory.Exists(backupRoot))
        {
            PathSafety.TryDeleteTreeNoReparse(backupRoot);
        }

        var applicationPath = Path.Combine(installRoot, ApplicationFileName);
        if (!File.Exists(applicationPath))
        {
            throw new InvalidDataException($"The committed installation does not contain {ApplicationFileName}.");
        }

        report?.Invoke("Installation complete.");
        return new InstallResult(
            installRoot,
            applicationPath,
            payloadManifest.Version,
            payloadManifest.Files.Count,
            selectedShortcuts);
    }

    public static void LaunchApplication(string applicationPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
            UseShellExecute = true
        });
    }

    private static string NormalizeInstallRoot(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("Choose an installation folder.", nameof(requestedPath));
        }

        var fullPath = PathSafety.NormalizeDirectory(
            Environment.ExpandEnvironmentVariables(requestedPath.Trim()));
        var root = Path.GetPathRoot(fullPath);
        if (PathSafety.PathsEqual(fullPath, root ?? fullPath))
        {
            throw new ArgumentException("Installing directly into a drive root is not supported.", nameof(requestedPath));
        }

        return fullPath;
    }

    private static TargetInspection InspectInstallTarget(string installRoot, PayloadManifest payloadManifest)
    {
        if (File.Exists(installRoot))
        {
            throw new InvalidDataException("The selected installation path is an existing file.");
        }

        if (!Directory.Exists(installRoot))
        {
            return new TargetInspection(Exists: false, OwnedInstallation: null);
        }

        PathSafety.AssertNoReparsePointsInTree(installRoot);
        if (!Directory.EnumerateFileSystemEntries(installRoot).Any())
        {
            return new TargetInspection(Exists: true, OwnedInstallation: null);
        }

        try
        {
            return new TargetInspection(
                Exists: true,
                OwnedInstallation: LoadAndValidateOwnedInstallation(installRoot, payloadManifest));
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "The selected folder is not empty and is not a valid Community Relay POC installation. Choose an empty folder.",
                exception);
        }
    }

    private static OwnedInstallation LoadAndValidateOwnedInstallation(
        string installRoot,
        PayloadManifest payloadManifest)
    {
        PathSafety.AssertNoReparsePointsInTree(installRoot);
        var markerPath = Path.Combine(installRoot, MarkerFileName);
        var manifestPath = Path.Combine(installRoot, InstalledManifestFileName);
        if (!File.Exists(markerPath) || !File.Exists(manifestPath))
        {
            throw new InvalidDataException("The product ownership marker or install manifest is missing.");
        }

        var marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The product ownership marker is empty.");
        var manifest = JsonSerializer.Deserialize<InstalledManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("The install manifest is empty.");

        ValidateIdentity(marker.SchemaVersion, marker.Product, marker.ProductId, marker.InstallDirectory, installRoot);
        ValidateIdentity(manifest.SchemaVersion, manifest.Product, manifest.ProductId, manifest.InstallDirectory, installRoot);
        if (!Guid.TryParse(marker.InstallId, out var markerId) || markerId == Guid.Empty ||
            !Guid.TryParse(manifest.InstallId, out var manifestId) || markerId != manifestId)
        {
            throw new InvalidDataException("The product ownership marker and install manifest IDs do not match.");
        }

        if (string.IsNullOrWhiteSpace(marker.Version) ||
            !string.Equals(marker.Version, manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, payloadManifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The existing installation is not the exact payload version supported by this setup executable.");
        }

        var files = manifest.Files ?? throw new InvalidDataException("The install manifest has no file inventory.");
        var normalizedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var normalized = NormalizeRelativePath(file);
            if (!normalizedFiles.Add(normalized))
            {
                throw new InvalidDataException($"Duplicate path in the install manifest: {normalized}");
            }

            var fullPath = PathSafety.GetSafeChildPath(installRoot, normalized);
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException($"A tracked installation file is missing: {normalized}");
            }
        }

        var allowedTrackedFiles = payloadManifest.Files
            .Select(file => NormalizeRelativePath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowedTrackedFiles.Add(MarkerFileName);
        allowedTrackedFiles.Add(UninstallerFileName);
        if (manifest.VerificationInstall)
        {
            allowedTrackedFiles.Add(VerificationResultFileName);
        }

        if (!normalizedFiles.SetEquals(allowedTrackedFiles))
        {
            throw new InvalidDataException("The install manifest file inventory differs from the embedded product allowlist.");
        }

        VerifyInstalledPayload(installRoot, payloadManifest);
        ShortcutService.ValidateRecordedShortcutPaths(manifest.Shortcuts);
        return new OwnedInstallation(markerId, marker, manifest);
    }

    private static void ValidateIdentity(
        int schemaVersion,
        string product,
        string productId,
        string recordedInstallDirectory,
        string actualInstallDirectory)
    {
        if (schemaVersion != InstallSchemaVersion ||
            !string.Equals(product, ProductName, StringComparison.Ordinal) ||
            !string.Equals(productId, ProductId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(recordedInstallDirectory) ||
            !PathSafety.PathsEqual(recordedInstallDirectory, actualInstallDirectory))
        {
            throw new InvalidDataException("The product ownership identity is invalid.");
        }
    }

    private static void ValidateStagedMetadata(
        string stagingRoot,
        string finalInstallRoot,
        Guid installId,
        string version)
    {
        var marker = JsonSerializer.Deserialize<InstallMarker>(
            File.ReadAllText(Path.Combine(stagingRoot, MarkerFileName)),
            JsonOptions) ?? throw new InvalidDataException("The staged ownership marker is empty.");
        var manifest = JsonSerializer.Deserialize<InstalledManifest>(
            File.ReadAllText(Path.Combine(stagingRoot, InstalledManifestFileName)),
            JsonOptions) ?? throw new InvalidDataException("The staged install manifest is empty.");

        ValidateIdentity(marker.SchemaVersion, marker.Product, marker.ProductId, marker.InstallDirectory, finalInstallRoot);
        ValidateIdentity(manifest.SchemaVersion, manifest.Product, manifest.ProductId, manifest.InstallDirectory, finalInstallRoot);
        if (!Guid.TryParse(marker.InstallId, out var markerId) || markerId != installId ||
            !Guid.TryParse(manifest.InstallId, out var manifestId) || manifestId != installId ||
            !string.Equals(marker.Version, version, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged ownership metadata is inconsistent.");
        }

        ShortcutService.ValidateRecordedShortcutPaths(manifest.Shortcuts);
    }

    private static void CopyUnknownFilesFromExistingInstall(
        string existingRoot,
        string stagingRoot,
        InstalledManifest existingManifest,
        HashSet<string> currentTrackedFiles)
    {
        PathSafety.AssertNoReparsePointsInTree(existingRoot);
        var previousTrackedFiles = (existingManifest.Files ?? [])
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        previousTrackedFiles.Add(InstalledManifestFileName);

        foreach (var sourcePath in Directory.EnumerateFiles(existingRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(existingRoot, sourcePath));
            if (previousTrackedFiles.Contains(relativePath) || currentTrackedFiles.Contains(relativePath))
            {
                continue;
            }

            var destinationPath = PathSafety.GetSafeChildPath(stagingRoot, relativePath);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new IOException($"An untracked file conflicts with the new application payload: {relativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            PathSafety.AssertNoReparsePointsInExistingAncestry(destinationPath);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static PayloadManifest LoadPayloadManifest()
    {
        using var stream = OpenRequiredResource(PayloadManifestResourceName);
        return JsonSerializer.Deserialize<PayloadManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("The embedded payload manifest is empty.");
    }

    private static void ValidatePayloadManifest(PayloadManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("The embedded payload manifest is incomplete.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"Duplicate payload path: {relativePath}");
            }

            if (file.Length < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"Invalid payload metadata for {relativePath}.");
            }
        }
    }

    private static void ExtractAndValidatePayload(string stagingRoot, PayloadManifest manifest)
    {
        var expectedFiles = manifest.Files.ToDictionary(
            file => NormalizeRelativePath(file.Path),
            StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var payloadStream = OpenRequiredResource(PayloadResourceName);
        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.FullName);
            if (!expectedFiles.TryGetValue(relativePath, out var expected) || !extractedFiles.Add(relativePath))
            {
                throw new InvalidDataException($"Unexpected or duplicate file in installer payload: {relativePath}");
            }

            var destinationPath = PathSafety.GetSafeChildPath(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            PathSafety.AssertNoReparsePointsInExistingAncestry(destinationPath);
            using var source = entry.Open();
            using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, bytesRead);
                hasher.AppendData(buffer, 0, bytesRead);
                totalBytes += bytesRead;
            }

            destination.Flush(flushToDisk: true);
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (totalBytes != expected.Length ||
                !actualHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Payload integrity check failed for {relativePath}.");
            }
        }

        var missing = expectedFiles.Keys.Except(extractedFiles, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException($"Installer payload is missing: {string.Join(", ", missing)}");
        }
    }

    private static void VerifyInstalledPayload(string root, PayloadManifest manifest)
    {
        PathSafety.AssertNoReparsePointsInTree(root);
        foreach (var expected in manifest.Files)
        {
            var path = PathSafety.GetSafeChildPath(root, NormalizeRelativePath(expected.Path));
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length)
            {
                throw new InvalidDataException($"Installed file is missing or has the wrong length: {expected.Path}");
            }

            using var stream = File.OpenRead(path);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Installed file hash differs: {expected.Path}");
            }
        }
    }

    private static void WriteUninstallerResource(
        string destinationPath,
        IEnumerable<string> trackedFiles)
    {
        const string allowlistToken = "__COMMUNITY_RELAY_TRACKED_FILES_BASE64__";
        using var stream = OpenRequiredResource(UninstallerResourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var template = reader.ReadToEnd();
        if (!template.Contains(allowlistToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The uninstaller template is missing its tracked-file allowlist token.");
        }

        var allowlistJson = JsonSerializer.Serialize(
            trackedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            JsonOptions);
        var allowlistBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(allowlistJson));
        var rendered = template.Replace(allowlistToken, allowlistBase64, StringComparison.Ordinal);
        File.WriteAllText(
            destinationPath,
            rendered,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static Stream OpenRequiredResource(string resourceName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Required embedded resource not found: {resourceName}");

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"Unsafe relative path: {value}");
        }

        var normalized = value.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "." or ""))
        {
            throw new InvalidDataException($"Unsafe relative path: {value}");
        }

        return normalized;
    }

    private sealed record TargetInspection(bool Exists, OwnedInstallation? OwnedInstallation);

    private sealed record OwnedInstallation(
        Guid InstallId,
        InstallMarker Marker,
        InstalledManifest Manifest);
}

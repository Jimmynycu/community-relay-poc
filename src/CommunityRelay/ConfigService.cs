using System.Text.Json;

namespace CommunityRelay;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ConfigService(string? path = null)
    {
        _path = path ?? AppPaths.ConfigFile;
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return (JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            var backupPath = $"{_path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(_path, backupPath, overwrite: true);
            }
            catch
            {
                // Loading safe defaults is more important than preserving a second corrupt copy.
            }
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        config.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFiles.WriteAllText(_path, JsonSerializer.Serialize(config, JsonOptions));
    }
}

public static class AtomicFiles
{
    public static void WriteAllText(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            Replace(temporaryPath, path);
        }
        finally
        {
            DeleteBestEffort(temporaryPath);
        }
    }

    public static void WriteAllBytes(string path, byte[] content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            Replace(temporaryPath, path);
        }
        finally
        {
            DeleteBestEffort(temporaryPath);
        }
    }

    public static void DeleteTemporarySiblings(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return;
        }

        var prefix = $"{fileName}.";
        const string suffix = ".tmp";
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(
                         directory,
                         $"{fileName}.*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                var candidateName = Path.GetFileName(candidate);
                if (!candidateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !candidateName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var token = candidateName[prefix.Length..^suffix.Length];
                if (Guid.TryParseExact(token, "N", out _))
                {
                    DeleteBestEffort(candidate);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is privacy hardening. A later canonical load still decides
            // whether startup can continue safely.
        }
    }

    private static void Replace(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original write/load outcome if cleanup itself fails.
        }
    }
}

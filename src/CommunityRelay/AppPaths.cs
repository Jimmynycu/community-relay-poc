namespace CommunityRelay;

public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string SecretsFile => Path.Combine(Root, "secrets.bin");
    public static string StateFile => Path.Combine(Root, "relay-state.json");
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(LogsDirectory, "community-relay.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("COMMUNITY_RELAY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var portableMarker = Path.Combine(AppContext.BaseDirectory, "portable.flag");
        if (File.Exists(portableMarker))
        {
            return Path.Combine(AppContext.BaseDirectory, "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CommunityRelayPOC");
    }
}

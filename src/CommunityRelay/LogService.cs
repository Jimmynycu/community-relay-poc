using System.Text.RegularExpressions;

namespace CommunityRelay;

public sealed partial class LogService
{
    private const long MaximumLogBytes = 1_000_000;
    private readonly string _path;
    private readonly object _sync = new();

    public LogService(string? path = null)
    {
        _path = path ?? AppPaths.LogFile;
    }

    public void Write(RelayEvent relayEvent)
    {
        var safeMessage = Sanitize(relayEvent.Message);
        var line = $"{relayEvent.Timestamp:O}\t{relayEvent.Level}\t{safeMessage}{Environment.NewLine}";
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfNeeded();
            File.AppendAllText(_path, line);
        }
    }

    public static string Sanitize(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        singleLine = BearerRegex().Replace(singleLine, "Bearer [REDACTED]");
        singleLine = SecretRegex().Replace(singleLine, "$1=[REDACTED]");
        singleLine = RedditFullnameRegex().Replace(singleLine, "[POST_ID]");
        return singleLine.Length > 800 ? singleLine[..800] : singleLine;
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaximumLogBytes)
        {
            return;
        }

        var previousPath = $"{_path}.1";
        File.Delete(previousPath);
        File.Move(_path, previousPath);
    }

    [GeneratedRegex("Bearer\\s+[^\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(refresh_token|client_secret|access_token)\\s*[=:]\\s*[^\\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();

    [GeneratedRegex("\\bt[1-6]_[A-Za-z0-9_]+\\b", RegexOptions.IgnoreCase)]
    private static partial Regex RedditFullnameRegex();
}

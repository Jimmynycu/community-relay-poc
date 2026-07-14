namespace CommunityRelay.Setup;

internal static class PathSafety
{
    public static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root!;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);

    public static void AssertNoReparsePointsInExistingAncestry(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            AssertNotReparsePoint(fullPath);
        }

        DirectoryInfo? current;
        if (Directory.Exists(fullPath))
        {
            current = new DirectoryInfo(fullPath);
        }
        else
        {
            current = new DirectoryInfo(Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException($"Path has no parent directory: {fullPath}"));
        }

        while (current is not null)
        {
            if (current.Exists)
            {
                AssertNotReparsePoint(current.FullName);
            }

            current = current.Parent;
        }
    }

    public static void AssertNoReparsePointsInTree(string root)
    {
        var fullRoot = NormalizeDirectory(root);
        AssertNoReparsePointsInExistingAncestry(fullRoot);
        if (!Directory.Exists(fullRoot))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            AssertNotReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Reparse points and junctions are not allowed in an installation path: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    public static string GetSafeChildPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Unsafe relative path: {relativePath}");
        }

        var fullRoot = NormalizeDirectory(root);
        var rootWithSeparator = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path escapes its owned directory: {relativePath}");
        }

        AssertNoReparsePointsInExistingAncestry(candidate);
        return candidate;
    }

    public static void DeleteTreeNoReparse(string root)
    {
        var fullRoot = NormalizeDirectory(root);
        if (!Directory.Exists(fullRoot))
        {
            return;
        }

        AssertNoReparsePointsInTree(fullRoot);
        DeleteDirectoryContents(fullRoot);
        Directory.Delete(fullRoot, recursive: false);
    }

    public static void TryDeleteTreeNoReparse(string root)
    {
        try
        {
            DeleteTreeNoReparse(root);
        }
        catch
        {
            // Never follow an unexpected reparse point merely to clean up staging data.
        }
    }

    private static void DeleteDirectoryContents(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Refusing to delete through a reparse point: {entry}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryContents(entry);
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                File.Delete(entry);
            }
        }
    }

    private static void AssertNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Reparse points and junctions are not allowed in an installation path: {path}");
        }
    }
}

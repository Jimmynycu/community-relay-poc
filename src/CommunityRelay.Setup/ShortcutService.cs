using System.Reflection;
using System.Runtime.InteropServices;

namespace CommunityRelay.Setup;

internal static class ShortcutService
{
    private const string ProductFolder = "Community Relay POC";
    private const string StartMenuOverrideVariable = "COMMUNITY_RELAY_SETUP_START_MENU_ROOT";
    private const string DesktopOverrideVariable = "COMMUNITY_RELAY_SETUP_DESKTOP_ROOT";

    public static IReadOnlyList<string> GetSelectedShortcutPaths(bool startMenu, bool desktop)
    {
        var paths = GetExactProductShortcutPaths();
        var selected = new List<string>();
        if (startMenu)
        {
            selected.Add(paths.StartMenuApplication);
            selected.Add(paths.StartMenuUninstaller);
        }

        if (desktop)
        {
            selected.Add(paths.DesktopApplication);
        }

        return selected;
    }

    public static void ValidateRecordedShortcutPaths(IEnumerable<string>? recordedPaths)
    {
        var allowed = GetExactProductShortcutPaths().All
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recordedPath in recordedPaths ?? [])
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(recordedPath);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("The existing install manifest contains an invalid shortcut path.", exception);
            }

            if (!allowed.Contains(fullPath) || !seen.Add(fullPath))
            {
                throw new InvalidDataException("The existing install manifest contains a shortcut that is not owned by Community Relay POC.");
            }
        }
    }

    public static void ValidateNoUnownedShortcutCollisions(
        IEnumerable<string> selectedPaths,
        IEnumerable<string> previouslyOwnedPaths)
    {
        var allowed = GetExactProductShortcutPaths().All
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = previouslyOwnedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var selectedPath in selectedPaths.Select(Path.GetFullPath))
        {
            if (!allowed.Contains(selectedPath))
            {
                throw new InvalidDataException("A selected shortcut path is not an exact Community Relay product path.");
            }
            if (previous.Contains(selectedPath))
            {
                continue;
            }

            PathSafety.AssertNoReparsePointsInExistingAncestry(selectedPath);
            if (File.Exists(selectedPath) || Directory.Exists(selectedPath))
            {
                throw new IOException($"Setup will not overwrite a pre-existing shortcut it does not own: {selectedPath}");
            }
        }
    }

    public static ShortcutSnapshot CaptureSnapshot(IEnumerable<string> pathsToModify)
    {
        var allowed = GetExactProductShortcutPaths().All
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pathsToModify.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!allowed.Contains(path))
            {
                throw new InvalidDataException("A shortcut transaction path is not an exact Community Relay product path.");
            }
            PathSafety.AssertNoReparsePointsInExistingAncestry(path);
            if (Directory.Exists(path))
            {
                throw new InvalidDataException($"A directory occupies the product shortcut path: {path}");
            }

            contents[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        return new ShortcutSnapshot(contents);
    }

    public static IReadOnlyList<string> ApplySelection(
        string installRoot,
        bool startMenu,
        bool desktop,
        IEnumerable<string> previouslyOwnedPaths)
    {
        var paths = GetExactProductShortcutPaths();
        var previouslyOwned = previouslyOwnedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (startMenu)
        {
            CreateShortcut(
                paths.StartMenuApplication,
                Path.Combine(installRoot, InstallerEngine.ApplicationFileName),
                string.Empty,
                installRoot,
                "Open Community Relay POC");

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            CreateShortcut(
                paths.StartMenuUninstaller,
                powershell,
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(installRoot, InstallerEngine.UninstallerFileName)}\"",
                installRoot,
                "Uninstall Community Relay POC");
        }
        else
        {
            var removedStartMenuShortcut = false;
            if (previouslyOwned.Contains(paths.StartMenuApplication))
            {
                DeleteExactShortcut(paths.StartMenuApplication);
                removedStartMenuShortcut = true;
            }
            if (previouslyOwned.Contains(paths.StartMenuUninstaller))
            {
                DeleteExactShortcut(paths.StartMenuUninstaller);
                removedStartMenuShortcut = true;
            }
            if (removedStartMenuShortcut)
            {
                TryDeleteEmptyProductFolder(Path.GetDirectoryName(paths.StartMenuApplication)!);
            }
        }

        if (desktop)
        {
            CreateShortcut(
                paths.DesktopApplication,
                Path.Combine(installRoot, InstallerEngine.ApplicationFileName),
                string.Empty,
                installRoot,
                "Open Community Relay POC");
        }
        else
        {
            if (previouslyOwned.Contains(paths.DesktopApplication))
            {
                DeleteExactShortcut(paths.DesktopApplication);
            }
        }

        return GetSelectedShortcutPaths(startMenu, desktop);
    }

    private static ProductShortcutPaths GetExactProductShortcutPaths()
    {
        var startMenuRoot = ResolveShellRoot(
            StartMenuOverrideVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Start Menu");
        var desktopRoot = ResolveShellRoot(
            DesktopOverrideVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Desktop");
        var productStartMenu = Path.Combine(startMenuRoot, "Programs", ProductFolder);
        return new ProductShortcutPaths(
            Path.GetFullPath(Path.Combine(productStartMenu, "Community Relay POC.lnk")),
            Path.GetFullPath(Path.Combine(productStartMenu, "Uninstall Community Relay POC.lnk")),
            Path.GetFullPath(Path.Combine(desktopRoot, "Community Relay POC.lnk")));
    }

    private static string ResolveShellRoot(string overrideVariable, string defaultPath, string displayName)
    {
        var overridePath = Environment.GetEnvironmentVariable(overrideVariable);
        var selected = string.IsNullOrWhiteSpace(overridePath) ? defaultPath : overridePath;
        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidOperationException($"The current user's {displayName} folder is unavailable.");
        }

        return Path.GetFullPath(selected);
    }

    private static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string description)
    {
        PathSafety.AssertNoReparsePointsInExistingAncestry(shortcutPath);
        var parent = Path.GetDirectoryName(shortcutPath)!;
        Directory.CreateDirectory(parent);
        PathSafety.AssertNoReparsePointsInExistingAncestry(shortcutPath);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable; the shortcut could not be created.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows Script Host could not be started.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            if (shortcut is null)
            {
                throw new InvalidOperationException("Windows Script Host did not return a shortcut object.");
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [arguments]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [description]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{targetPath},0"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void DeleteExactShortcut(string shortcutPath)
    {
        PathSafety.AssertNoReparsePointsInExistingAncestry(shortcutPath);
        if (Directory.Exists(shortcutPath))
        {
            throw new InvalidDataException($"A directory occupies the product shortcut path: {shortcutPath}");
        }

        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static void TryDeleteEmptyProductFolder(string folder)
    {
        try
        {
            PathSafety.AssertNoReparsePointsInExistingAncestry(folder);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch
        {
            // Leaving an empty product folder is safer than broad cleanup.
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed record ProductShortcutPaths(
        string StartMenuApplication,
        string StartMenuUninstaller,
        string DesktopApplication)
    {
        public IReadOnlyList<string> All =>
            [StartMenuApplication, StartMenuUninstaller, DesktopApplication];
    }
}

internal sealed class ShortcutSnapshot
{
    private readonly IReadOnlyDictionary<string, byte[]?> _contents;

    public ShortcutSnapshot(IReadOnlyDictionary<string, byte[]?> contents)
    {
        _contents = contents;
    }

    public void Restore()
    {
        List<Exception> failures = [];
        foreach (var (path, content) in _contents)
        {
            try
            {
                PathSafety.AssertNoReparsePointsInExistingAncestry(path);
                if (content is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    PathSafety.AssertNoReparsePointsInExistingAncestry(path);
                    File.WriteAllBytes(path, content);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("One or more product shortcuts could not be restored during rollback.", failures);
        }
    }
}

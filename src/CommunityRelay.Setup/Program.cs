namespace CommunityRelay.Setup;

internal static class Program
{
    private const string HelpText = """
        Community Relay POC Setup

        Double-click without arguments for the graphical installer.

        Command-line options:
          --install-dir <path>          Override the per-user installation folder
          --silent                      Install without showing the graphical installer
          --start-menu-shortcut         Create Start Menu app and uninstall shortcuts
          --no-start-menu-shortcut      Do not create Start Menu shortcuts
          --desktop-shortcut            Create a Desktop shortcut
          --no-desktop-shortcut         Do not create a Desktop shortcut
          --no-shortcuts                Do not create any shortcuts
          --launch / --no-launch        Launch or do not launch after a graphical install
          --verify                      Silent integrity-verification install; forces no shortcuts
          --help                        Show this help
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        CommandLineOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Community Relay Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            MessageBox.Show(HelpText, "Community Relay Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        if (options.VerificationMode || options.Silent)
        {
            try
            {
                InstallerEngine.Install(new InstallRequest(
                    options.InstallDirectory,
                    options.VerificationMode ? false : options.CreateStartMenuShortcut,
                    options.VerificationMode ? false : options.CreateDesktopShortcut,
                    options.VerificationMode));
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm(options));
        return 0;
    }

    private static CommandLineOptions ParseArguments(string[] args)
    {
        var result = new CommandLineOptions();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--install-dir":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("--install-dir requires a path.");
                    }

                    result.InstallDirectory = args[index];
                    result.InstallDirectorySpecified = true;
                    break;
                case "--silent":
                    result.Silent = true;
                    result.LaunchAfterInstall = false;
                    break;
                case "--verify":
                    result.VerificationMode = true;
                    result.Silent = true;
                    result.CreateStartMenuShortcut = false;
                    result.CreateDesktopShortcut = false;
                    result.LaunchAfterInstall = false;
                    break;
                case "--start-menu-shortcut":
                    result.CreateStartMenuShortcut = true;
                    break;
                case "--no-start-menu-shortcut":
                    result.CreateStartMenuShortcut = false;
                    break;
                case "--desktop-shortcut":
                    result.CreateDesktopShortcut = true;
                    break;
                case "--no-desktop-shortcut":
                    result.CreateDesktopShortcut = false;
                    break;
                case "--no-shortcuts":
                    result.CreateStartMenuShortcut = false;
                    result.CreateDesktopShortcut = false;
                    break;
                case "--launch":
                    result.LaunchAfterInstall = true;
                    break;
                case "--no-launch":
                    result.LaunchAfterInstall = false;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown setup option: {args[index]}");
            }
        }

        if (result.VerificationMode)
        {
            if (!result.InstallDirectorySpecified && !result.ShowHelp)
            {
                throw new ArgumentException("--verify requires an explicit --install-dir path.");
            }

            result.CreateStartMenuShortcut = false;
            result.CreateDesktopShortcut = false;
            result.LaunchAfterInstall = false;
        }

        return result;
    }
}

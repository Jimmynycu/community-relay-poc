namespace CommunityRelay.Setup;

internal sealed class InstallerForm : Form
{
    private readonly TextBox _installDirectory;
    private readonly CheckBox _startMenuShortcut;
    private readonly CheckBox _desktopShortcut;
    private readonly CheckBox _launchAfterInstall;
    private readonly Button _installButton;
    private readonly Button _cancelButton;
    private readonly Label _status;
    private bool _busy;
    private bool _allowClose;

    public InstallerForm(CommandLineOptions options)
    {
        Text = "Community Relay POC Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "Install Community Relay POC",
            Font = new Font("Segoe UI Semibold", 17F),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        Controls.Add(title);

        var description = new Label
        {
            Text = "A self-contained Windows app for safely testing Reddit native crosspost relays.",
            AutoSize = true,
            Location = new Point(27, 61)
        };
        Controls.Add(description);

        var folderLabel = new Label
        {
            Text = "Install for this Windows user in:",
            AutoSize = true,
            Location = new Point(27, 99)
        };
        Controls.Add(folderLabel);

        _installDirectory = new TextBox
        {
            Text = options.InstallDirectory,
            Location = new Point(30, 122),
            Size = new Size(510, 23)
        };
        Controls.Add(_installDirectory);

        var browseButton = new Button
        {
            Text = "Browse...",
            Location = new Point(550, 120),
            Size = new Size(82, 27)
        };
        browseButton.Click += BrowseButton_Click;
        Controls.Add(browseButton);

        _startMenuShortcut = new CheckBox
        {
            Text = "Create Start Menu shortcuts (app and uninstaller)",
            Checked = options.CreateStartMenuShortcut,
            AutoSize = true,
            Location = new Point(30, 164)
        };
        Controls.Add(_startMenuShortcut);

        _desktopShortcut = new CheckBox
        {
            Text = "Create a Desktop shortcut",
            Checked = options.CreateDesktopShortcut,
            AutoSize = true,
            Location = new Point(30, 190)
        };
        Controls.Add(_desktopShortcut);

        _launchAfterInstall = new CheckBox
        {
            Text = "Launch Community Relay after installation",
            Checked = options.LaunchAfterInstall,
            AutoSize = true,
            Location = new Point(30, 216)
        };
        Controls.Add(_launchAfterInstall);

        _status = new Label
        {
            Text = "Ready to install. No administrator access is required.",
            AutoEllipsis = true,
            Location = new Point(30, 254),
            Size = new Size(410, 35)
        };
        Controls.Add(_status);

        _installButton = new Button
        {
            Text = "Install",
            Location = new Point(456, 271),
            Size = new Size(84, 31)
        };
        _installButton.Click += InstallButton_Click;
        Controls.Add(_installButton);

        _cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(548, 271),
            Size = new Size(84, 31)
        };
        Controls.Add(_cancelButton);
        CancelButton = _cancelButton;
        AcceptButton = _installButton;
        FormClosing += InstallerForm_FormClosing;
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where Community Relay POC should be installed",
            UseDescriptionForTitle = true,
            SelectedPath = _installDirectory.Text,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installDirectory.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            IProgress<string> progress = new Progress<string>(message => _status.Text = message);
            var request = new InstallRequest(
                _installDirectory.Text,
                _startMenuShortcut.Checked,
                _desktopShortcut.Checked,
                VerificationMode: false);
            var result = await Task.Run(() => InstallerEngine.Install(request, progress.Report));
            _status.Text = $"Installed version {result.Version}.";
            MessageBox.Show(
                this,
                $"Community Relay POC was installed successfully in:{Environment.NewLine}{result.InstallDirectory}",
                "Installation complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            if (_launchAfterInstall.Checked)
            {
                InstallerEngine.LaunchApplication(result.ApplicationPath);
            }

            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _status.Text = "Installation failed.";
            MessageBox.Show(
                this,
                exception.Message,
                "Community Relay Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _installButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _installDirectory.Enabled = !busy;
        _startMenuShortcut.Enabled = !busy;
        _desktopShortcut.Enabled = !busy;
        _launchAfterInstall.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void InstallerForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && !_allowClose)
        {
            e.Cancel = true;
            _status.Text = "Installation is committing verified files. Please wait for it to finish.";
        }
    }
}

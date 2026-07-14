using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CommunityRelay;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly SecretStore _secretStore;
    private readonly StateStore _stateStore;
    private readonly LogService _logService;
    private AppConfig _config;
    private CancellationTokenSource? _relayCancellation;
    private Task? _relayTask;
    private CancellationTokenSource? _operationCancellation;
    private Task? _operationTask;
    private bool _operationBusy;
    private bool _closeAfterQuiescence;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _configService = new ConfigService();
        _logService = new LogService();
        _stateStore = CreateStateStoreWithRecovery();
        _stateStore.PurgeOlderThan(DateTimeOffset.UtcNow.AddHours(-48));
        _secretStore = CreateSecretStoreWithRecovery();
        _config = _configService.Load();
        PopulateForm(_config);
        UpdateModeSummary();
        AddActivity(new RelayEvent(
            DateTimeOffset.Now,
            RelayEventLevel.Info,
            "Ready. Run the offline demo or complete Setup for approved API access."));
    }

    public ObservableCollection<ActivityItem> Activity { get; } = [];

    private static StateStore CreateStateStoreWithRecovery()
    {
        try
        {
            return new StateStore();
        }
        catch (InvalidOperationException exception)
        {
            var response = MessageBox.Show(
                $"The local relay state is unreadable. Delete it and start with an empty state?\n\n" +
                $"No Reddit action will occur.\n\n{LogService.Sanitize(exception.Message)}",
                "Recover relay state",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes)
            {
                throw;
            }

            File.Delete(AppPaths.StateFile);
            return new StateStore();
        }
    }

    private static SecretStore CreateSecretStoreWithRecovery()
    {
        var store = new SecretStore();
        try
        {
            store.EnsureReadable();
            return store;
        }
        catch (InvalidOperationException exception)
        {
            var response = MessageBox.Show(
                $"The encrypted Reddit authorization store is unreadable for this Windows user. " +
                $"Remove it and continue? You will need to authorize again.\n\n" +
                LogService.Sanitize(exception.Message),
                "Recover authorization",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes)
            {
                throw;
            }

            store.ResetAll();
            return store;
        }
    }

    private async void RunDemoButton_Click(object sender, RoutedEventArgs e)
    {
        DemoModeCheckBox.IsChecked = true;
        DryRunCheckBox.IsChecked = true;
        if (!SaveSettings(showConfirmation: false))
        {
            return;
        }

        await RunOperationAsync("Running offline demo…", async cancellationToken =>
        {
            await using var client = new DemoRedditClient();
            var engine = CreateEngine(client);
            await engine.RunDemoSequenceAsync(cancellationToken);
        });
    }

    private async void RunOnceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showConfirmation: true))
        {
            return;
        }

        await RunOperationAsync("Polling once…", async cancellationToken =>
        {
            await using var client = CreateClient();
            if (!client.IsDemo)
            {
                await ValidateLiveDestinationAsync(client, cancellationToken);
            }
            var engine = CreateEngine(client);
            await engine.RunOnceAsync(cancellationToken);
        });
    }

    private async void TestOneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showConfirmation: true))
        {
            return;
        }

        var prompt = _config.DemoMode
            ? "Simulate exactly one native crosspost? No Reddit call will be made."
            : $"Create exactly one real native crosspost in r/{_config.DestinationSubreddit}?\n\n" +
              "This is a write action. It will use the newest eligible source post and cannot be undone by this dialog.";
        if (MessageBox.Show(
                prompt,
                "Confirm one crosspost",
                MessageBoxButton.YesNo,
                _config.DemoMode ? MessageBoxImage.Information : MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Testing one crosspost…", async cancellationToken =>
        {
            await using var client = CreateClient();
            if (!client.IsDemo)
            {
                await ValidateLiveDestinationAsync(client, cancellationToken);
            }
            var engine = CreateEngine(client);
            await engine.TestOneAsync(cancellationToken);
        });
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_relayTask is { IsCompleted: false } || !SaveSettings(showConfirmation: true))
        {
            return;
        }

        _relayCancellation = new CancellationTokenSource();
        var cancellationToken = _relayCancellation.Token;
        SetRunningState(isRunning: true);
        SetStatus("Starting…", isError: false);

        _relayTask = Task.Run(async () =>
        {
            await using var client = CreateClient();
            if (!client.IsDemo)
            {
                await ValidateLiveDestinationAsync(client, cancellationToken);
            }
            var engine = CreateEngine(client);
            await engine.RunContinuousAsync(cancellationToken);
        }, cancellationToken);
        _ = ObserveRelayTaskAsync(_relayTask);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _relayCancellation?.Cancel();
        SetStatus("Stopping…", isError: false);
    }

    private async Task ObserveRelayTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Emergency stop is a normal path.
        }
        catch (Exception exception)
        {
            AddActivity(new RelayEvent(
                DateTimeOffset.Now,
                RelayEventLevel.Error,
                $"Continuous relay failed: {SafeMessage(exception)}"));
            SetStatus("Stopped with error", isError: true);
        }
        finally
        {
            _relayCancellation?.Dispose();
            _relayCancellation = null;
            SetRunningState(isRunning: false);
            if (StatusText.Text != "Stopped with error")
            {
                SetStatus("Stopped", isError: false);
            }
        }
    }

    private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadFormIntoConfig();
        }
        catch (FormatException exception)
        {
            MessageBox.Show(exception.Message, "Invalid number", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var errors = _config.Normalize().Validate(requireLiveFields: true);
        if (errors.Count > 0)
        {
            ShowValidationErrors(errors);
            return;
        }
        _configService.Save(_config);
        SaveEnteredClientSecret();

        await RunOperationAsync("Waiting for browser authorization…", async cancellationToken =>
        {
            var oauth = new RedditOAuthService();
            await oauth.AuthorizeAsync(_config, _secretStore, cancellationToken);
            Dispatcher.Invoke(() =>
            {
                SecretStoredText.Text = "Authorization token stored with Windows DPAPI.";
                ClientSecretPasswordBox.Clear();
            });
            AddActivity(new RelayEvent(
                DateTimeOffset.Now,
                RelayEventLevel.Success,
                "OAuth authorization completed; the refresh token is DPAPI-encrypted."));
        });
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(showConfirmation: true))
        {
            return;
        }

        await RunOperationAsync("Testing connection…", async cancellationToken =>
        {
            await using var client = CreateClient();
            var identity = client.IsDemo
                ? await client.ValidateAsync("demo_animals", cancellationToken)
                : await ValidateLiveDestinationAsync(client, cancellationToken);
            AddActivity(new RelayEvent(
                DateTimeOffset.Now,
                RelayEventLevel.Success,
                client.IsDemo
                    ? "Offline demo connection is healthy."
                    : $"Connected as u/{identity.Username}; destination is {identity.DestinationKind}."));
        });
    }

    private void ClearAuthorizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Remove the encrypted Reddit refresh token for this Windows user?",
                "Clear authorization",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _secretStore.ClearAuthorization();
        SecretStoredText.Text = _secretStore.HasClientSecret
            ? "Client secret is DPAPI-encrypted; no authorization token is stored."
            : "No client secret or authorization token is stored.";
        AddActivity(new RelayEvent(
            DateTimeOffset.Now,
            RelayEventLevel.Info,
            "Encrypted Reddit authorization token removed."));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveSettings(showConfirmation: true))
        {
            AddActivity(new RelayEvent(
                DateTimeOffset.Now,
                RelayEventLevel.Success,
                "Settings saved. No Reddit post was created."));
            SetStatus(_config.DemoMode ? "Demo ready" : _config.DryRun ? "Dry run ready" : "Live ready", false);
        }
    }

    private bool SaveSettings(bool showConfirmation)
    {
        try
        {
            ReadFormIntoConfig();
        }
        catch (FormatException exception)
        {
            MessageBox.Show(exception.Message, "Invalid number", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var errors = _config.Normalize().Validate(requireLiveFields: !_config.DemoMode);
        if (errors.Count > 0)
        {
            ShowValidationErrors(errors);
            return false;
        }

        if (showConfirmation && !_config.DemoMode && !_config.DryRun)
        {
            var response = MessageBox.Show(
                $"Enable live writes to r/{_config.DestinationSubreddit}?\n\n" +
                $"The app may create up to {_config.MaxForwardsPerHour} native crossposts per hour " +
                $"and {_config.MaxForwardsPerDay} per day. It sends the title required for a native crosspost, but never downloads or re-uploads the source body or media.",
                "Confirm live mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes)
            {
                DryRunCheckBox.IsChecked = true;
                _config.DryRun = true;
                return false;
            }
        }

        _configService.Save(_config);
        SaveEnteredClientSecret();
        UpdateModeSummary();
        return true;
    }

    private void ReadFormIntoConfig()
    {
        _config.ClientId = ClientIdTextBox.Text;
        _config.ContactUsername = ContactUsernameTextBox.Text;
        _config.DestinationSubreddit = DestinationTextBox.Text;
        _config.Sources = SplitValues(SourcesTextBox.Text, allowComma: true);
        _config.IncludeKeywords = SplitValues(IncludeKeywordsTextBox.Text, allowComma: true);
        _config.ExcludeKeywords = SplitValues(ExcludeKeywordsTextBox.Text, allowComma: true);
        _config.PollIntervalSeconds = ParseInteger(PollIntervalTextBox.Text, "Poll interval");
        _config.ScanLimitPerSource = ParseInteger(ScanLimitTextBox.Text, "Scan limit");
        _config.MaxForwardsPerHour = ParseInteger(HourlyCapTextBox.Text, "Hourly cap");
        _config.MaxForwardsPerDay = ParseInteger(DailyCapTextBox.Text, "Daily cap");
        _config.DemoMode = DemoModeCheckBox.IsChecked == true;
        _config.DryRun = DryRunCheckBox.IsChecked == true;
        _config.AllowNsfw = AllowNsfwCheckBox.IsChecked == true;
        _config.SkipStickied = SkipStickiedCheckBox.IsChecked == true;
        _config.ApiApprovalConfirmed = ApiApprovalCheckBox.IsChecked == true;
        _config.PrivateDestinationConfirmed = PrivateDestinationCheckBox.IsChecked == true;
        _config.ModeratorConfirmed = ModeratorCheckBox.IsChecked == true;
    }

    private void PopulateForm(AppConfig config)
    {
        ClientIdTextBox.Text = config.ClientId;
        ContactUsernameTextBox.Text = config.ContactUsername;
        DestinationTextBox.Text = config.DestinationSubreddit;
        SourcesTextBox.Text = string.Join(Environment.NewLine, config.Sources);
        IncludeKeywordsTextBox.Text = string.Join(", ", config.IncludeKeywords);
        ExcludeKeywordsTextBox.Text = string.Join(", ", config.ExcludeKeywords);
        PollIntervalTextBox.Text = config.PollIntervalSeconds.ToString();
        ScanLimitTextBox.Text = config.ScanLimitPerSource.ToString();
        HourlyCapTextBox.Text = config.MaxForwardsPerHour.ToString();
        DailyCapTextBox.Text = config.MaxForwardsPerDay.ToString();
        DemoModeCheckBox.IsChecked = config.DemoMode;
        DryRunCheckBox.IsChecked = config.DryRun;
        AllowNsfwCheckBox.IsChecked = config.AllowNsfw;
        SkipStickiedCheckBox.IsChecked = config.SkipStickied;
        ApiApprovalCheckBox.IsChecked = config.ApiApprovalConfirmed;
        PrivateDestinationCheckBox.IsChecked = config.PrivateDestinationConfirmed;
        ModeratorCheckBox.IsChecked = config.ModeratorConfirmed;
        SecretStoredText.Text = _secretStore.HasRefreshToken
            ? "Authorization token is DPAPI-encrypted for this Windows user."
            : _secretStore.HasClientSecret
                ? "Client secret is DPAPI-encrypted; no authorization token is stored."
                : "No client secret or authorization token is stored.";
    }

    private void SaveEnteredClientSecret()
    {
        if (ClientSecretPasswordBox.SecurePassword.Length == 0)
        {
            return;
        }
        _secretStore.SetClientSecret(ClientSecretPasswordBox.Password);
        ClientSecretPasswordBox.Clear();
        SecretStoredText.Text = _secretStore.HasRefreshToken
            ? "Client secret and authorization token are DPAPI-encrypted."
            : "Client secret is DPAPI-encrypted; no authorization token is stored.";
    }

    private IRedditClient CreateClient() =>
        _config.DemoMode
            ? new DemoRedditClient()
            : new RedditApiClient(_config, _secretStore);

    private RelayEngine CreateEngine(IRedditClient client)
    {
        var engine = new RelayEngine(_config, client, _stateStore, _logService);
        engine.EventPublished += (_, relayEvent) => AddActivity(relayEvent);
        return engine;
    }

    private async Task<IdentityResult> ValidateLiveDestinationAsync(
        IRedditClient client,
        CancellationToken cancellationToken)
    {
        EnsureLiveApiPauseExpired();
        IdentityResult identity;
        try
        {
            identity = await client.ValidateAsync(_config.DestinationSubreddit, cancellationToken);
            if (client.RateLimits is { Remaining: <= 1 } limits)
            {
                var resetAfter = limits.ResetAfter is { } reported && reported > TimeSpan.Zero
                    ? reported
                    : TimeSpan.FromMinutes(1);
                PersistLiveApiPause(resetAfter);
                throw new RedditRateLimitException(resetAfter);
            }
        }
        catch (RedditRateLimitException rateLimit)
        {
            PersistLiveApiPause(rateLimit.RetryAfter);
            throw;
        }
        if (!identity.ModeratesDestination)
        {
            throw new RedditPermissionException(
                $"u/{identity.Username} does not moderate r/{_config.DestinationSubreddit}.");
        }
        if (!identity.IsSubscribedToDestination)
        {
            throw new RedditPermissionException(
                $"u/{identity.Username} must join r/{_config.DestinationSubreddit} before crossposting.");
        }
        if (!identity.DestinationKind.Equals("private", StringComparison.OrdinalIgnoreCase) &&
            !identity.DestinationKind.Equals("restricted", StringComparison.OrdinalIgnoreCase))
        {
            throw new RedditPermissionException(
                "The POC destination must be private or restricted. Change its community type first.");
        }
        return identity;
    }

    private void EnsureLiveApiPauseExpired()
    {
        var now = DateTimeOffset.UtcNow;
        if (_stateStore.GetApiNotBefore(isDemo: false) is { } notBefore && notBefore > now)
        {
            throw new InvalidOperationException(
                $"Reddit requested an API pause. Try again after {notBefore.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}.");
        }
    }

    private void PersistLiveApiPause(TimeSpan retryAfter)
    {
        var safeDelay = retryAfter <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : retryAfter;
        _stateStore.ExtendApiPause(isDemo: false, DateTimeOffset.UtcNow.Add(safeDelay));
    }

    private async Task RunOperationAsync(
        string status,
        Func<CancellationToken, Task> operation)
    {
        if (_operationBusy || _relayTask is { IsCompleted: false })
        {
            MessageBox.Show("Stop the current operation first.", "Busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _operationBusy = true;
        SetOperationButtonsEnabled(false);
        SetStatus(status, isError: false);
        _operationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            _operationTask = operation(_operationCancellation.Token);
            await _operationTask;
            SetStatus(_config.DemoMode ? "Demo ready" : _config.DryRun ? "Dry run ready" : "Live ready", false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled", isError: true);
        }
        catch (Exception exception)
        {
            var message = SafeMessage(exception);
            AddActivity(new RelayEvent(DateTimeOffset.Now, RelayEventLevel.Error, message));
            SetStatus("Action failed", isError: true);
            MessageBox.Show(message, "Community Relay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationTask = null;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _operationBusy = false;
            SetOperationButtonsEnabled(true);
        }
    }

    private void AddActivity(RelayEvent relayEvent)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddActivity(relayEvent));
            return;
        }
        Activity.Insert(0, new ActivityItem(relayEvent));
        while (Activity.Count > 500)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
        if (relayEvent.Level == RelayEventLevel.Error)
        {
            SetStatus("Attention required", isError: true);
        }
        else if (_relayTask is { IsCompleted: false })
        {
            SetStatus(_config.DryRun || _config.DemoMode ? "Running safely" : "Running live", false);
        }
    }

    private void UpdateModeSummary()
    {
        var sources = _config.Sources.Count == 0
            ? "no sources"
            : string.Join(", ", _config.Sources.Select(source => $"r/{source}"));
        ModeSummaryText.Text = _config.DemoMode
            ? $"Mode: offline demo • Sources: {sources} • Reddit writes: none"
            : _config.DryRun
                ? $"Mode: approved API dry run • Sources: {sources} • Reddit writes: none"
                : $"Mode: LIVE native crossposts • Sources: {sources} • Caps: {_config.MaxForwardsPerHour}/hour, {_config.MaxForwardsPerDay}/day";
    }

    private void SetRunningState(bool isRunning)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetRunningState(isRunning));
            return;
        }
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
        RunDemoButton.IsEnabled = !isRunning;
        RunOnceButton.IsEnabled = !isRunning;
        TestOneButton.IsEnabled = !isRunning;
        SaveButton.IsEnabled = !isRunning;
        AuthorizeButton.IsEnabled = !isRunning;
        TestConnectionButton.IsEnabled = !isRunning;
    }

    private void SetOperationButtonsEnabled(bool enabled)
    {
        RunDemoButton.IsEnabled = enabled;
        RunOnceButton.IsEnabled = enabled;
        TestOneButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        AuthorizeButton.IsEnabled = enabled;
        TestConnectionButton.IsEnabled = enabled;
    }

    private void SetStatus(string status, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(status, isError));
            return;
        }
        StatusText.Text = status;
        StatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#A43131" : "#275DAD"));
    }

    private static List<string> SplitValues(string value, bool allowComma)
    {
        var separators = allowComma ? new[] { '\r', '\n', ',' } : new[] { '\r', '\n' };
        return value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int ParseInteger(string value, string label) =>
        int.TryParse(value.Trim(), out var result)
            ? result
            : throw new FormatException($"{label} must be a whole number.");

    private static string SafeMessage(Exception exception) =>
        LogService.Sanitize(exception.Message);

    private static void ShowValidationErrors(IReadOnlyList<string> errors) =>
        MessageBox.Show(
            string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
            "Check setup",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private static void OpenUrl(string url)
    {
        if (!OfficialRedditLinks.TryParse(url, out var parsed))
        {
            MessageBox.Show(
                "Community Relay blocked a non-Reddit or non-HTTPS link.",
                "Link blocked",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
    }

    private void OpenSetupGuideButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void BackToDashboardButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void RequestApiAccessButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://support.reddithelp.com/hc/en-us/requests/new?tf_42139884615700=api_request_type_developer_clone&ticket_form_id=14868593862164");
    private void OAuthAppsButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.reddit.com/prefs/apps");
    private void CreateCommunityButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.reddit.com/subreddits/create");
    private void ResponsiblePolicyButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://support.reddithelp.com/hc/en-us/articles/42728983564564-Responsible-Builder-Policy");
    private void DataApiRulesButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://support.reddithelp.com/hc/en-us/articles/16160319875092-Reddit-Data-API-Wiki");
    private void DevvitDocsButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://developers.reddit.com/docs/");

    private void CommunitySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = SubredditNames.Normalize(DestinationTextBox.Text);
        OpenUrl(SubredditNames.IsValid(destination)
            ? $"https://www.reddit.com/mod/{destination}/settings/general"
            : "https://www.reddit.com/subreddits/create");
    }

    private void OpenDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = SubredditNames.Normalize(DestinationTextBox.Text);
        if (!SubredditNames.IsValid(destination))
        {
            MessageBox.Show("Enter a destination community in Setup first.", "Destination", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenUrl($"https://www.reddit.com/r/{destination}/new");
    }

    private void ActivityGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActivityGrid.SelectedItem is not ActivityItem item)
        {
            return;
        }
        var url = item.DestinationUrl ?? item.SourceUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterQuiescence)
        {
            return;
        }

        var activeTasks = new[] { _relayTask, _operationTask }
            .Where(task => task is { IsCompleted: false })
            .Cast<Task>()
            .ToArray();
        if (activeTasks.Length == 0)
        {
            return;
        }

        e.Cancel = true;
        _relayCancellation?.Cancel();
        _operationCancellation?.Cancel();
        SetStatus("Stopping safely…", isError: false);
        try
        {
            await Task.WhenAll(activeTasks);
        }
        catch
        {
            // The operation handlers already surface safe, sanitized errors.
        }

        _closeAfterQuiescence = true;
        Close();
    }
}

public sealed class ActivityItem
{
    public ActivityItem(RelayEvent relayEvent)
    {
        Time = relayEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Level = relayEvent.Level.ToString();
        Message = relayEvent.Message;
        SourceUrl = relayEvent.SourceUrl;
        DestinationUrl = relayEvent.DestinationUrl;
    }

    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public string? SourceUrl { get; }
    public string? DestinationUrl { get; }
}

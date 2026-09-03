using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class VoiceConsoleHostedService : BackgroundService
{
    private readonly RealtimeVoiceCoordinator _coordinator;
    private readonly VoiceOptions _options;
    private readonly LocalAiOptions _localAiOptions;
    private readonly ToolOptions _toolOptions;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<VoiceConsoleHostedService> _logger;

    public VoiceConsoleHostedService(
        RealtimeVoiceCoordinator coordinator,
        IOptions<VoiceOptions> options,
        IOptions<LocalAiOptions> localAiOptions,
        IOptions<ToolOptions> toolOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<VoiceConsoleHostedService> logger)
    {
        _coordinator = coordinator;
        _options = options.Value;
        _localAiOptions = localAiOptions.Value;
        _toolOptions = toolOptions.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteHelp();
        using CancellationTokenSource notificationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task notifications = DisplayNotificationsAsync(notificationCancellation.Token);

        if (_localAiOptions.Enabled && _options.WakeWord.AlwaysListeningEnabled)
        {
            await TryExecuteAsync(ArmWakeWordAsync, stoppingToken).ConfigureAwait(false);
        }
        else if (_localAiOptions.Enabled && _options.AutoStart)
        {
            await TryExecuteAsync(StartSessionAsync, stoppingToken).ConfigureAwait(false);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string? input = await Console.In.ReadLineAsync(stoppingToken).ConfigureAwait(false);
                if (input is null)
                {
                    break;
                }

                await HandleInputAsync(input.Trim(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            notificationCancellation.Cancel();
            try
            {
                await notifications.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleInputAsync(string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        switch (input.ToUpperInvariant())
        {
            case "/START":
                await TryExecuteAsync(StartDiagnosticSessionAsync, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "/STOP":
                await TryExecuteAsync(
                    token => _coordinator.StopAsync(token),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "/PTT":
                await TryExecuteAsync(BeginPushToTalkAsync, cancellationToken).ConfigureAwait(false);
                break;
            case "/SEND":
                await TryExecuteAsync(
                    token => _coordinator.EndPushToTalkAsync(token),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "/INTERRUPT":
                await TryExecuteAsync(
                    token => _coordinator.InterruptAsync(token),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "/FALSEWAKE":
                await TryExecuteAsync(ReportFalseWakeAsync, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "/HELP":
                WriteHelp();
                break;
            case "/QUIT":
                _applicationLifetime.StopApplication();
                break;
            default:
                await TryExecuteAsync(
                    token => _coordinator.SubmitTextAsync(input, token),
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private Task StartSessionAsync(CancellationToken cancellationToken) =>
        _coordinator.StartAsync(CreateConfiguration(), cancellationToken);

    private async Task StartDiagnosticSessionAsync(CancellationToken cancellationToken)
    {
        EnsureLocalAiEnabled();
        if (_coordinator.State != VoiceSessionState.Stopped)
        {
            await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await StartSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task ArmWakeWordAsync(CancellationToken cancellationToken)
    {
        EnsureLocalAiEnabled();
        return _coordinator.ArmWakeWordAsync(CreateConfiguration(), cancellationToken);
    }

    private async Task BeginPushToTalkAsync(CancellationToken cancellationToken)
    {
        EnsureLocalAiEnabled();
        if (_coordinator.State is VoiceSessionState.Stopped or VoiceSessionState.Sleeping)
        {
            if (_coordinator.State == VoiceSessionState.Sleeping)
            {
                await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            await _coordinator.StartAsync(
                CreateConfiguration(VoiceActivationMode.PushToTalk),
                cancellationToken).ConfigureAwait(false);
        }

        await _coordinator.BeginPushToTalkAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportFalseWakeAsync(CancellationToken cancellationToken)
    {
        _coordinator.RecordFalseActivation();
        if (!_options.WakeWord.AlwaysListeningEnabled)
        {
            return;
        }

        await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        await ArmWakeWordAsync(cancellationToken).ConfigureAwait(false);
    }

    private VoiceSessionConfiguration CreateConfiguration(
        VoiceActivationMode? activationMode = null) =>
        new(
            activationMode ?? _options.ActivationMode,
            _options.Persona,
            speechInputEnabled: _options.Enabled,
            speechOutputEnabled: _options.SpeechOutputEnabled,
            maximumOutputTokens: _localAiOptions.MaximumOutputTokens,
            responseSegmentation: new ResponseSegmentationConfiguration(
                _options.ResponseSegmentation.MinimumSentenceCharacters,
                _options.ResponseSegmentation.MinimumClauseCharacters,
                _options.ResponseSegmentation.MaximumSegmentCharacters),
            wakeWord: new WakeWordSessionConfiguration(
                _options.WakeWord.AlwaysListeningEnabled,
                _options.WakeWord.Phrase,
                _options.WakeWord.KeywordScore,
                _options.WakeWord.KeywordThreshold,
                TimeSpan.FromSeconds(_options.WakeWord.CooldownSeconds),
                TimeSpan.FromSeconds(_options.WakeWord.ContinuationWindowSeconds),
                _options.WakeWord.Acknowledgement));

    private void EnsureLocalAiEnabled()
    {
        if (!_localAiOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Local AI is disabled. Set LocalAi:Enabled to true first.");
        }
    }

    private async Task TryExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HostLog.VoiceCommandFailed(_logger, exception.GetType().Name);
            string message = exception is LocalComponentUnavailableException
                ? exception.Message
                : $"JARVIS command failed ({exception.GetType().Name}). " +
                    "See the structured log for the failure class.";
            Console.WriteLine(message);
        }
    }

    private async Task DisplayNotificationsAsync(CancellationToken cancellationToken)
    {
        await foreach (VoiceSessionNotification notification in
            _coordinator.ReadNotificationsAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (notification)
            {
                case VoiceSessionStateChangedNotification state:
                    if (state.State == VoiceSessionState.Listening)
                    {
                        Console.WriteLine();
                    }

                    Console.WriteLine($"[voice: {state.State}]");
                    break;
                case VoiceCaptureStateChangedNotification capture:
                    Console.WriteLine($"[capture: {capture.State}]");
                    break;
                case WakeWordDetectedNotification wake:
                    Console.WriteLine($"[wake word: {SanitizeForConsole(wake.Phrase)}]");
                    break;
                case AssistantTranscriptNotification transcript:
                    Console.Write(SanitizeForConsole(transcript.Text));
                    Console.Write(' ');
                    break;
                case UserTranscriptNotification transcript:
                    Console.WriteLine(
                        transcript.IsFinal
                            ? $"[heard: {SanitizeForConsole(transcript.Text)}]"
                            : $"[hearing: {SanitizeForConsole(transcript.Text)}]");
                    break;
                case VoiceSessionErrorNotification error:
                    Console.WriteLine(
                        $"[voice error: {error.Code}; transient: {error.IsTransient}]");
                    break;
            }
        }
    }

    private void WriteHelp()
    {
        Console.WriteLine("JARVIS local 0.4 voice, tools, project intelligence, and learning console");
        Console.WriteLine("/start  start a session manually (diagnostics)");
        Console.WriteLine("/stop   stop the active session");
        Console.WriteLine("/ptt    begin push-to-talk capture (push-to-talk mode)");
        Console.WriteLine("/send   stop capture and submit the push-to-talk turn");
        Console.WriteLine("/interrupt  stop the current assistant response");
        Console.WriteLine("/falsewake  record a false activation and return to sleep");
        Console.WriteLine("/quit   stop JARVIS cleanly");
        Console.WriteLine("Any other line is sent as text for debugging; /help repeats this list.");

        if (!_localAiOptions.Enabled)
        {
            Console.WriteLine("Local AI is disabled; see README.md for offline setup.");
        }
        else if (!_options.Enabled)
        {
            Console.WriteLine("Microphone input is disabled; text debugging remains available.");
        }

        if (!_toolOptions.Enabled)
        {
            Console.WriteLine("Computer tools are disabled; conversation remains available.");
        }
        else if (_toolOptions.AllowedRoots.Count == 0)
        {
            Console.WriteLine(
                "Filesystem tools are denied until at least one absolute Tools:AllowedRoots entry is configured.");
        }
    }

    private static string SanitizeForConsole(string text) =>
        string.Create(
            text.Length,
            text,
            static (destination, source) =>
            {
                for (int index = 0; index < source.Length; index++)
                {
                    char character = source[index];
                    destination[index] = !char.IsControl(character) ||
                        character is '\r' or '\n' or '\t'
                        ? character
                        : '\uFFFD';
                }
            });
}

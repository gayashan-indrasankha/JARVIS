using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class VoiceConsoleHostedService : BackgroundService
{
    private readonly RealtimeVoiceCoordinator _coordinator;
    private readonly VoiceOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<VoiceConsoleHostedService> _logger;

    public VoiceConsoleHostedService(
        RealtimeVoiceCoordinator coordinator,
        IOptions<VoiceOptions> options,
        IHostApplicationLifetime applicationLifetime,
        ILogger<VoiceConsoleHostedService> logger)
    {
        _coordinator = coordinator;
        _options = options.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteHelp();
        using CancellationTokenSource notificationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task notifications = DisplayNotificationsAsync(notificationCancellation.Token);

        if (_options.Enabled && _options.AutoStart)
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
                await TryExecuteAsync(StartSessionAsync, cancellationToken).ConfigureAwait(false);
                break;
            case "/STOP":
                await TryExecuteAsync(
                    token => _coordinator.StopAsync(token),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "/PTT":
                await TryExecuteAsync(
                    token => _coordinator.BeginPushToTalkAsync(token),
                    cancellationToken).ConfigureAwait(false);
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

    private Task StartSessionAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "Voice is disabled. Configure Voice:Enabled and a credential first.");
        }

        RealtimeSessionConfiguration configuration = new(
            _options.ActivationMode,
            _options.Instructions);
        return _coordinator.StartAsync(configuration, cancellationToken);
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
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException or IOException)
        {
            HostLog.VoiceCommandFailed(_logger, exception.GetType().Name);
            Console.WriteLine(
                $"JARVIS command failed ({exception.GetType().Name}). See the structured log for the failure class.");
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
                    Console.WriteLine($"[voice: {state.State}]");
                    break;
                case AssistantTranscriptNotification transcript:
                    Console.Write(SanitizeForConsole(transcript.Text));
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
        Console.WriteLine("JARVIS 0.1 realtime voice console");
        Console.WriteLine("/start  start a configured voice session");
        Console.WriteLine("/stop   stop the active session");
        Console.WriteLine("/ptt    begin push-to-talk capture (push-to-talk mode)");
        Console.WriteLine("/send   stop capture and submit the push-to-talk turn");
        Console.WriteLine("/interrupt  stop the current assistant response");
        Console.WriteLine("/quit   stop JARVIS cleanly");
        Console.WriteLine("Any other line is sent as text for debugging; /help repeats this list.");

        if (!_options.Enabled)
        {
            Console.WriteLine("Voice is disabled; see README.md for secret-safe setup.");
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

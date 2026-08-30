using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Voice.OpenAi;

internal sealed class OpenAiRealtimeProvider : IRealtimeConversationProvider
{
    private readonly IRealtimeTransportFactory _transportFactory;
    private readonly OpenAiRealtimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public OpenAiRealtimeProvider(
        IRealtimeTransportFactory transportFactory,
        IOptions<VoiceOptions> options,
        ILoggerFactory loggerFactory)
    {
        _transportFactory = transportFactory;
        _options = options.Value.OpenAi;
        _loggerFactory = loggerFactory;
    }

    public async Task<IRealtimeConversationSession> OpenSessionAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        OpenAiRealtimeSession session = new(
            _transportFactory,
            _options,
            _loggerFactory.CreateLogger<OpenAiRealtimeSession>());

        try
        {
            await session.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

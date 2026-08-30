using System.Buffers;
using System.Net.WebSockets;

namespace Jarvis.Infrastructure.Voice.OpenAi;

internal interface IRealtimeTransportFactory
{
    public IRealtimeTransport Create();
}

internal interface IRealtimeTransport : IAsyncDisposable
{
    public Task ConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    public ValueTask<RealtimeTransportMessage> ReceiveAsync(CancellationToken cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken);
}

internal readonly record struct RealtimeTransportMessage(
    ReadOnlyMemory<byte> Payload,
    bool IsClosed = false);

internal sealed class ClientWebSocketRealtimeTransportFactory : IRealtimeTransportFactory
{
    public IRealtimeTransport Create() => new ClientWebSocketRealtimeTransport();
}

internal sealed class ClientWebSocketRealtimeTransport : IRealtimeTransport
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageSize = 1024 * 1024;
    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        foreach ((string name, string value) in headers)
        {
            _socket.Options.SetRequestHeader(name, value);
        }

        await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            WebSocketMessageFlags.EndOfMessage,
            cancellationToken);

    public async ValueTask<RealtimeTransportMessage> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using MemoryStream message = new();
            while (true)
            {
                ValueWebSocketReceiveResult result = await _socket
                    .ReceiveAsync(rentedBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new RealtimeTransportMessage(default, IsClosed: true);
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("The realtime provider returned a non-text protocol message.");
                }

                if (message.Length + result.Count > MaximumMessageSize)
                {
                    throw new InvalidDataException("The realtime provider message exceeded the size limit.");
                }

                message.Write(rentedBuffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return new RealtimeTransportMessage(message.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: true);
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket
                .CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "JARVIS shutdown",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

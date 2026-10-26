using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ReplayBuffer.Contracts.Ipc;

/// <summary>
/// Writes frames: one UTF-8 JSON object per line, ending in <c>\n</c>. The payload is written
/// through its own generated <see cref="JsonTypeInfo{T}"/>, straight into the envelope.
/// </summary>
public static class FrameWriter
{
    public static byte[] Command<T>(string id, string type, T payload, JsonTypeInfo<T> payloadType) =>
        Write(id, type, seq: null, payload, payloadType);

    public static byte[] Reply<T>(string id, string type, T payload, JsonTypeInfo<T> payloadType) =>
        Write(id, type, seq: null, payload, payloadType);

    public static byte[] Event<T>(string type, long seq, T payload, JsonTypeInfo<T> payloadType) =>
        Write(id: null, type, seq, payload, payloadType);

    public static byte[] Error(string id, string type, string code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Protocol.Version);
            writer.WriteString("id", id);
            writer.WriteString("type", type);
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, new IpcError(code, message), IpcJsonContext.Default.IpcError);
            writer.WriteEndObject();
        }

        return EndLine(buffer);
    }

    private static byte[] Write<T>(string? id, string type, long? seq, T payload, JsonTypeInfo<T> payloadType)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Protocol.Version);
            if (id is not null)
            {
                writer.WriteString("id", id);
            }

            writer.WriteString("type", type);
            if (seq is { } value)
            {
                writer.WriteNumber("seq", value);
            }

            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, payload, payloadType);
            writer.WriteEndObject();
        }

        return EndLine(buffer);
    }

    private static byte[] EndLine(ArrayBufferWriter<byte> buffer)
    {
        var frame = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(frame);
        frame[^1] = (byte)'\n';
        return frame;
    }
}

/// <summary>
/// Reads frames from a stream: one JSON object per line. Blank lines are skipped. A line longer
/// than the limit, or one that isn't an envelope, is an <see cref="InvalidDataException"/>; the
/// caller drops the connection, since the stream can't be trusted after it.
/// </summary>
public sealed class FrameReader(Stream stream, int maxFrameBytes) : IAsyncDisposable
{
    private readonly PipeReader _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));

    /// <summary>The next frame, or <c>null</c> once the stream ends.</summary>
    public async ValueTask<Envelope?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (buffer.PositionOf((byte)'\n') is { } newline)
            {
                var line = buffer.Slice(0, newline);
                buffer = buffer.Slice(buffer.GetPosition(1, newline));
                if (line.Length > maxFrameBytes)
                {
                    throw new InvalidDataException($"A frame is longer than {maxFrameBytes} bytes.");
                }

                if (IsBlank(line))
                {
                    continue;
                }

                var envelope = Parse(line);
                _reader.AdvanceTo(buffer.Start);
                return envelope;
            }

            if (buffer.Length > maxFrameBytes)
            {
                throw new InvalidDataException($"A frame is longer than {maxFrameBytes} bytes.");
            }

            if (result.IsCompleted)
            {
                // A partial last line is a peer that died mid-write: the stream simply ended.
                _reader.AdvanceTo(buffer.End);
                return null;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public ValueTask DisposeAsync() => _reader.CompleteAsync();

    private static Envelope Parse(ReadOnlySequence<byte> line)
    {
        Envelope? envelope;
        try
        {
            var reader = new Utf8JsonReader(line);
            envelope = JsonSerializer.Deserialize(ref reader, IpcJsonContext.Default.Envelope);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("A frame is not a JSON envelope.", e);
        }

        if (envelope?.Type is not { Length: > 0 })
        {
            throw new InvalidDataException("A frame has no type.");
        }

        return envelope;
    }

    private static bool IsBlank(ReadOnlySequence<byte> line)
    {
        foreach (var segment in line)
        {
            foreach (var b in segment.Span)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

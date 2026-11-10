using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;

namespace Encore.Engine.Ipc;

/// <summary>A command's reply frame, and what to run once it is queued.</summary>
internal readonly record struct CommandReply(byte[] Frame, Action? AfterReply);

/// <summary>
/// Dispatches commands to their handlers (AD-3). Every command gets exactly one reply: the
/// handler's result, or an error with a code. A handler that throws something without a code
/// replies <see cref="ErrorCodes.CommandFailed"/>.
/// </summary>
internal sealed class CommandRouter(ILogger log)
{
    private delegate ValueTask<CommandReply> Handler(string id, JsonElement? payload, CancellationToken cancellationToken);

    private readonly ConcurrentDictionary<string, Handler> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the handler for <paramref name="command"/>. <paramref name="afterReply"/> runs once
    /// the reply is queued, so a command such as <c>engine.shutdown</c> can act after answering.
    /// </summary>
    public void Register<TArgs, TResult>(
        CommandType<TArgs, TResult> command,
        Func<TArgs, CancellationToken, ValueTask<TResult>> handler,
        Action? afterReply = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        var added = _handlers.TryAdd(command.Name, async (id, payload, cancellationToken) =>
        {
            TArgs args;
            try
            {
                args = ReadArgs(command, payload);
            }
            catch (JsonException e)
            {
                return new CommandReply(FrameWriter.Error(id, command.Name, ErrorCodes.InvalidArguments, e.Message), null);
            }

            var result = await handler(args, cancellationToken).ConfigureAwait(false);
            return new CommandReply(FrameWriter.Reply(id, command.Name, result, command.ResultType), afterReply);
        });
        if (!added)
        {
            throw new InvalidOperationException($"'{command.Name}' already has a handler.");
        }
    }

    /// <summary>Runs one command frame, which has an id, to its reply.</summary>
    public async ValueTask<CommandReply> DispatchAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var id = envelope.Id!;
        var type = envelope.Type!;
        if (envelope.V != Protocol.Version)
        {
            return Error(ErrorCodes.InvalidArguments, $"Protocol version {envelope.V} is not supported; this engine speaks {Protocol.Version}.");
        }

        if (!_handlers.TryGetValue(type, out var handler))
        {
            return Error(ErrorCodes.CommandUnknown, $"The engine has no command '{type}'.");
        }

        try
        {
            return await handler(id, envelope.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException e)
        {
            return Error(e.Code, e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log.LogError(e, "Command {Type} failed", type);
            return Error(ErrorCodes.CommandFailed, $"'{type}' failed: {e.Message}");
        }

        CommandReply Error(string code, string message) => new(FrameWriter.Error(id, type, code, message), null);
    }

    private static TArgs ReadArgs<TArgs, TResult>(CommandType<TArgs, TResult> command, JsonElement? payload)
    {
        if (payload is not { ValueKind: not JsonValueKind.Null } value)
        {
            return typeof(TArgs) == typeof(Empty)
                ? (TArgs)(object)Empty.Value
                : throw new JsonException($"'{command.Name}' needs a payload.");
        }

        return value.Deserialize(command.ArgsType) ?? throw new JsonException($"'{command.Name}' has a null payload.");
    }
}

namespace Encore.Engine.Ipc;

/// <summary>A command's reply frame, and what to run once it is queued.</summary>
internal readonly record struct CommandReply(byte[] Frame, Action? AfterReply);

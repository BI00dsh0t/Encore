using System.Security.Principal;

namespace Encore.Contracts.Ipc;

/// <summary>The engine's pipe (AD-3): <c>Encore.Engine.v1.&lt;instance&gt;.&lt;user SID&gt;</c>.</summary>
public static class PipeName
{
    public const string Prefix = "Encore.Engine.v1.";

    public static string For(string instanceName, string userSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceName);
        ArgumentException.ThrowIfNullOrEmpty(userSid);
        return $"{Prefix}{instanceName}.{userSid}";
    }

    /// <summary>The pipe of <paramref name="instanceName"/> for the user running this process.</summary>
    public static string ForCurrentUser(string instanceName) => For(instanceName, CurrentUserSid.Value);

    /// <summary>The SID of the user running this process.</summary>
    public static SecurityIdentifier CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The current process has no user SID.");
        }
    }
}

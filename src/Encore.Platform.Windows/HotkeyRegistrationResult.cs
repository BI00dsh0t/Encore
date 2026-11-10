using Encore.Contracts.Settings;

namespace Encore.Platform.Windows;

/// <summary>The result for one binding; Error is the Win32 error, or 0 when registered.</summary>
public sealed record HotkeyRegistrationResult(HotkeyAction Action, HotkeyRegistrationOutcome Outcome, int Error);

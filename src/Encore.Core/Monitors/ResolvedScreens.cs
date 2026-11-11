using Encore.Contracts.Messages;

namespace Encore.Core.Monitors;

/// <summary>The screens a Monitor Recording will use, and the chosen ones that aren't attached (by the id the setting holds).</summary>
public sealed record ResolvedScreens(IReadOnlyList<DisplayMonitor> Screens, IReadOnlyList<string> Missing);

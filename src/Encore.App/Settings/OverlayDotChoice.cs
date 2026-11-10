using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>One <c>overlayDot</c> choice on the General page.</summary>
internal sealed record OverlayDotChoice(OverlayDotMode Mode, string Label);

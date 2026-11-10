using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>One <c>appTheme</c> choice on the General page.</summary>
internal sealed record AppThemeChoice(AppTheme Theme, string Label);

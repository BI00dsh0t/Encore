namespace Encore.App.Settings;

/// <summary>One attached screen in the layout preview, in preview pixels; a record, so an unchanged layout keeps its tiles.</summary>
internal sealed record ScreenTile(ScreenRow Row, string Number, bool Primary, double Left, double Top, double Width, double Height);

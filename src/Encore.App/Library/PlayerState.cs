namespace Encore.App.Library;

/// <summary>Where the player page is: opening the file, paused, playing, or unable to play it.</summary>
internal enum PlayerState
{
    Opening,
    Paused,
    Playing,
    Failed,
}

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Flash;

/// <summary>
/// Reads native emote/animation state directly from FFXIVClientStructs, used to detect
/// when an emote animation has actually finished (as opposed to just "a different emote
/// wasn't detected").
/// </summary>
public static unsafe class NativeCharacterHelper
{
    /// <summary>
    /// True if the character at the given native address is currently playing an emote
    /// animation or looping one (e.g. sit/dance loops). Reads
    /// Character.EmoteController.IsEmoting()/IsInEmoteLoop() directly - both confirmed
    /// real member functions (FieldOffset 0x630 on Character), verified via the user's
    /// own installed FFXIVClientStructs.dll, not guessed.
    /// </summary>
    public static bool IsEmoting(nint characterAddress)
    {
        if (characterAddress == nint.Zero)
            return false;

        var character = (Character*)characterAddress;
        return character->EmoteController.IsEmoting() || character->EmoteController.IsInEmoteLoop();
    }
}

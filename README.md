# Flash

Changes a character's gear (head/body/hands/legs/feet/ears/neck/wrists/rings)
to either nothing ("Smallclothes") or the Emperor's New Set when a specified
emote is used. Gear reverts once the animation finishes, or after a set
Duration if that's enabled on the mapping. No Glamourer design pasting
required.

## Requirements

- FFXIV with XIVLauncher + Dalamud installed and working.
- .NET 10 SDK (`dotnet --version` should report a 10.x SDK).
- Glamourer installed and enabled in-game (this plugin talks to it over IPC;
  it does not modify gear itself).

## Before building

1. Open `Flash.csproj` and confirm the `Dalamud.NET.Sdk` version and
   `TargetFramework` match what your local Dalamud install actually reports
   (check `/xldev` in-game). Mismatches cause `CS1705` assembly-version
   errors on build.
2. `EmoteHook.cs`'s hook target (`EmoteManager.ExecuteEmote`) was confirmed
   directly against your installed `FFXIVClientStructs.dll` via IDE
   decompilation, not guessed - should be solid unless a future game patch
   reshapes that struct again.

## Build

```
dotnet build -c Debug
```

## Load it in-game for testing

1. In-game, run `/xlsettings` -> Experimental tab.
2. Under "Dev Plugin Locations", add the full path to the built `.dll` (or
   its containing folder).
3. Run `/xlplugins`, find Flash under Dev Tools, and load it.
4. Run `/flash` to open the config window ("Flash Config").

## Using it

1. Make sure Glamourer is running.
2. Pick a gear mode at the top of the window: **Smallclothes** (bare) or
   **Emperor's Set**.
3. Type part of an emote's name (e.g. "Dance") and click "Add mapping" once
   it matches.
4. Optionally set a **Delay (s)** per entry - how long to wait after the
   emote starts before gear changes.
5. Optionally check **Use Duration** and set **Duration (s)** to force gear
   back to normal after a fixed time even if the animation is still playing
   (e.g. to cut a looping emote's effect short deliberately). Off by
   default - gear only reverts once the animation actually finishes.
6. Use the `^`/`v` buttons on the left of a row to reorder the list
   (matching stops at the first enabled entry that fires, so order can
   matter if you ever have overlapping mappings).
7. Perform the emote in-game - gear changes per the mode above, and stays
   that way until you do something not configured (a different, unmapped
   emote, Duration elapsing, or - for looping emotes - simply nothing, since
   they never re-trigger the native hook while looping).

## How emote detection works

**Local player (primary path):** `EmoteHook.cs` hooks
`FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteManager.ExecuteEmote`
directly - the function FFXIVClientStructs' own doc comments confirm is
specifically used "for the local player". This fires the instant any emote
starts (wheel, `/emote`, macro), gives an exact numeric `EmoteId`, and does
not depend on any chat/log settings.

**Other characters (LocalPlayerOnly = false entries only):** falls back to
`EmoteWatcher.cs`, which listens for `XivChatType.StandardEmote`/
`CustomEmote` chat lines and matches by a case-insensitive substring check of
the emote's name against the rendered text. This only works if the other
player's client - not yours - has "Log Emotes" enabled (default on), and
Flash can't control that. Local-player chat lines are explicitly ignored in
this path since the native hook already owns that case; running both would
double-handle the same emote.

## Gear persistence model

Two complementary mechanisms decide when gear goes back to normal:

1. **Animation-end polling** (`NativeCharacterHelper.cs`, checked every frame
   in `Plugin.cs`): reads `Character.EmoteController.IsEmoting()` /
   `IsInEmoteLoop()` directly. The instant both go false for an altered
   character, gear reverts - covers a single emote playing out naturally and
   a looping emote being cancelled by movement or re-triggering.
2. **Duration** (optional per-entry, `UseDuration` + `DurationSeconds`):
   force-reverts gear after a fixed time even if the animation is still
   playing, letting a change be cut short deliberately.

`Plugin.cs` tracks which characters currently have Flash-altered gear via
`alteredCharacters`. A new *unmatched* emote for an already-altered character
also triggers an immediate revert, rather than waiting for polling to catch
up.

**Reverting preserves any prior Glamourer state, when possible.**
`RevertState` on its own discards any active Glamourer override entirely and
reverts to the character's actual equipped gear/body - so if you'd used
Glamourer to, say, swap genders, a plain revert would silently undo that
too. Instead, `GlamourerIpc.CaptureState` snapshots the character's full
current Glamourer state right before stripping, and `RevertCharacterGear`
restores that exact snapshot via `GlamourerIpc.RestoreState` instead of
calling `Revert`. If capturing or restoring the snapshot fails, it falls
back to the old `Revert` (real equipped gear) behavior rather than leaving
gear stuck.

**Known issue:** state preservation has not been confirmed reliable in
testing yet - `RestoreState` can report success without a visible change.
`GetStateBase64`/`ApplyState` are confirmed to exist in Glamourer's IPC
surface, and the `ApplyFlag.Equipment | ApplyFlag.Customization` fix (see
the confidence note in `GlamourerIpc.cs`) addressed one confirmed cause of a
silent no-op, but if it's still not restoring correctly, that's the next
thing to dig into - the fallback to plain `Revert` at least keeps gear from
getting permanently stuck either way.

## Known limitations / things to sanity-check yourself

- `GlamourerIpc.StrippableSlots` covers head, body, hands, legs, feet, ears,
  neck, wrists, and both rings - not weapons/offhand.
- `ResolveEmperorsSetItemIds` looks up each Emperor's Set item by exact name
  match in Lumina's `Item` sheet at startup and logs a warning to `/xllog`
  for any it can't find (check there if Emperor's Set mode seems to skip a
  slot).
- `StripAllGear` sends item ID `0` for "nothing" on Smallclothes mode. Stains
  are sent as `List<byte> { 0, 0 }` (see the confidence note at the top of
  `GlamourerIpc.cs`) - both were confirmed via live runtime errors during
  testing, not guessed.
- Remote-player detection (`EmoteWatcher.cs`) is best-effort text matching
  and language-dependent; see "How emote detection works" above.

## Debugging

Run `/flash log` (or the "Debug Log" button in Flash Config) to open a
dedicated, persistent window listing every local-player emote you use, with
its resolved name, real numeric ID, and whether it matched a configured
entry - plus a Copy button per row. It's always recording in the background,
no toggle needed - just use an emote, then check the log. Only covers
local-player native detections (the ones that carry a numeric ID);
chat-detected remote-player emotes aren't listed here since they never have
one.

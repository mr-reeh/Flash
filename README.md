# Flash

Changes a character's gear (head/body/hands/legs/feet/ears/neck/wrists/rings)
to either nothing ("Smallclothes") or the Emperor's New Set when a specified
emote or combat action is used. Gear reverts once the animation finishes
(emotes) or after a set duration (actions, or emotes with Duration enabled).
No Glamourer design pasting required.

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
4. Run `/emotegear` to open the config window ("Flash Config").

## Using it

1. Make sure Glamourer is running.
2. Pick a gear mode at the top of the window: **Smallclothes** (bare) or
   **Emperor's Set**.
3. Type part of an emote's name (e.g. "Dance") and click "Add mapping" once
   it matches.
4. Optionally set a **Delay (s)** per entry - how long to wait after the
   emote starts before gear changes.
5. Drag the `::` handle on the left of a row to reorder the list (matching
   only stops at the first enabled entry that fires, so order can matter if
   you ever have overlapping triggers).
5. Perform the emote in-game - gear changes per the mode above, and stays
   that way until you do something not configured (a different, unmapped
   emote, or - for looping emotes - simply nothing, since they never
   re-trigger the native hook while looping).

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
   `IsInEmoteLoop()` directly. The instant both go false for an
   Emote-triggered altered character, gear reverts. This only applies to
   Emote entries - Actions have no animation state to poll.
2. **Duration** (optional per-entry, `UseDuration` + `DurationSeconds`):
   force-reverts gear after a fixed time even if the animation is still
   playing, letting you cut a change short deliberately. **Action entries
   always use this regardless of the checkbox** - it's their only way back
   to normal gear, since there's no emote animation to detect the end of.
3. **Immediate revert on an unmatched new emote**: if a *different* emote is
   detected for an already-altered character and it isn't configured, gear
   reverts right away rather than waiting for polling to catch up.

`Plugin.cs` tracks which characters currently have Flash-altered gear (and
which trigger type caused it) via `alteredCharacters`.

**Reverting preserves any prior Glamourer state.** `RevertState` on its own
discards any active Glamourer override entirely and reverts to the
character's actual equipped gear/body - so if you'd used Glamourer to, say,
swap genders, Flash's revert would silently undo that too. Instead,
`GlamourerIpc.CaptureState` snapshots the character's full current Glamourer
state right before stripping (`ProcessPendingStrips`), and all three revert
paths above call `RevertCharacterGear`, which restores that exact snapshot
via `GlamourerIpc.RestoreState` instead of calling `Revert`. If capturing or
restoring the snapshot fails for any reason, it falls back to the old
`Revert` (real equipped gear) behavior rather than leaving gear stuck.

CaptureState/RestoreState use Glamourer's `GetStateBase64`/`ApplyState` IPC,
confirmed to exist but not independently verified parameter-by-parameter the
way `SetItem` was (see the confidence note in `GlamourerIpc.cs`) - if a
runtime error shows up here, it's the same kind of one-round fix `SetItem`
needed.

## Combat action triggers

`ActionHook.cs` hooks `FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseAction`
directly - the central entry point for the local player using any action
(weaponskill, spell, item, mount, general action, etc.), confirmed against
the user's installed `FFXIVClientStructs.dll`. It fires whenever the game
accepts an action (the underlying call returns `true`).

**Caveat** (from FFXIVClientStructs' own doc comment on `UseAction`): near a
cooldown/animation-lock boundary the action gets *queued* rather than
executed immediately, so this can fire slightly before the actual effect, or
for something that ends up queued/cancelled. Fine for "flash briefly when
Provoke is used," not frame-perfect for syncing to a specific VFX moment.

Action entries have no animation state to poll (unlike emotes), so **Duration
is always forced on for them** regardless of the checkbox - it's their only
way back to normal gear. The Add-mapping UI defaults new Action entries to a
2s Duration; adjust it in the table after adding.

Pick Emote vs Action via the radio buttons above the search box in the Add
row - both search their respective Lumina sheet (`Emote`/`Action`) by name,
the same safe-lookup pattern either way.

## Known limitations / things to sanity-check yourself

- `GlamourerIpc.StrippableSlots` covers head, body, hands, legs, feet, ears,
  neck, wrists, and both rings - not weapons/offhand.
- `ResolveEmperorsSetItemIds` looks up each Emperor's Set item by exact name
  match in Lumina's `Item` sheet at startup and logs a warning to `/xllog`
  for any it can't find (check there if Emperor's Set mode seems to skip a
  slot). If Square Enix ever renames one of these items, the lookup for that
  slot will fail gracefully (that slot just won't change) rather than crash.
- `StripAllGear` sends item ID `0` for "nothing" on Smallclothes mode. Stains
  are sent as `List<byte> { 0, 0 }` (see the confidence note at the top of
  `GlamourerIpc.cs`) - both were confirmed via live runtime errors during
  testing, not guessed.
- Remote-player detection (`EmoteWatcher.cs`) is best-effort text matching
  and language-dependent; see "How emote detection works" above.

## Debugging

Run `/emotegear debug` (or check "Debug mode" in the config window). With it
on, every emote Flash sees - native or chat-based - gets echoed to chat and
`/xllog`, along with which step it passed or failed at (no match, Glamourer
unavailable, per-slot results, etc.). Toggle it off once confirmed working,
since it's noisy.

For finding the exact ID of an emote or action to enter in the manual
override field, use **`/emotegear log`** (or the "Debug Log" button in Flash
Config) instead - it opens a dedicated, persistent window listing every
locally-detected emote/action with its resolved name, real numeric ID, and
whether it matched a configured entry, plus a Copy button per row. Unlike
Debug Mode's chat echo, this is always recording in the background and
doesn't need to be toggled on first - just use the emote/action, then check
the log. Only covers local-player native detections (the ones that actually
carry a numeric ID); chat-detected remote-player emotes aren't listed here
since they never have one (see "How emote detection works").

# Flash

Applies a Glamourer gear design automatically when a specified emote is used,
and optionally reverts back to your real gear afterward.

## Requirements

- FFXIV with XIVLauncher + Dalamud installed and working.
- .NET 10 SDK (`dotnet --version` should report a 10.x SDK) - Dalamud moved to
  .NET 10 as of its v14 API level.
- Glamourer installed and enabled in-game (this plugin talks to it over IPC;
  it does not modify gear itself).

## Before building

1. Open `Flash.csproj` and confirm the `Dalamud.NET.Sdk` version and
   `TargetFramework` match what your local Dalamud install actually reports
   (check `/xldev` in-game or the Dalamud.dll file properties). If they don't
   match you'll get `CS1705` assembly-version mismatch errors on build.
2. See "How emote detection works now" below before relying on this for
   anything precision-critical - it's chat-text matching, not exact ID
   matching.

## Build

```
dotnet build -c Debug
```

Output DLL lands under `bin/Debug/Flash.dll` (or similar,
depending on SDK version).

## Load it in-game for testing

1. In-game, run `/xlsettings` -> Experimental tab.
2. Under "Dev Plugin Locations", add the full path to the built `.dll`
   (or its containing folder).
3. Run `/xlplugins`, find Flash under "Dev Tools" / installed
   dev plugins, and load it.
4. Run `/emotegear` to open the config window.

## Using it

1. Make sure Glamourer is running and you have at least one saved design.
2. In Glamourer, right-click the design you want -> copy its base64 string
   to your clipboard.
3. In the Flash window, type part of an emote's name (e.g.
   "Dance"), paste the design string into the box, and click "Add mapping".
4. Optionally toggle "Revert after" and set a delay (in seconds) so your
   gear reverts back automatically once the emote is done playing.
5. Perform the emote in-game (via `/emote`, the emote wheel, or a macro) -
   the gear should swap immediately.

## Changelog / build fixes applied

- `Flash.csproj`: bumped to `Dalamud.NET.Sdk/15.0.0` and `TargetFramework` to
  `net10.0-windows`, matching Dalamud 15.0.3.2 (Dalamud moved to .NET 10 as of
  its v14 API level). If your installed Dalamud version differs, both of these
  need to match it or you'll get `CS1705` assembly-version mismatch errors.
- `PluginUi.cs`: `using ImGuiNET;` → `using Dalamud.Bindings.ImGui;` (Dalamud
  v13 renamed this binding assembly/namespace; the ImGui API surface itself is
  unchanged).
- `GlamourerIpc.cs`: switched from the concrete
  `Dalamud.Game.ClientState.Objects.Types.Character` class to the `ICharacter`
  interface, since `Character` is now `internal` and can't be referenced from
  plugin code. This assumes Glamourer's own IPC also takes `ICharacter` - if
  you get an `IpcTypeMismatchError` at runtime, check Glamourer's current
  `IPC.md`/`IpcSubscribers` for the exact expected type.
- `Plugin.cs`: `IClientState.LocalPlayer` was removed in Dalamud v15 in favor
  of `IObjectTable.LocalPlayer` - switched to that.
- **Emote detection was redesigned** (`EmoteHook.cs` → `EmoteWatcher.cs`).
  The original design hooked FFXIVClientStructs' `EmoteManager.ExecuteEmote`
  natively for frame-perfect, EmoteId-based detection. That failed to build
  twice: the old global `EmoteManager` singleton has been refactored into a
  per-Character `EmoteController` component, and the exact current member
  function for triggering an emote couldn't be reliably confirmed from public
  sources. Rather than keep guessing at internals that shift between game
  patches, detection now listens to `IChatGui.ChatMessage` for
  `XivChatType.StandardEmote`/`CustomEmote` lines instead - this only needs
  documented, stable Dalamud APIs and reliably compiles. See the trade-offs
  below.
- `EmoteWatcher.cs`'s `ChatMessage` handler is an inline lambda rather than a
  named method. `IChatGui.ChatMessage`'s parameter type in Dalamud v15 is
  `IHandleableChatMessage`, but its exact declaring namespace couldn't be
  confirmed from available docs/source snippets. A lambda assigned directly
  to the event has its parameter type inferred from the delegate, so it never
  needs to be named/imported - this sidesteps needing to get that namespace
  right at all.
- **Added `Flash.json`** - a plugin manifest with the `Name`/`Author`/
  `Description`/`Punchline` keys `DalamudPackager` requires at build time (it
  errors without one). I set `Author` to "Hayden" based on your Windows
  username - change it, and the punchline/description/tags, to whatever you
  actually want shown in the plugin installer.

## How emote detection works now (and its limitations)

FFXIV writes a line to chat whenever any character performs an emote (e.g.
"You wave."), as long as the client's **Character Configuration > Log Window
Settings > Log Emotes** option is on (it's on by default). `EmoteWatcher.cs`
listens for those lines and `Plugin.cs` matches them against your configured
emotes via a **case-insensitive substring check of the emote's name against
the chat text** - not an exact numeric ID match like a native hook would give
you. Practically this means:

- It depends on the chat line actually containing a recognizable form of the
  emote's name. This works for most emotes (e.g. "Wave" matches "You wave.")
  but may miss ones whose log text doesn't closely resemble the emote name.
- It's client-language-dependent (built against English chat text).
- Self-emotes are detected by an empty chat "sender" field, which is the
  normal behavior for your own actions but isn't a guaranteed API contract.

If you want exact, ID-based detection instead: your IDE has the real,
currently-installed FFXIVClientStructs assembly referenced. Right-click
`FFXIVClientStructs.FFXIV.Client.Game.Character.Character` → "Go to
Definition", find the `EmoteController` field, and inspect its member
functions there - that's a more reliable source of truth than anything
searchable from outside, since it's the exact version you're building
against. Once you have the real method name/signature, you can reintroduce a
`Hook<T>` on it (the original `EmoteHook.cs` approach) and raise the same
kind of event `Plugin.cs` currently gets from `EmoteWatcher`.

## Known limitations / things to sanity-check yourself

- `LocalPlayerOnly` defaults to true. If you want this to also trigger for
  party members or other nearby characters performing the emote, set it to
  false per-entry (the config model already supports it; wire up a checkbox
  in `PluginUi.cs` if you want it exposed in the UI).
- The revert timer is wall-clock based (`DateTime.UtcNow`), not tied to the
  actual emote animation length, since animation duration isn't trivially
  exposed. You may want to tune `RevertDelaySeconds` per emote to match how
  long that specific emote plays.
- `GlamourerApiEc` and `ApplyFlag` in `GlamourerIpc.cs` only include the
  members this plugin uses. Check Glamourer's `IPC.md` in its repo if you
  want to add more flags (e.g. also restoring customizations, not just
  equipment).

# Flash

Flash is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final
Fantasy XIV that works with [Glamourer](https://github.com/Ottermandias/Glamourer)
to automatically strip your character's gear when you play specific emotes
or use specific actions.

## What it does

Pick any emotes or job/combat actions you like, and Flash will strip your
character's gear (down to Smallclothes, or swap it to the Emperor's New Set
instead, your choice) the moment you play/use one of them. Your gear comes
back on its own afterward.

- **Emotes or Actions** - map either kind of trigger. Emote gear reverts
  automatically once the animation finishes; action-triggered gear always
  reverts after a set Duration, since actions don't have an animation to
  watch the end of.
- **Slots** - choose exactly which slots get changed (Head, Body, Hands,
  Legs, Feet, Earrings, Necklace, Bracelet, Left Ring, Right Ring) with
  individual checkboxes. Leave a slot unchecked and Flash won't touch it.
  Flash never sets a weapon item itself, but restoring your appearance uses
  Glamourer's own full-state apply, which as a side effect briefly redraws
  your character (weapon included) - this is a known Glamourer behavior we
  can't fully avoid without losing correctness elsewhere.
- **Delay** - if you don't want the change to happen the instant the trigger
  fires, set a delay (in seconds) and Flash will wait before changing
  anything.
- **Duration** - for emotes, gear comes back as soon as the animation ends
  by default; you can set a duration instead if you'd rather it end sooner.
  Action triggers always use a duration.

## Requirements

- [Glamourer](https://github.com/Ottermandias/Glamourer) installed and
  running - Flash uses it to actually change your gear.

## Installing

Add this custom plugin repository in Dalamud (`/xlsettings` -> Experimental
-> Custom Plugin Repositories):

```
https://raw.githubusercontent.com/mr-reeh/flash/main/repo.json
```

Then find and install **Flash** from `/xlplugins`.

## Using it

1. Run `/flash` in-game to open the config window.
2. Choose whether triggers strip you to **Smallclothes** or the **Emperor's
   Set**.
3. Check or uncheck the slots you want Flash to actually change.
4. Pick **Emote** or **Action**, search by name, and click **Add mapping**.
   If the search grabs the wrong one (short searches can match the wrong
   name), check the Flash Debug Log for the exact ID and type it into the
   **ID override** field instead.
5. Optionally set a **Delay** and/or **Duration** for that mapping.
6. That's it - play the emote or use the action in-game and watch it happen.

You can add as many mappings as you like, reorder them, and toggle each one
on or off individually from the config window.

## 🤖 AI Assistance & Attribution
This project is AI-assisted. 
* **Core Coding & Architecture:** Assisted by [Anthropic's Claude](https://claude.ai) 

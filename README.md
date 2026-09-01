# Flash

Flash is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final
Fantasy XIV that works with [Glamourer](https://github.com/Ottermandias/Glamourer)
to automatically strip your character's gear when you play specific emotes.

## What it does

Pick any emotes you like, and Flash will strip your character's gear (down
to Smallclothes, or swap it to the Emperor's New Set instead, your choice)
the moment you play one of them. Your gear comes back on its own once the
animation finishes.

- **Delay** - if you don't want the change to happen the instant the emote
  starts, set a delay (in seconds) and Flash will wait before changing
  anything.
- **Duration** - by default, gear comes back as soon as the emote animation
  ends. If you'd rather it end sooner than that, you can set a duration
  instead, and Flash will restore your gear after that many seconds,
  whichever comes first.

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
2. Choose whether emotes strip you to **Smallclothes** or the **Emperor's
   Set**.
3. Search for an emote by name and click **Add mapping**.
4. Optionally set a **Delay** and/or **Duration** for that mapping.
5. That's it - play the emote in-game and watch it happen.

You can add as many emotes as you like, reorder them, and toggle each one
on or off individually from the config window.

## 🤖 AI Assistance & Attribution
This project is AI-assisted. 
* **Core Coding & Architecture:** Assisted by [Anthropic's Claude](https://claude.ai) 
* **Human Oversight:** [Your Name/GitHub Handle] (Review, debugging, and deployment)


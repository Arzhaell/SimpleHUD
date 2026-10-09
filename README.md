# Simple HUD

🇫🇷 [Version française](README.fr.md)

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV: simple display tweaks, right from the
**HUD layout editor**. It moves the **flying text** shown on your character and sets every game **HUD element** to
the pixel.

Formerly "Flying Text Modifier" (up to version 1.1.0).

## Installation
1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, click **+**, tick the checkbox and save:
   ```
   https://raw.githubusercontent.com/Arzhaell/SimpleHUD/main/repo.json
   ```
3. Type `/xlplugins`, search for **Simple HUD** and install it.

## Usage
- Open the HUD layout editor: "Flying text" frames show up over the editor, along with the plugin window.
- **Texts on you** (fixed place on screen): the window offers four layouts.
  - **All grouped** (like the game): a single frame; healing on one side, status effects and damage taken on the
    other.
  - **Healing separate**: healing received · status effects and damage taken.
  - **Status effects separate**: status effects · healing and damage taken.
  - **All separate**: healing received · status effects · damage taken.

  The game only knows two blocks (healing; status effects and damage taken). To separate status effects, the plugin
  moves each status text one by one. When a text arrives, the game pushes down the others in its block to make room
  for it: the plugin cancels that push for texts in another frame, so each frame scrolls on its own.
- **Other texts in their own frame** (checkbox): EXP, looted items, MP recovered, crafting and gathering get their own
  frame, whatever the layout.
- **On the target**: your hits on the target. These texts always follow the target; you set their offset from it
  (the circle marks the target, or your character when there is none).
- Drag a frame (hold Shift for fine moves): the yellow dot marks where the game places the texts. When you let go,
  test texts scroll at the new place.
- To the pixel: the "Flying text" tab of the plugin window gives X and Y for each frame, with − / + buttons
  (Ctrl+click: by 10).
- Right-click a frame, or the ↺ button: back to the game's original position.
- **Display and size**: for buffs / debuffs, healing, damage taken (on you), damage dealt (on your targets) and other
  texts (EXP, MP…), the window lets you hide them or set their size, from 50% to 200% of the game's size. The
  critical hit bounce is kept.
- **Test all texts** scrolls every kind of text over your character: healing, critical healing, beneficial and
  detrimental status effects gained then lost (real game status effects, picked at random on each try), damage,
  critical hits, direct hits, auto-attacks, misses, EXP, MP…
- `/simplehud` opens the window outside the HUD layout editor; the frames show while it is open.

### Game HUD to the pixel
- In the HUD layout editor, hover an element (hotbars, party list, target, compass…, including those hidden in game):
  the position of its top-left corner on screen, in pixels, shows just above its frame. The "Game HUD" tab of the
  plugin window can show all of them at once.
- **To the pixel**: under the selected element, a panel gives X and Y (− / + buttons, Ctrl+click: by 10). It moves
  as if you dragged it: the editor's "Save" button keeps the new position, and closing without saving cancels it.
- The Debug build also writes what the HUD layout editor does to the Dalamud log (`[hud]` lines).

Positions are saved as a proportion of the screen: they follow a resolution change.
When the plugin is disabled, the texts go back to their original place.

The grouping of texts is set by the game. To offset the texts on the target, the plugin moves the whole flying text
layer and makes up for it in the position of the two groups on your character.

The plugin follows the Dalamud language (English or French); the window lets you force it.

If [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) is loaded too and also moves these texts, the positions
chosen in Simple HUD take over.

## Disclaimer
Like any third-party tool, using Dalamud and its plugins is against the FFXIV Terms of Service. Use it at your own
risk.

## Building and testing
- `dotnet test SimpleHUD.Tests -c Release`: tests of the logic (memory lookups, conversions, settings).
- `dotnet build SimpleHUD -c Release`: to try it, add `SimpleHUD\bin\Release\SimpleHUD.dll` to the dev plugin
  locations (`/xlsettings`, **Experimental** tab); Dalamud reloads it by itself, even with the game running. It does
  not load the version installed from the repository at the same time: disable that one.
- The Debug build (`dotnet build SimpleHUD`) also writes the state of the groups and of each text to the Dalamud log
  (`[diag]` lines), for troubleshooting. It writes a lot: only load it to investigate.

## Releasing a new version
Pushing an annotated `vX.Y.Z` tag is all it takes: the GitHub workflow runs the tests, builds the plugin, creates
the release (its notes are the tag message) and updates `repo.json`.

```bash
git tag -a v1.2.0 --cleanup=verbatim -F notes.txt
git push origin v1.2.0
```

## Credits
- [Dalamud](https://github.com/goatcorp/Dalamud) and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs):
  plugin framework and game structures.
- [FlyTextFilter](https://github.com/Aireil/FlyTextFilter) (Aireil): this plugin found where the game stores the
  position of the flying text groups. None of its code is reused.
- [xUnit](https://xunit.net/) for the tests.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This plugin is neither affiliated with nor endorsed by Square Enix.

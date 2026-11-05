# UX pass 2: decisions for you

Seven choices for epic 10. Each one has a recommendation and the alternatives. Answer each by its letter, or say "all as recommended". The pictures are in [renders/](renders/README.md), in both themes, and the full design is in [EXPERIENCE.md](EXPERIENCE.md) and the [DESIGN.md](DESIGN.md) addendum. Nothing is built until you answer, and your answers go into the epic's notes.

## 1. Page names and order

**Recommended (A):** Library, then under Settings: General, Clips, Hotkeys, Audio Tracks, Games, Sounds, Storage and Cleanup.

| Was | Becomes | Why |
|---|---|---|
| Replay Buffer | **Clips** | It controls how long a Clip is, where recent gameplay is kept, and video quality. "Clips" is the word you'd look for; "Replay Buffer" is the engine's. |
| Track Layout | **Audio Tracks** | It's the list of audio tracks every Clip gets. |
| Game Profiles | **Games** | It's your games, each with its own page. |
| Feedback Sounds | **Sounds** | It's shorter, and the page explains itself. |
| Library and Cleanup | **Storage and Cleanup** | It's where files go and how they're cleaned up. "Library" is already the list of Clips. |

The order runs from what you change most to what you change least, after General.

**Alternatives:**

- **(B)** "Instant replay" instead of "Clips". It's familiar from NVIDIA, but it's their name.
- **(C)** Keep "Replay Buffer" (it's the app's name) and rename only the others.
- **(D)** Keep "Feedback Sounds" as it is.

## 2. Remove the Recordings page

**Recommended (A):** remove it.

- The page held one control, the screen picker for Monitor Recording, plus a note pointing to two other pages. The picker moves onto Hotkeys, beside the Monitor Recording key ([hotkeys-*.png](renders/hotkeys-fluent.png)).
- The per-game "allow Recordings" switch is on each game's page ([game-page-*.png](renders/game-page-fluent.png)).
- Hotkeys explains each kind in one sentence: a Clip saves the last 30 s with one key; a Recording is one long video you start and stop; a Monitor Recording records a whole screen.

**Alternatives:**

- **(B)** Keep a "Recordings" page that holds the Record mode, the screen picker and the explanations, with the keys staying on Hotkeys. It's one more page, and the keys sit apart from their explanation.
- **(C)** Fold it into Clips. Recordings aren't Clips, so that would blur the two again.

## 3. The plain-word labels

**Recommended (A):** use the glossary in [EXPERIENCE.md](EXPERIENCE.md#the-plain-language-rule-and-glossary) everywhere you read text: labels, help, the status line, warnings, the tray and the Overlay. Only the words change; settings files, codes and the engine stay the same.

| Engine word | You'd see | Alternatives |
|---|---|---|
| Ring folder | **Cache folder** | "Temporary clip storage", "Replay cache" |
| Buffer Window | **Clip length** ("Save the last 30 seconds") | "Replay length", "How far back to save" |
| Save cooldown | **Time between saves** | "Minimum gap between saves" |
| Replay Buffer: Memory or Disk | **Where recent gameplay is kept: Memory or Disk** | "Keep the replay in" |
| Output Folder | **Clips folder** | "Save folder", "Videos folder" |
| Output Settings | **Video quality** | "Video settings" |
| Target Game | **Capturing now** (a tag) / "the game being captured" | "Active game" |
| Global setting / override | **Uses the main setting / Changed for this game** | "Default / Custom" |
| Ignored | **Capture this game** (off) / "Not captured" | keep "Ignored" |
| Buffering / Draining / Replay Buffer off | **Ready to save Clips in {game} / Finishing the save… / Waiting for a game** | "Recording the last 30 s of {game}" |
| engine | **ReplayBuffer's background service** | "the capture service" |
| Feedback Sounds | **Sounds** | — |

**Alternative:** **(B)** name any line to change, and give the label you'd use.

## 4. Favourites can't be deleted, even one at a time

**Recommended (A):** yes. Delete leaves Favourites out, whether one or many are selected, and says so in the confirmation ("2 Favourites are kept"). To delete a Favourite, unstar it first.

- With multi-select, one Shift-click can sweep in Favourites without your noticing. The star is your "keep this" mark, and Cleanup already respects it.
- This changes today's behaviour: one Favourite can currently be deleted after the confirmation.

**Alternatives:**

- **(B)** Bulk delete leaves Favourites out, but deleting a single Favourite is still allowed after a warning ("This is a Favourite. Move it to the Recycle Bin?").
- **(C)** Delete everything selected, with the confirmation saying how many are Favourites.

## 5. "Save Clip now" in the tray menu

**Recommended (A):** not now.

- It needs a new engine command, and it's a second way to save, where the plan had one Save key.
- Clicking the tray means leaving the game, so the Save key is the better tool.
- The tray gets a status line, "Open Library" and "Open the Clips folder" instead ([surfaces-*.png](renders/surfaces-fluent.png)).

**Alternative:** **(B)** add it. It saves exactly as the Save key does, Overlay and sound included, and it's added in story 10.6.

## 6. The Audio Tracks presets

**Recommended (A):** two presets, offered from "Use a preset" on Audio Tracks ([audio-tracks-*.png](renders/audio-tracks-fluent.png)):

- **Simple:** one track, "Everything", with your PC's sound and your microphone. A fresh install starts with this, so Clips have sound without any setup.
- **For editing:** three tracks: Everything (PC sound and microphone), Game and chat (PC sound), and Microphone (microphone alone).

Each preset uses your Windows default playback and recording devices, and it asks before replacing your tracks. Your current BEACN layout isn't touched unless you pick a preset.

**Alternatives:**

- **(B)** Add a third preset: "One track per playback device", for mixers like BEACN that split game, chat and music into separate outputs.
- **(C)** Rename them, for example "Everything on one track" and "Separate voice for editing".
- **(D)** No first-run audio: a fresh install starts silent, as today, and the welcome card says to set up audio.

## 7. Anything the renders got wrong

**Recommended (A):** approve the renders as they stand. Two adjustments were made while rendering, and both are in the DESIGN.md addendum:

- Under Gaming dark the Library's action bar doesn't fit at the default window width, so Copy to folder… and Show in Explorer move into a "More ⋯" menu.
- A folder picker's buttons sit on a row under the path.

Under Fluent with a grey Windows accent, like this PC's, selected Library rows are only faintly highlighted. The addendum switches to a stronger grey fill in that case, and story 10.2 checks it on your PC.

**Alternative:** **(B)** name any screen to change.

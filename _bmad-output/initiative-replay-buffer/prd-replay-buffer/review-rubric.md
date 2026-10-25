# PRD Quality Review — Replay Buffer

## Overall verdict
For a solo hobby PRD this is a good one, and lean in the right way. The Vision is specific, §2.2 "What makes me abandon it" drives the NFRs and Success Metrics, the Glossary is real, and most FRs have measurable consequences (±20% estimate, ≤1 s loss on crash, <1 frame drift over 60 min). The biggest risk is outside the PRD's own text. §0 and the addendum send architecture to `PLAN.md` (rev 2) as "the detailed record", but PLAN.md still encodes product decisions this PRD reverses: post-roll, several save hotkeys, single-device Tracks with gain, monitor capture by default, an allow-list, and one file per ring generation. Nothing marks which source wins. A §2.1 job that contradicts FR-8, plus a handful of undefined behaviours (output folder, dismissing the red X, Everything-Track double counting), make up the rest of the work.

## Decision-readiness — adequate
Most decisions are stated as decisions, and several go against the obvious ShadowPlay or OBS default: "the app has no per-Track volume or gain" (FR-18), devices only and no per-app audio (§4.3 Out of scope, §6), "exactly one" Save Hotkey (§3 Hotkey), Reset with no overlap (FR-5), and "Any fullscreen or borderless app counts" as a game, with Ignored as the escape hatch (FR-26/FR-28). The Open Questions are actually open. Q-4 is the sharpest of them: it names a real conflict between the ShadowPlay-style Overlay and the no-injection rule in NFR-3.

What weakens this dimension is where those tensions fail to reach the FRs they affect. FR-6 promises things unconditionally that Q-4 says may be impossible. One user job contradicts an FR outright. And NFR-1 is called open even though the input document already holds a provisional number.

### Findings
- **[high]** §2.1 job contradicts FR-8 on automatic Recordings (§2.1 "Record a whole session" vs FR-8) — The job says "or have it happen automatically for the games I choose", but FR-8 says switching Recordings on "doesn't start a Recording automatically when the game launches". One of them is wrong, and a story writer can't tell which. If auto-record is wanted, there is no FR for it. *Fix:* Decide. Either add an FR such as "Auto-record on game launch, per Game Profile, off by default", or remove "automatically" from the job.
- **[medium]** The exclusive-fullscreen Overlay tension is missing from FR-6 (FR-6, Q-4) — FR-6 requires "The dot appears within 1 s of capture starting" and "The green saving icon appears within 250 ms", with no exception. Q-4 says an always-on-top window "may not show over games running in exclusive fullscreen". That is the PRD's main save feedback, and the only feedback for FR-7 failures and the FR-14 red X. *Fix:* Add a `[NOTE FOR PM]` at FR-6 that points to Q-4, and scope the FR-6 consequences to borderless/windowed until Q-4 is settled. Or name the fallback now, for example a sound cue on save and on failure.
- **[medium]** NFR-1 is called open when PLAN.md already has a target (NFR-1, Q-1, §8 SM-3) — NFR-1 says "the number is still open". PLAN.md §6.3, which the NFR itself cites, lists "Game FPS loss compared with no capture: < 3%". FPS loss is the first abandonment reason in §2.2, but SM-3 can't be measured as written. *Fix:* Adopt "< 3% average FPS loss at 1080p60 under GPU-bound load (provisional, PLAN.md §6.3)" and leave Q-1 to confirm or revise it after the benchmark.

## Substance over theater — strong
There is no theater to flag. The only persona is the builder, and the JTBD plus the abandonment list do real work: each item in §2.2 maps to an NFR or SM. The Vision could not be swapped into another PRD in this category, because "the audio tracks you define, each built from any combination of your output and input devices" is the specific thesis, and the second paragraph explains why ShadowPlay and OBS each fall short. The NFRs are product-specific: no injection, no low-level keyboard hooks, separate processes, named anti-cheat families, a drift bound. The addendum's device survey is genuine discovery (shared names, "Not present" entries), and it shows up in FR-15's consequences.

## Strategic coherence — adequate
The thesis is clear and the features serve it. Multi-track capture (§4.3) and reliability (NFR-2, NFR-4, FR-12) carry the most consequences, and the counter-metrics (SM-C1, SM-C2) protect integrity over a few frames or a smaller file. That is the right bet for this thesis.

Scope logic is weaker. §7.1 puts "Every feature in §4" into the MVP, and §7.2 says "Nothing in §4 is deferred". So the Library with thumbnails, Cleanup, Monitor Recording, crash-safe 4-hour Recordings and per-game Output overrides all ship alongside the core replay-plus-multi-track loop. For a solo build, that is a non-decision on sequencing, and it leaves Sofia to invent the epic order. The §8 headline "so it fully replaces ShadowPlay" also has no metric of its own.

### Findings
- **[medium]** The MVP is "everything", so there is no first usable slice (§7.1, §7.2) — With no priority, the thesis-critical loop (FR-1–FR-7, FR-15–FR-18, FR-22, FR-24, FR-37) is weighted the same as FR-30's thumbnails or FR-32's Cleanup. PLAN.md §5 has milestones, but they predate this scope (no Recordings, no Library). *Fix:* Keep everything in v1 if that's the intent, but add an order to §7.1, for example: (1) Replay Buffer + Clips + Multi-Track Audio + game-window capture; (2) Game Profiles; (3) Recordings; (4) Library, Cleanup and Monitor Recording.
- **[low]** "Fully replaces ShadowPlay" has no measure (§8 headline) — SM-1 to SM-4 cover reliability, performance and anti-cheat, but nothing shows the replacement happened. *Fix:* Add an SM such as "ShadowPlay Instant Replay switched off for 4 consecutive weeks of normal play".

## Done-ness clarity — adequate
This is better than most hobby PRDs. Nearly every FR has a concrete test: "Two Clips saved 10 s apart: the second Clip is 10 s long" (FR-5), "Unplugging a USB mic for 10 s mid-Clip" (FR-21), "within ±20%" (FR-3), "at most 1 s" (FR-12), a "4-hour Recording is one playable file" (FR-13). The gaps are behaviours the PRD names but never defines, plus a few adjectives where bounds belong.

### Findings
- **[medium]** There's no way to dismiss the red X (FR-14) — "The red X stays until I dismiss it or a Recording starts again". The Overlay is a notification area over a game that has focus, and there is no Hotkey or Main App control for dismissing it. *Fix:* Say how dismissal works (a Main App or tray action, a timeout, or the Record Hotkey), or drop "until I dismiss it".
- **[medium]** The output folder and Clip file names are never defined (FR-29, FR-32, §3) — FR-29 uses `<output folder>\Valorant\` and Cleanup works on "the output folder", but no FR lets me choose it and the Glossary has no entry. Clip and Recording file names are also unspecified (PLAN.md §4 had a naming template), and FR-31 rename and the FR-30 Library both depend on them. *Fix:* Add an FR covering the output folder setting (default location, changeable, applies to new files) and the default file name pattern, or add an `[ASSUMPTION]` for each.
- **[low]** Adjectives where bounds belong (FR-5, FR-10, NFR-3, NFR-5, FR-35) — "as soon as possible after the press" (FR-5; already implied by "no frames are missing", so it can go), "no gap or glitch" (FR-10), "kept to a minimum" (NFR-3, though its bullet is testable), "with no memory growth" (NFR-5, which can't be tested literally), and FR-35 has no Consequences block at all. *Fix:* Give NFR-5 a bound, for example "working set within X MB of its value after 10 min, over 8 h". Give FR-35 one consequence each for open and quit.
- **[low]** Cleanup's delete mode is unspecified (FR-32 vs FR-31) — FR-31 sends deletes to the Recycle Bin, but if Cleanup also did, the folder would still be over its size limit. *Fix:* State that Cleanup deletes permanently, or make it configurable as PLAN.md §4 did.
- **[low]** Behaviour when the game window is minimized or alt-tabbed is unspecified (FR-22) — "Other windows... never appear" is covered, but whether the Clip shows a frozen frame, black frames or a gap is not, and alt-tab mid-session is common. *Fix:* Add a consequence, for example "While the game is minimized, the Clip shows the last frame (or black) and every Track keeps recording."

## Scope honesty — adequate
The Non-Goals (§6) do real work: hybrid GPUs and distribution are exactly the omissions someone could otherwise assume. Each feature's "Out of scope" line is useful, for example "Post-roll; more than one Save Hotkey". The 10 inline `[ASSUMPTION]` tags are all indexed in §10. Open-items density (4 Open Questions, 10 assumptions, 1 `[NOTE FOR PM]`) is fine for these stakes. The gaps are omissions made silently compared with the input plan.

### Findings
- **[medium]** Everything Track double counting was dropped without saying so (§3 Track, FR-16) — The "Everything" Track is "every selected device mixed". On the surveyed setup, a mixer bus and the physical output it feeds (BEACN "Headphones", the Schiit Modi 3E) are both Output Devices, so selecting both doubles the audio. PLAN.md §2 had an "Overlap warning" heuristic for exactly this, and the PRD neither keeps it nor rules it out. *Fix:* Add a consequence (warn, or leave unhandled as a stated non-goal), and define "selected device" for a new Track Layout's Everything Track.
- **[low]** Monitor Recording edge cases are folded into one assumption (FR-23) — The assumption covers Track Layout, Output Settings, crash safety and folder all at once. It leaves open whether a Monitor Recording can run alongside a game Recording, and what the Overlay shows when no game is running. *Fix:* Split the assumption, and add a consequence on running at the same time as a game Recording.

## Downstream usability — thin
The Glossary is strong and used consistently, the FR, NFR and SM IDs are unique, and the Open Question cross-references resolve. What makes this dimension thin is that the PRD's own routing sends downstream work to a source that contradicts it. §0 says technical design "stay[s]" in PLAN.md, and the addendum points `bmad-architecture` at PLAN.md §0–§3 as inputs. Those sections still encode reversed product decisions, and no supersession rule exists.

### Findings
- **[high]** PLAN.md conflicts with the PRD, and nothing says which one wins (§0, addendum "Inputs for architecture") — Here is what PLAN.md rev 2 says against the PRD:
  - §1 saves "[now − SaveSeconds, now + PostRoll]", but PostRoll is out of scope per §4.1.
  - §0 #4 and §4 have "Several save hotkeys", but the PRD has exactly one (§3).
  - §2 defines `Track = { Name, Device, Gain, ... }` with one device and gain, but PRD Tracks combine any devices with no gain (FR-16, FR-18).
  - §2 has mute silence "the mic in both the stem and the master", but the PRD mutes only the Tracks you mark (FR-20).
  - §3 says "A save that spans generations writes one file per generation", which conflicts with FR-13 and FR-22.
  - The config captures `"Monitor"` and uses an allow-list, but the PRD captures the game window (FR-22) and auto-detects games (FR-26).
  - §2 says "the UI shows a warning" when a device goes missing, but FR-21 says "The Overlay shows nothing" (consistent only if the Main App warns, and that isn't stated).

  An architect reading the addendum's pointers would build the wrong ring and Track model. *Fix:* Add one line to §0 and the addendum: "Where PLAN.md and this PRD disagree on behaviour, this PRD wins; PLAN.md is authoritative only for mechanism." Then list the known conflicts in the addendum, or update PLAN.md to rev 3.
- **[medium]** FR-36 and FR-37 fall outside the stated FR range and order (§0, §7.1) — §0 says "FR-1 to FR-35" and §7.1 puts "FR-1 to FR-35" in scope. FR-36 (Memory or Disk) sits between FR-2 and FR-3, and FR-37 (Configurable Hotkeys) between FR-34 and FR-35. Read literally, two required FRs are outside the MVP, and a range-based extraction would miss them. *Fix:* Change both statements to "FR-1 to FR-37", or renumber in document order.
- **[low]** The Glossary doesn't cover everything the FRs use (§3 Hotkey, Overlay, Recording) — The Hotkey kinds don't include the Monitor Recording hotkey (FR-23). Overlay lists only the red dots and messages, not the green saving state (FR-6), the mic-muted icon (FR-20) or the disk-full red X (FR-14). Recording says Recordings are "possible only for games whose Game Profile has Recordings switched on", which contradicts Monitor Recording, itself "a Recording" that works with no game. *Fix:* Extend the three entries. For Recording, add "except Monitor Recordings".

## Shape fit — strong
The shape matches the product. It's a single-operator capability spec with first-person FRs, short JTBD instead of User Journeys, and no persona apparatus. Technical depth (WASAPI, WGC, fMP4, slab ring) is kept out of the PRD and pointed to from the addendum, as it should be at these stakes. User Journeys would be overhead here, and the PRD rightly has none. The one formality that earns its keep is the Glossary, because downstream agents depend on it.

## Mechanical notes
- **Assumptions Index roundtrip:** clean. All 10 inline `[ASSUMPTION]` tags (FR-23, FR-26 ×2, FR-27, FR-29, FR-30 ×2, FR-31, FR-32, NFR-3) appear in §10, and every §10 entry appears inline.
- **ID continuity:** FR-1 to FR-37 are unique with no gaps, but they aren't in document order (FR-36 comes after FR-2, FR-37 after FR-34), and §0 and §7.1 still say FR-1 to FR-35 (see the finding above). NFR-1–5, SM-1–4, SM-C1–C2 and Q-1–4 are contiguous.
- **Term collision:** "Replay Buffer" is both the product name (title, §1 "Replay Buffer is an always-on instant replay") and the Glossary term for the rolling store. Downstream text like "the Replay Buffer crashes" becomes ambiguous. The title is already flagged "*Working title — confirm.*", so renaming the product would also fix this.
- **Minor drift:**
  - FR-3 says "output settings" in lowercase; the Glossary term is "Output Settings".
  - §4.2 says "separate Start and Stop keys"; the Glossary says "Start Recording and Stop Recording keys".
  - §2.1 says "open any of them in the media player I choose", but the Glossary's Media Player is fixed to "the Windows default app for the file type".
- **Counter-metric target:** SM-C2 "Counterbalances FR-24" pairs a counter-metric with an FR rather than an SM. That's harmless, but every other counter-metric pairs with an SM.
- **Missing block:** FR-35 is the only FR without a Consequences block.
- **Required sections:** all present for hobby stakes.

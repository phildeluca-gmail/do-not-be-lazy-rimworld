<!-- Capture file for RimWorld mod ideas that are not yet architected. Created 2026-08-23. Not required session reading - see CLAUDE.md. -->

# RimWorld Wishlist

Ideas parked here rather than in an architecture document, because
nothing below has been designed, costed or agreed. **Nothing in this
file is a go-ahead.** An entry graduates by being written up in the
relevant architecture doc first.

Each entry records what was asked for verbatim, then what that appears
to mean against the code as it stands. Where the second part is a guess,
it says so - do not build from a guess.

---

## 1. ConfigureKeys - interface changes

**Asked for, verbatim:** "ConfigureKeys - interface changes".

**Status: captured, not understood. Needs the requester to expand it
before anything else happens.** Recorded now so it isn't lost.

There is no `ConfigureKeys` anywhere in this repo or in either
architecture document - grepped 2026-08-23 across `.md`, `.cs` and
`.xml`. So this is new, and the name is doing all the work.

Two readings, and they lead to completely different pieces of work:

1. **Key bindings for Do Not Be Lazy.** Every `*` sweep is a
   right-click float menu entry today; there is no keyboard path to any
   of them. RimWorld's own mechanism is a `KeyBindingDef` in XML plus a
   `KeyBindingDefOf` lookup, surfaced under Options > Key Bindings for
   free. A hotkey that repeats the last sweep, or one per common sweep
   type, would fit that. This is the reading the mod's shape suggests.
2. **A separate mod about configuring keys**, i.e. the interface for key
   binding itself rather than any of our features. That would be a third
   project alongside Do Not Be Lazy and Do Not Freak Out.

"interface changes" is equally open - it could mean the key-binding UI,
the mod settings window (`DoNotBeLazySettings.DoWindowContents` is a
bare `Listing_Standard` today), the float menu itself, or an overlay.

**Related things already known, so they don't get re-derived:**

- `showSweepOverlay` is a settings checkbox that **does nothing** -
  confirmed by grep, recorded in architecture doc 3.4. If "interface
  changes" includes drawing the sweep radius, that dead checkbox is
  where it starts, and it already implies a promise to the player that
  the mod doesn't keep.
- The float menu path is the most-touched interface surface in the mod
  and the least traced - see the "float-menu path is still mostly
  untraced" note in `NEXT_SESSION.md`.
- Open items 4 and 5 in `NEXT_SESSION.md` are both float-menu interface
  bugs (drafted right-click no longer moves pawns; nonsense entries on a
  workbench). Whatever "interface changes" turns out to mean, those two
  are probably ahead of it in the queue.

**Questions to settle before this becomes a plan:**

- Is this Do Not Be Lazy, or a new mod?
- Keyboard shortcuts, on-screen UI, or both?
- If shortcuts: what does a key press act on - the pawn's current
  selection and the cell under the cursor, the way a right-click does?

---

## 2. Do Not Be Lazy - focus from centre out - **GRADUATED 2026-08-27**

**Asked for, verbatim:** "DoNotBeLazy focus from center out".

**Status: graduated and implemented.** Ordered on 2026-08-27 as "the
work should emanate from my mouse click", written up as TOP PRIORITY
item A in `DoNotBeLazy_Architecture.md` section 0, and implemented the
same day in `SweepManager.NextTargetIndex`. It is no longer wishlist and
this entry is kept only so the reasoning below isn't re-derived.

**How the open design question was settled.** This entry said the
weighting between distance-from-centre and distance-from-pawn was the
real design work, and that "strict centre-out is easy and probably
wrong". It was built strict anyway, on the user's explicit instruction:
the click is an order, and a blend makes the result less predictable
rather than more. Distance from `ScanCenter` ranks; distance from the
pawn only breaks ties.

**The objection this entry raised was answered on 2026-08-29 by making
it a setting.** Strict centre-out can send a pawn past a target beside
them to reach one nearer the click; that is now the default half of a
two-way radio group (`centerOutOrder`, architecture doc 3.4), with the
old nearest-to-pawn rule as the other half. The mode is stamped onto
the `SweepOrder` at click time, so it does not change under a running
sweep. **The "do not soften without asking" note that used to sit here
is retired - the softening was asked for.**

**The blend is still not built and still needs a fresh order.** The
mitigation this entry proposed - let a pawn take anything within a few
tiles of itself first, otherwise centre-out - was explicitly excluded
from the 08-29 change rather than forgotten. It would be a third radio
entry. Only worth doing if the radius-50 case is actually played and
actually annoys.

**Still unbuilt, still paired with this:** `showSweepOverlay` remains a
settings checkbox that draws nothing. Now that the sweep genuinely
radiates from the click, drawing the radius would be showing something
real.

---

## 3. Five new mods, ordered 2026-08-27 - **BUILT AND SPLIT 2026-08-29**

**Asked for, verbatim:** "New mods coming, just build separate folders
for each: Highlight Corpses With Tech; Notify Ripe (options: Ambrosia,
Berries); Notify Still Being Attacked; Uninstall Hotkey; Menu Hotkeys."

**Status: folders built 2026-08-29. Behaviour still not ordered.** The
instruction was a go-ahead for *folders*, and explicitly not for
behaviour - "just build separate folders" was the whole ask. **Do not
implement any of the five without a fresh order.**

**Where the detail now lives.** Each mod has its own
`<Name>_Architecture.md` carrying the verbatim ask, the
guess-from-the-name, and the questions that must be settled before it
becomes a plan - in this repo for the four still parked here, and in its
own repo for Highlight Corpses With Tech. **Those stubs supersede the
summaries below** - the summaries are kept because this entry is the
historical record.

**Highlight Corpses With Tech is no longer a guess.** It was fully
designed on 2026-08-29 and its stub is now a real architecture document
in `RimWorld-HighlightCorpsesWithTech/`. Notably, reading the installed
Reclaim/Reuse/Recycle mod proved rot is irrelevant to it. Still no
behaviour implemented.

**Both scope questions were answered 2026-08-29:**

1. **How deep does each folder go?** Answered: **scaffold plus
   architecture stub** - `About/About.xml`,
   `Source/<Name>/<Name>.csproj` pointing at `../../../lib`, a
   `Core/<Name>Mod.cs` that loads and logs one line, plus a
   `<Name>_Architecture.md` holding the verbatim ask. All five build
   clean and produce a DLL - verified, though the first build of a
   scaffold needs `/t:Restore,Rebuild` because no assets file exists yet.
   (The options rejected were: bare directories; scaffold with no stub;
   stubs with no code.)
2. **Where do they live?** Answered twice on 2026-08-29, and the second
   answer is the live one. **First:** top-level in this repo. **Then
   reversed the same day - one standalone repo per mod**, each depending
   on nothing but Harmony and vanilla RimWorld. See the table in
   `CLAUDE.md`. `.gitignore` was broadened from
   `DoNotBeLazy/Assemblies/*.dll` to `*/Assemblies/*.dll` on the way
   through, which is still right either way.

   **Highlight Corpses With Tech has already moved out** to
   `RimWorld-HighlightCorpsesWithTech/` and
   `github.com/phildeluca-gmail/highlight-corpses-with-tech-rimworld`.
   The other four are still here **only because their repos do not exist
   yet** - split each one the moment its repo is created. Naming: repo
   `<kebab-name>-rimworld`, folder `RimWorld-<PascalName>`.

   Note `DoNotFreakOut_Architecture.md` still sits in this repo for a
   sixth mod that was never scaffolded. Under the one-repo-per-mod rule
   it should move out too, whenever it becomes real.

**What each name appears to mean.** All five are guesses from the name
alone - **do not build from these.** Recorded so the reading isn't
re-derived from scratch:

- **Highlight Corpses With Tech.** Probably an overlay or marker on
  corpses that carry researchable/strippable technology, so they are not
  missed before they rot. Unclear whether "with tech" qualifies the
  corpse (mechanoid, high-tech raider gear) or the highlight (a
  tech-styled overlay). Ask.
- **Notify Ripe (options: Ambrosia, Berries).** A letter or alert when a
  wild ambrosia bush or berry bush reaches harvestable growth. The
  parenthetical is explicitly a settings list, so this one arrives with
  two toggles already specified - the first of the five with any stated
  interface.
- **Notify Still Being Attacked.** Probably a recurring alert while a
  colonist or colony is under ongoing attack, covering the case where
  the initial letter is missed or dismissed and the fight continues.
- **Uninstall Hotkey.** A `KeyBindingDef` that fires the Uninstall
  designator on the selection or the thing under the cursor, instead of
  going through the architect menu.
- **Menu Hotkeys.** Keyboard access to menu entries generally. **This
  overlaps entry 1 above** (`ConfigureKeys - interface changes`) and may
  be the same idea restated, or its second reading - "a separate mod
  about configuring keys". Settle entry 1 and this one together.

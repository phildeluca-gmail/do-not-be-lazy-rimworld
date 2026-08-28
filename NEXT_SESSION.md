<!-- Pickup context for a fresh session. Session closed 2026-08-27 evening, on short notice (machine shutting down): T7.1 and T7.2 PASSED in a real game, the installed DLL was confirmed already current, and five new mods were ordered as folders-only but not built. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

**This session is closed, 2026-08-27 evening.** It was cut short - the
user's machine was shutting down - so this file was written fast. What
it says is accurate; it is just thinner than usual.

**The headline: the drafted-pawn bug is fixed and proven.** T7.1 and
T7.2 both passed in a real game on the 08-27 build. That is the first
in-game confirmation of either ordered change. **T7.3 through T7.6 were
not run** - T7.3 is the undrafted half of the same report and T7.5 is
the centre-out change, so the centre-out work is still unproven.

**Also settled:** the installed DLL was already current (see "The DLL
was already copied" below), and `jobDiagnostics` is now OFF.

**Left open:** five new mods were ordered as folders, and **no folder
was built** - two scope questions went unanswered when the session
ended. Full detail in `RW-Wishlist.md` entry 3; summary under "Start
here next session" below. Resume this session with:

```
claude --resume c0e00c19-e139-4554-9d68-d05e7127b90f
```

Earlier sessions, for reference only:

```
OLD: claude --resume 92786a93-35e8-4bdb-b076-3e794ec4a752   (centre-out sweeps + right-click move built; 08-22 pool fixes confirmed)
OLD: claude --resume 7254dd70-3fd5-4c55-a0f6-fcab3652315a   (standing-still answered, pool-discard + rescan fixed)
OLD: claude --resume 6ed5db2f-8d09-4435-ab6c-2fb321b5823c   (menu findings 1+2, job diagnostics, /pull-logs)
OLD: claude --resume 4b475e1d-24b5-48e9-94e4-f6ce4865faa9   (vehicle-packing diagnosis, TEST_PLAN conversion)
OLD: claude --resume 5e862c7c-fa2b-40a6-b221-e0174424c01f   (08-18 review: six findings, radius, standing-still)
OLD: claude --resume 9fe56f23-dbd5-4515-818c-6170fe4921d1   (fire sweeps / need-pause fix / menu feedback)
OLD: claude --resume 88fc941c-80ed-4d29-b235-7b39abac91ce   (consume/log triage)
OLD: claude --resume cc6c6703-86ad-4821-85ea-64813ca0b8ec   (sow fix)
OLD: claude --resume 18d354df-c62b-4ef8-805c-7cbd58244e51
OLD: claude --resume bd100a68-9153-4629-abf2-f0045dc3b922
```

Read `CLAUDE.md` first (project instructions), then this file, then
`DoNotBeLazy_Architecture.md` section 0 for the detailed current-state
log. This file is the fast-orientation summary.

## How the user wants to be talked to

**Shorter than you think.** Stated again on 2026-08-20 and this is the
second tightening - go **lower verbosity than previous sessions**, not
the same. Answer the question and stop. No restating what was asked, no
preamble, no summary of what you just did when the diff already says it,
no bulleted recap of a two-line result. Depth only when asked for.

This governs **chat replies only** - the docs in this repo stay dense on
purpose, that's their job. Do not "helpfully" trim the docs to match.

Also still true: don't paste whole logs (see the extraction workflow
below), and only commit when explicitly asked.

## What this is

A RimWorld 1.5 Harmony mod. Right-click a target with pawns selected,
get `*` sweep options (haul/build/mine/clean/sow/harvest/cut/workstation
bills/fight fires) that send eligible pawns to do that task repeatedly
across a radius until done. Also has a `* Consume` option for
eating/drugs (separate system, not WorkGiver-based) and pause/resume on
critical needs (hunger/rest/joy/mood).

## State right now - READ THIS FIRST

- Builds clean: `cd DoNotBeLazy/Source/DoNotBeLazy && dotnet build`
  (0 errors, 0 warnings).
- **Everything was committed and pushed at the close of the session,
  2026-08-23.** Two commits: `420588d` (the sweep-pool fixes and the
  logging work) and the close-up commit carrying `RW-Wishlist.md`, open
  item 6 and these doc updates. Run `git log -1` and `git status`
  anyway rather than trusting this line: it has been stale twice and
  cost time both times.
- **New file: `RW-Wishlist.md`** - capture for ideas that have not been
  architected. Two entries so far (ConfigureKeys / interface changes;
  Do Not Be Lazy focus from centre out). **Nothing in it is a
  go-ahead**; an entry graduates by being written up in an architecture
  doc first. Registered in `CLAUDE.md` as non-required reading.
- **2026-08-22 evening: the standing-still report is ANSWERED, from a
  real log, and the cause was ours.** See "Open item 2" below - it is
  now a closed item kept for the record. The two fixes for it are
  written and building; **neither has run in a game.**
- **The installed DLL is CURRENT - do not re-copy on faith.** Verified
  2026-08-27 evening: both copies are
  `de25c3a5389a1c702206e78424207ffb`, and no `.cs` file is newer than
  the DLL. The copy step (`DoNotBeLazy/Assemblies/DoNotBeLazy.dll` +
  `.pdb` into
  `E:\SteamLibrary\steamapps\common\RimWorld\Mods\DoNotBeLazy\Assemblies\`,
  then restart RimWorld) is the user's, and `/pull-logs` step 1 checks
  it - but it did not need doing this time.
  **Compare hashes, not the sentence above.** This bullet has now been
  wrong three times: it claimed "older than the repo build" from
  2026-08-23 through 2026-08-26 while the copies were byte-identical,
  and claimed it again on 08-27 when they were identical *again*.
  Acting on it wasted a step each time. `md5sum` both and believe the
  result:
  `md5sum DoNotBeLazy/Assemblies/DoNotBeLazy.dll "E:/SteamLibrary/steamapps/common/RimWorld/Mods/DoNotBeLazy/Assemblies/DoNotBeLazy.dll"`
- **`jobDiagnostics` is OFF as of 2026-08-27 evening**, set directly in
  `Mod_DoNotBeLazy_DoNotBeLazyMod.xml` under
  `AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\`
  with the game closed. `verboseLogging` stays `True`. That file lives
  outside the repo, so it is **not** covered by any commit here.
  Automatic Hunting was **not** disabled - that is still to do in-game.
- **What shipped 2026-08-22 evening, all untested in game:**
  - `SweepManager.AssignNextTask` no longer consumes a pool target on
    failure (fix 1) - `RemoveAt` moved to after a job is created, with a
    per-call `refused` set and a new `TargetIsGone` split for permanent
    vs transient.
  - Every area sweep rescans when a pawn runs the pool dry (fix 2); the
    `Rescannable` flag is gone and `AddNewTargets` dedups the rescan.
  - The `break` in `BeginAreaSweep` is gone (review finding 6).
  - Logging: `TargetRefusalReason` replaces the bool `TargetStillValid`
    and names the failing check; the empty-pool `RemoveSweep` says so;
    `BeginWorkstationSweep` finally emits a start line.
  - `/pull-logs` now captures ` at Namespace.Class.Method` stack frames
    and `Could not reserve` / `Existing reservers`.
- **What IS verified in game:** verbose logging works (2026-08-17), a
  `HaulMerge` sweep runs end to end (2026-08-17), the **need
  pause/resume loop works** (08-22 log, four pause/resume pairs), and as
  of the **2026-08-27 log the two 08-22 pool fixes are confirmed** - see
  "The 2026-08-27 log" below. That log also ran the job-pipeline census
  for the first time. **New 2026-08-27 evening: T7.1 and T7.2 passed** -
  a right-click moves a drafted squad, and a single drafted pawn, with no
  menu. That is change B below, confirmed in play.
- Still untested from before: **the sow fixes** (`cc502c9`) and the menu
  findings 1+2 fix. Phase 1 of the playtest plan has still not been run.

## Start here next session

**Two live threads. The second is the one with an unanswered question
in it, so read both before picking.**

### Thread A - finish Phase 7

1. **The DLL is already current - do not re-copy on faith.** Verified
   2026-08-27 evening: repo and installed copies are both
   `de25c3a5389a1c702206e78424207ffb`, and no `.cs` file is newer than
   the DLL, so the build matches source too. The old step-1 instruction
   claiming the installed copy was the 08-23 build (`da1ead61`) was
   **wrong for the third time**. Run the `md5sum` before believing any
   sentence in this file about which DLL is installed:
   `md5sum DoNotBeLazy/Assemblies/DoNotBeLazy.dll "E:/SteamLibrary/steamapps/common/RimWorld/Mods/DoNotBeLazy/Assemblies/DoNotBeLazy.dll"`
2. **`jobDiagnostics` is already OFF.** Set to `False` on 2026-08-27
   evening directly in
   `C:\Users\ninja\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\Mod_DoNotBeLazy_DoNotBeLazyMod.xml`
   while RimWorld was not running. `verboseLogging` stays `True`.
   **This file is outside the repo and is not in the commit** - if the
   game was open at any point since and overwrote it, check the value
   again. **Automatic Hunting still needs disabling in-game**; that was
   never done.
3. **`TEST_PLAN.md` Phase 7, T7.3 through T7.6.** T7.1 and T7.2
   **passed** and have moved to the Completed tests section at the
   bottom of that file. T7.3 is the undrafted half of the same report
   and is the next thing to run; T7.5 is the centre-out change, which
   nothing has yet confirmed in a game.
4. **Watch for the three deliberate behaviour changes.** Two from the
   rescan: sweeps run indefinitely (confirmed in the 08-27 log - not one
   sweep ended on its own), and a rescan uses the *requesting* pawn as
   driver, so a pawn with a restricted allowed area rescans a smaller
   area than the original click did. One from centre-out: **pawns
   walking past nearer work is correct now**, not a bug.
5. **Still never run:** `TEST_PLAN.md` Phase 1 (the sow fixes), and the
   menu findings 1+2 feedback entries. Note a **fully drafted**
   selection now shows nothing at all by design, so don't test the
   greyed-entry path that way.

### Thread B - the five new mods, ordered but NOT built

Ordered verbatim on 2026-08-27: *"New mods coming, just build separate
folders for each: Highlight Corpses With Tech; Notify Ripe (options:
Ambrosia, Berries); Notify Still Being Attacked; Uninstall Hotkey; Menu
Hotkeys."*

**Nothing was created.** The order is a go-ahead for folders and
explicitly not for behaviour. Two scope questions were put to the user,
the dialog was dismissed, and the machine shut down before they were
answered:

- **How deep does each folder go** - bare directories, an architecture
  doc stub only, a buildable empty scaffold mirroring `DoNotBeLazy/`, or
  scaffold plus stub? Recommended: scaffold plus stub.
- **Where do they live** - top-level in this repo, regrouped under
  `Mods/<Name>/`, or separate repos? Recommended: top-level here, and
  note in `CLAUDE.md` that the repo has become multi-mod.

**Ask these two before building anything.** Full detail, plus a
name-by-name reading of what each mod appears to mean and the overlap
between *Menu Hotkeys* and wishlist entry 1 (`ConfigureKeys`), is in
`RW-Wishlist.md` entry 3. Those readings are guesses from the names
alone - do not build from them.

## The 2026-08-27 log: the 08-22 pool fixes are CONFIRMED

`logs/20260827-013412-dnbl.log`, 3,882 trace lines. **Produced by the
08-23 build**, so it tests nothing from 08-27 - but it is the first real
game to exercise the 08-22 evening fixes, and they hold.

| What | Evidence |
|---|---|
| The removed `break` (finding 6) | `BeginSweep CleanFilth: 648 targets, 50 pawns` - fifty pawns assigned from one click, 51 distinct pawns given clean jobs over the session. The old code served a handful and dropped the rest silently. |
| No-consume-on-failure (fix 1) | **872 `skipping ... - reserved`, zero `no job for` discards.** Refusals are named and left in the pool. The 08-22 log had 151 discards against ~97 assignments. |
| Rescan on dry pool (fix 2) | Five rescans across three concurrent orders. Nikoletta drained hers, a rescan found 17, she carried on. |
| Sweeps run indefinitely | Zero `nothing left within ... ending sweep` lines. Deliberate, not a fault. |
| Failure tolerance | 8 x `job ended Incompletable`, 1 x `ErroredPather`, all CleanFilth. Zero `failed in a row`. No sweep died. |

**One loose end:** `Abi paused from sweep` appears once with **no resume
line**. The 08-22 log had four clean pairs. It may simply have fallen in
a dropped window (below) - unresolved, worth a glance next time.

## The log message cap - a real trap, cost data this time

`Reached max messages limit. Stopping logging to avoid spam.` appears
**four times** in the 08-27 `Player.log` (lines 1877, 2913, 3955, 4995).
That is RimWorld's ~1000-message cap being hit and reset. **Messages in
those windows are gone**, so every count from that log is a floor.

**Do not mistake the resulting hole for a bug.** It looked like one:
exactly one `BeginSweep` line exists, yet at least three pools were live
at once (297, 118 and 17 "left" within ten lines, rescans from two
different centres). `AddNewTargets` mutates one shared list, so differing
counts can only mean separate orders - the later `BeginSweep` lines were
dropped, not missing from the code.

**The cause is ours.** 2,136 of 3,882 trace lines were
`job <pawn>: Wait_MaintainPosture from none [-] (tree ?)` - drafted pawns
standing still, a job `JobSourcePatch` cannot attribute and logs anyway.
That noise is what blew the cap. **Turn `jobDiagnostics` off unless you
are actively chasing an idle report**, and consider making
`JobSourcePatch` skip `Wait_MaintainPosture` before turning it on again.

## Job pipeline census - first run, one piece of news

From the same log, the only time these instruments have ever emitted:

- `TryFindAndStartJob`: prefix `TKS_PriorityTreatment` - expected.
- `StartJob`: prefixes `CommonSense`, `VFEPirates.Mod`; our postfix.
- `EndCurrentJob`: prefix `CommonSense`, plus our prefix and postfix.
- `DetermineNextJob`: **unpatched.**
- `JobGiver_Work.TryIssueJobPackage`: **transpiler `SmarterConstruction`.**

**The last one is the news** - a transpiler on the WorkGiver scan loop,
which was not on the radar in any previous session. Not implicated in
anything yet; know it exists before blaming vanilla for a scan oddity.

The idle probe produced a single line (`idle Food: no job - ... queued=2
... thinker=ok`) - one occurrence with jobs queued, i.e. between jobs,
not stuck.

**Errors: nothing of ours.** 35 x `MissingMethodException` from
`ARY_AutomaticHunting.AnimalHuntingManager.GameComponentTick`, the same
known-broken mod, still enabled despite T0.4 saying to disable it.
Contained by `GameComponentUtility`'s per-component try/catch. Zero
`Exception in WorkGiver`, zero error-recover jobs, and **zero
`Could not reserve` / `Existing reservers`** - consistent with an
all-cleaning session, since cleaning has no destination to reserve. That
contrast is the same one that pinned the 08-22 diagnosis.

## TOP PRIORITY, built 2026-08-27 - B is PASSED, A is not

Two changes, both ordered directly rather than found in review, both
outranking every open item below. Full specification in
`DoNotBeLazy_Architecture.md` section 0, "TOP PRIORITY". Tests are
`TEST_PLAN.md` Phase 7. Builds clean, 0 warnings; **neither has run in a
game.**

The two share an intent: **the player's mouse click is the instruction,
and the mod was overriding it in both directions** - ignoring where they
clicked when handing out work, and swallowing the click entirely when
they meant to move.

### A. Work emanates from the click - **STILL UNTESTED** (T7.5)

`AssignNextTask` picked each pawn's next target nearest to **the pawn**.
`ScanCenter` - the clicked cell - built and rescanned the pool but never
ordered it, so a group sweep dissolved into each pawn tidying its own
feet and the pile the player pointed at was cleared whenever.

`NearestTargetIndex` is now `NextTargetIndex(center, pawnPos, pool,
skip)`: rank by distance from `ScanCenter`, break ties on distance from
the pawn. The pool empties in rings outward from the click. One file,
`SweepManager.cs`.

**This graduates the `RW-Wishlist.md` "focus from centre out" entry**,
and it settles that entry's open weighting question the strict way,
against that entry's own recommendation. The objection it raised was
accepted, not answered: **a pawn can now walk past a target beside them
to reach one nearer the click, and that is the pass condition, not a
bug.** It is bounded by `sweepRadius` - trivial at the default 16,
possibly annoying at the maximum 50, which is what T7.6 exists to
measure. The mitigation is designed and written down in the wishlist
entry; **do not apply it without asking.**

### B. A right-click meant to move pawns must move pawns - **PASSED 2026-08-27**

**Reported from play: "we always trigger clean or the underlying task
when moving or trying to move to a formation."** This is review finding
4 confirmed live, and worse than the finding said - it does not need
drafted pawns to bite.

Appending a single option defeats two separate vanilla mechanisms:

1. `TryMakeFloatMenu` auto-executes a menu whose options are **all**
   `autoTakeable` - that is how a right-click moves a pawn with no menu.
   Ours never are, and feedback entries are `Disabled`, so the first one
   we add cancels it.
2. `TryMakeMultiSelectFloatMenu` returns `false` on an empty list, and
   that `false` is what lets the group move happen. One option flips it
   to `true` - into a menu with **no goto entry**, because
   `ChoicesAtForMultiSelect` never builds one. The player's move became
   a menu whose only entry was `* Clean until done`.

Both halves fixed, in `FloatMenuPatch.cs`:

- **All-drafted selections get nothing at all** - no sweep options, no
  greyed feedback, no consume. `CanSweep` rejected drafted pawns anyway,
  so every entry suppressed was a grey one. Gated on *all* drafted, not
  *any*; a mixed selection still holds pawns that can sweep.
- **A `Move here` option** is inserted at `MenuOptionPriority.GoHere`
  (above our `Low`) on the multi-select path only, and only when the
  incoming list was empty - i.e. only when we are the reason the menu
  opened. Per pawn it calls
  `RCellFinder.BestOrderedGotoDestNear(cell, pawn, null)` then
  `FloatMenuMakerMap.PawnGotoAction(cell, pawn, dest)`. Both verified
  present in this build by reflecting on `lib/Assembly-CSharp.dll`, and
  both are what vanilla's own `GotoLocationOption` uses - so the spread
  that stops a squad stacking on one cell is vanilla's, not ours.

**Deliberately not done:** our entries are not made `autoTakeable`. That
would start a sweep on a bare right-click with no menu and no
confirmation - a worse failure than the one being fixed.

**One side effect.** A fully drafted selection used to show a greyed
`* Fight fires until done - <pawn>: is drafted`. It now shows nothing.
No behaviour lost, but if the long-open "should drafted pawns fight
fires" question is ever answered yes, this suppression needs a
firefighting exemption too.

## Open item 1: vehicle packing offers no `* pack until done`

Reported 2026-08-19. **Fully diagnosed, nothing implemented, both fixes
awaiting a go-ahead.** Full detail in architecture doc section 0; the
short version:

1. **Vehicle Framework eats the whole right-click when 2+ pawns are
   selected.** It prefixes `Selector.HandleMapClicks`
   (`Extra.MultiSelectFloatMenu`); on a multi-select right-click over a
   cell holding a `VehiclePawn` it opens its own one-entry
   ("Board \<vehicle\>") `FloatMenuMulti` and returns `false`, so vanilla
   `HandleMapClicks` never runs and `ChoicesAtForMultiSelect` is never
   called. **Our postfix never fires - so *every* `*` option vanishes on
   a group click on a vehicle, not just pack.** Single-select is not
   intercepted and should already work.
2. **Even with the menu fixed, the sweep would do one item with one
   pawn.** `PackVehicle.PotentialWorkThingsGlobal` returns the
   *vehicles* awaiting loading, not the items, so the pool holds one
   entry; `BeginAreaSweep` drops every pawn after the first, and one
   `LoadVehicle` job later `AssignNextTask` finds an empty pool and ends
   the sweep. General gap - the pool assumes one job per target, which
   is wrong for `WorkGiver_Refuel` / `HaulToContainer` /
   `FillFermentingBarrel` in vanilla too.

**Do this first, before writing any code: the single-pawn test.** Select
**one** colonist, right-click a vehicle that is being packed. The option
should appear. If it does, cause 1 is confirmed and the diagnosis is
complete. **If it does not appear for a single pawn either, cause 1 is
not the whole story - keep digging.**

Already established, don't re-derive: `PackVehicle` passes
`IsSweepEligible` cleanly (workType Hauling, `directOrderable` defaults
true, worker is a `WorkGiver_Scanner`); its `JobOnThing` targets the
`VehiclePawn` itself; the vehicle *is* in `cell.GetThingList` despite
the multi-cell footprint; there is no `HasJobOnThing` override so our
probe is faithful; and `PotentialWorkThingRequest` is a plain property
that cannot throw.

## CLOSED 2026-08-22 evening: colonists standing still

**Answered from a real log. The cause was ours, and there was no
exception behind it.** Kept here because three sessions were spent
theorising about other mods; the record is worth more than the space.

`logs/20260822-225440-dnbl.log`, produced by the 00:01 build:

| WorkGiver   | pool | selected | got work | `no job` discards |
|-------------|-----:|---------:|---------:|------------------:|
| HaulGeneral |    1 |   **36** |    **1** |                 0 |
| HaulGeneral |    6 |       35 |        4 |                 2 |
| HaulGeneral |   17 |   **34** |    **2** |                16 |
| HaulGeneral |   76 |       34 |       30 |            **72** |
| HaulGeneral |   59 |        1 |        1 |                26 |
| CleanFilth  |  644 |        4 |        5 |                 0 |
| CleanFilth  |  667 |       32 |       40 |                 0 |

Two of our own bugs, both now fixed:

1. **`BeginAreaSweep` broke out of the pawn loop on an empty pool**, so a
   1-target pool served one pawn and dropped the other 35 without a
   word. They fell back to the think tree and stood there. That is the
   whole report.
2. **`AssignNextTask` consumed a target before asking for a job**, so a
   transient refusal destroyed it for the entire group. 151 `no job`
   discards against ~97 haul assignments - more targets thrown away than
   hauled.

**The tell that pins the mechanism:** CleanFilth has **zero** discards
and HaulGeneral is full of them. Cleaning has no destination to reserve;
hauling does. Vanilla's own `Could not reserve Thing_Meat_Bear_Grizzly...
Existing reservers: [0] Inga` lines say the same thing directly.

**What it was NOT, each ruled out by evidence rather than argument:**

- **No job-pipeline exceptions at all** in that log - no
  `Exception in WorkGiver`, no error-recover jobs, no think-tree throws.
- **Automatic Hunting is broken but innocent.** 232 ×
  `MissingMethodException: TraverseParms.For(Pawn, Danger, TraverseMode,
  bool, bool, bool, bool)` out of
  `ARY_AutomaticHunting.AnimalHuntingManager.GameComponentTick`. The mod
  does nothing at all. It **cannot** cause standing still:
  `GameComponentUtility.GameComponentTick` wraps each component in its
  own try/catch (checked in the decompile), so it can't take down our
  `NeedMonitor` either. Recommend disabling it for the noise, not the
  symptom.
- **Sense of Urgency** was cleared on 2026-08-21 and stays cleared.
- **Job diagnostics never ran.** The settings file held only
  `<verboseLogging>True</verboseLogging>`, so `PipelineCensus`,
  `JobSourcePatch` and `IdleProbe` have *still* never emitted a line.
  They are built and available if a future idle report needs them.

**The general lesson, worth keeping:** before blaming a throwing mod for
a symptom, check *where* it throws. Vanilla catches per GameComponent,
per WorkGiver (`JobGiver_Work.TryIssueJobPackage`), and around the think
tree (`DetermineNextJob` -> `TryStartErrorRecoverJob`). A mod throwing
inside any of those is loud and contained.

## Review findings from 2026-08-18 evening - open, unfixed

All six are in the overnight session's own new code. Full detail in
architecture doc section 0.

1. **FIXED 2026-08-21, untested in game - menu feedback missed
   pawn-side refusals.** `EligiblePawns` empty used to `continue` before
   any feedback was built, so "Hauling is priority 0", "pawn is
   drafted", "no Manipulation" produced silent nothing. Now
   `PawnValidator.RefusalReason` names the reason and the first refusing
   pawn, scoped to defs whose `PotentialWorkThingRequest` accepts
   something on the clicked cell. **Watch for this in the stone-blocks
   repro** - it is the likelier answer there than `NoEmptyPlaceLower`.
2. **FIXED 2026-08-21, untested in game - a burning cell could produce
   NO MENU AT ALL.** Not an empty menu; the window never opened.
   `FindTargetWithJob` now writes a hardcoded reason when a `Fire`
   yields no job, so there is always at least one entry to render.
3. **Duplicate `* Pack vehicle until done`** - `PackVehicleTurret`
   carries the same label as `PackVehicle` and also targets the
   `VehiclePawn`, so a vehicle needing cargo *and* turret ammo shows the
   entry twice. **The Sense of Urgency half of this finding is
   withdrawn** (2026-08-21): that mod adds no `Firefighter` def, and
   nothing sweep-eligible at all.
4b. **Paused sweeps now survive interrupts that used to end them** -
   `SweepManager.cs:365`. A manual player order during a pause no longer
   ends the sweep; the pawn is pulled back when it finishes. Looks
   intentional, but it's wider than the reported bug.
5. **`MaxPauseTicks` only evaluated on a job end** -
   `SweepManager.cs:381`. Also the log line prints the constant, not the
   elapsed ticks, so it can claim "after 30000 ticks" when it was far
   longer. One-line fix.
6. **FIXED 2026-08-22 evening, untested in game.** The `break` in
   `BeginAreaSweep` is gone - every selected pawn now gets an
   `AssignNextTask` call and drops out there if it genuinely has nothing
   to do. This turned out to be the larger half of the standing-still
   report, not a minor effect. The other half of the finding (a rescan
   re-admitting a fire another pawn is walking to) is unchanged and now
   applies to **every** sweep type, since they all rescan;
   `TargetRefusalReason` returns "reserved" for it, so it is at least
   traced. **This `break` was also half of vehicle-packing cause 2** -
   check that side when the vehicle work resumes.

1 and 2 are done as of 2026-08-21 and **need a look during the
playtest** - neither has been seen in a running game. 3-6 are still
open. `TEST_PLAN.md` has no tests for either, since it predates the
whole feedback change.

## Open items 3-6: diagnosed 2026-08-22 evening

**Item 4 was built 2026-08-27** and is marked below; 3, 5 and 6 are still
fully traced to a vanilla mechanism with nothing written.
They were ranked as items 3-8 of the fix list; 1, 2 and the logging ones
are done. Item 6 was reported from play after that list was written.

### 3. Workstation sweeps drop after one bill (butchering, drug synthesis)

**Proven, with the vanilla source line.**
`WorkGiver_DoBill.TryStartNewDoBillJob` opens with:

```csharp
haulOffJob = WorkGiverUtility.HaulStuffOffBillGiverJob(pawn, giver, null);
if (haulOffJob != null && dontCreateJobIfHaulOffRequired) return haulOffJob;
```

So `JobOnThing` returns a **`HaulToCell`** job, not `DoBill`, whenever
finished product is sitting on the bench - the normal state of a butcher
table or drug lab right after a bill. `JobTrackerPatch`'s continuation
branch requires `endedJob.def == JobDefOf.DoBill`, so when that haul-off
ends it falls through to `Notify_JobEnded`, hits
`order.WorkGiverDef.Worker is WorkGiver_DoBill`, and `RemoveSweep`s. The
refuel branch (`CompRefuelable` -> `RefuelWorkGiverUtility.RefuelJob`)
kills it the same way.

The 08-22 log matches exactly: every workstation order in it is **one**
`job ended Succeeded (DoBills...)` line and nothing more. That line is
the haul-off end - the `DoBill` end returns early inside `JobTrackerPatch`
and never logs.

Second killer, same area: on ingredient-search failure vanilla sets
`bill.nextTickToSearchForIngredients = now + ReCheckFailedBillTicksRange`
(500-600 ticks). One transient miss - a teammate holding the stack for a
moment - makes `JobOnThing` null for ~9 seconds and we remove the order
permanently. `MaxConsecutiveFailures` is never applied to workstation
orders.

**Fix:** re-ask the bill giver on any `Succeeded` job while a
`WorkGiver_DoBill` order is active, not only on `DoBill`; drop the
unconditional `RemoveSweep`; give a null `JobOnThing` a strike against
`MaxConsecutiveFailures` with a retry rather than ending the order. Same
for the resume path in `AssignNextTask`.

### 4. Drafted right-click can no longer move pawns - **FIXED, PASSED 2026-08-27**

**Implemented, untested in game. See "TOP PRIORITY" item B above**, which
also records that the bug was wider than this entry describes: it does
not need drafted pawns. The diagnosis below stands and is kept for the
mechanism.

`FloatMenuMakerMap.TryMakeFloatMenu` auto-executes the menu when *every*
option is `autoTakeable` and enabled - that is how a drafted right-click
moves a pawn with no menu ("Go here" is `autoTakeable`, priority 10).
Our appended `*` entries are never `autoTakeable`, and feedback entries
are `Disabled`, so the first one we add sets `flag = false` and the
auto-take is cancelled.

Worse on multi-select: `TryMakeMultiSelectFloatMenu` returns `false` when
the option list is empty, and that `false` is what lets the squad move
happen. Our option makes it return `true`. And
`ChoicesAtForMultiSelect` **never adds a goto option at all** - it builds
only from `Thing.GetMultiSelectFloatMenuOptions` - so the menu we force
open genuinely has no "move here" entry.

**Fix:** suppress `*` options entirely when the selection is drafted
(`PawnValidator.CanSweep` rejects drafted pawns anyway, so every entry we
add there is a useless grey one), **and** add a "Move here" option
running `FloatMenuMakerMap.PawnGotoAction` per pawn whenever we are the
reason a multi-select menu opened.

### 5. Nonsense entries on a workbench (the stonecutter screenshot)

Right-clicking a stonecutter's table offered `* Cook meals at stove`,
`* Butcher creatures` and `* Fix broken-down buildings`, and did **not**
offer stonecutting.

`WorkGiver_DoBill.PotentialWorkThingRequest` narrows to a specific def
**only when `fixedBillGiverDefs.Count == 1`**; otherwise it returns
`ThingRequest.ForGroup(ThingRequestGroup.PotentialBillGiver)`.
`CookMeals` lists three stoves and `ButcherCreatures` lists two, so both
fall back to the broad group and `FloatMenuPatch.WantsSomethingHere`
says yes for *any* workbench. `FixBrokenDownBuilding` is the same shape
via `ThingRequestGroup.BuildingArtificial`.

**Fix:** scope `WorkGiver_DoBill` feedback entries with vanilla's own
`WorkGiver_DoBill.ThingIsUsableBillGiver(thing)`, which checks
`fixedBillGiverDefs.Contains(thing.def)`. For broad-request non-DoBill
defs, require `HasJobOnThing` or a written `JobFailReason` rather than
accepting the request group alone.

Separately: `* Cut stone blocks` was **absent** rather than greyed, so
`FindTargetWithJob` got no job from the table - most likely
`!BillStack.AnyShouldDoNow` (bill suspended or its target count met).
Legitimate, but we say nothing at all about the one def the player
actually clicked. Worth a `- no bills ready` entry.

### 6. The menu offers a sweep the radius scan can't fill

**Reported from play 2026-08-22 evening: `* Haul general things until
done` appears when there is nothing haulable within 16 tiles.**
Already visible in `logs/20260822-225440-dnbl.log` - `BeginSweep
HaulGeneral: scan found nothing, no sweep started` fires **four times**
in that one session.

**The menu and the sweep ask different questions, and nothing reconciles
them.**

- `FloatMenuPatch.FindTargetWithJob` asks: does anything *on the clicked
  cell* return `HasJobOnThing` for *any* of the selected pawns? No
  radius, no pool.
- `SweepManager.BeginAreaSweep` then asks `TaskScanner.FindTargets`: what
  does `PotentialWorkThingsGlobal` return *within the radius*, for
  `eligiblePawns[0]` alone?

Four ways those disagree, in rough order of likelihood:

1. **`ListerHaulables` vs `HasJobOnThing`.**
   `WorkGiver_Haul.PotentialWorkThingsGlobal` is
   `listerHaulables.ThingsPotentiallyNeedingHauling()`, which **excludes
   anything already sitting in valid storage**. `HasJobOnThing` doesn't
   consult the lister at all - it just asks
   `StoreUtility.TryFindBestBetterStorageFor`. So an item in a stockpile
   that has a *better* stockpile available passes the menu check and is
   invisible to the scan. This is the one to test first: click something
   that is already stored.
2. **Driver pawn vs any pawn.** The menu accepts a job for any selected
   pawn; the scan runs entirely against `eligiblePawns[0]`.
3. **Filters the scan applies and the menu doesn't.** `ScanThings` also
   gates on `IsForbidden(forPawn)`, `allowedArea`, `CanReserve(forPawn,
   thing)` and `CanReachTarget`. The menu path checks none of them.
4. **The `CanReserve` pre-filter omits `ignoreOtherReservations`** while
   the job it gates passes `forced` through - already listed under
   still-open bugs, and it makes the pre-filter stricter than the job.

**Fix direction, not yet decided.** The honest options are to make the
menu run the same radius scan it is advertising (correct, but pays for a
radial scan per eligible def on every right-click - see the T4.1 perf
entry), or to keep the cheap check and make the failure graceful, since
`BeginAreaSweep` already messages `"* X: nothing to do within N tiles."`
and logs it. **Ask before building either.** The perf question is real:
`EligibleDefs()` is ~200 defs on this modlist.

## Verified in earlier sessions (don't re-derive)

- **`FireCompat.HasFireJob` is a faithful port** of
  `WorkGiver_FightFires.HasJobOnThing` minus the intended home-area
  gate. Diffed against the decompile by hand. In particular: vanilla's
  faction test is *part of* the home-area gate
  (`(sameFaction || hostFaction) && !Home && manhattan > 15`), **not** a
  separate "don't help hostiles" rule - vanilla does let colonists beat
  fires on enemies. Dropping it with the home gate is correct. It reads
  like an omission on a cold read; don't "fix" it.
- `HandledDistSquared 25` == `InHorDistOf(..., 5f)`; `225` matches;
  `JobOnThing` is a bare `new Job(BeatFire, t)`, so bypassing
  `HasJobOnThing` is a complete override.
- `JobDriver_BeatFire.TryMakePreToilReservations` returns true without
  reserving - the reservation happens opportunistically in the approach
  toil. So `FireIsBeingHandled` does have reservations to read (fan-out
  works), and vanilla tolerates two pawns per fire where we don't.
- `FightFires` sets no `scanThings` in XML, so it takes the default
  `true` - the `scanThings` branch does run for it.
- **Radius behaviour, answering "is it really finding work of that type
  within 16 tiles":** yes, structurally. Both scan branches enforce the
  radius from the clicked cell, and the same `WorkGiverDef` builds the
  pool and issues every job. Two caveats: the pool is a **snapshot**
  taken at click time (only fire sweeps rescan, so work appearing later
  inside the radius is never picked up), and the entire scan runs
  against `eligiblePawns[0]` as driver - allowed-area, `CanReach` and
  `CanReserve` are per-pawn, so a restricted or walled-off driver
  shrinks the pool for the whole group. The comment at
  `SweepManager.cs:313-316` claims those filters don't vary by pawn;
  it's wrong, fix it when next in there.
- `showSweepOverlay` still does nothing - confirmed by grep, and
  already recorded in architecture doc 3.4. Relevant to the radius
  question: the player has no way to see what 16 tiles covers, and the
  checkbox implies they should.

## Test plan

The playtest plan now lives in the repo as **`TEST_PLAN.md`** - plain
markdown, readable without a browser (the published artifact at
`https://claude.ai/code/artifact/e60cfd11-1f82-46a8-9111-a25d9352a2dd`
is the older HTML original and is no longer the source of truth).

Phase 0 (prove the DLL is current, the logger is live, and the
`wantedPlantDef` reflection resolved) passes as of 2026-08-17. **Phase 1
- the four sow tests - has still not been run.** The plan predates the
fire-sweep and menu-feedback work, so it has no tests for either, and
nothing for vehicles.

Three corrections are already folded into `TEST_PLAN.md` and marked in
place: T0.1 hardcoded a stale DLL timestamp, T3.5 wrongly said food
yields `* Eat meal` (only drugs set `ingestCommandString`, so food reads
`* Consume fine meal`), and **T3.6 was inverted by `9dc8717`** - it
still expected a burning tile to offer nothing, which was true before
fire sweeps shipped.

## The float-menu path is still mostly untraced

`AddConsumeOption`, `FindTargetWithJob`, `IsSweepEligible` and the
option-building path in `FloatMenuPatch` contain **zero**
`Logger.Message` calls - only `Error`/`Warning`. Only `SweepManager` and
`TaskScanner` are traced, and those only run *after* an option is
picked. So any "wrong/missing/duplicate menu option" bug is invisible to
the trace - the vehicle report is the second one in a row that a single
verbose line per offered option would have answered in minutes.

Partly mitigated by the disabled feedback entries, which surface refusal
reasons in the menu itself - but only for target-side refusals (see
finding 1 above).

## Corpse / `* Consume` - investigated, decision is LEAVE AS-IS

Reported: harvesting a corpse at a harvesting table should say "harvest",
not "consume". **User decided to leave the code alone.** Recorded so
nobody re-derives it:

- The harvesting table is **Reclaim, Reuse, Recycle (Continued)**
  (`Mlie.ReclaimReuseRecycle`, Workshop `2567364887`).
- Its `R3_DoWorkHarvestCorpse` is `giverClass WorkGiver_DoBill`,
  `workType Doctor`, label "harvest corpse", and crucially
  `fixedBillGiverDefs: R3_TableHarvesting`.
- We already offer it correctly as `* Harvest corpse until done` -
  `IsSweepEligible` takes any `WorkGiver_DoBill` regardless of workType.
  But `fixedBillGiverDefs` means `HasJobOnThing` is only true for the
  **table**, and `FindTargetWithJob` only looks at things on the clicked
  cell. So clicking the *corpse* can only ever produce `* Consume ...`;
  clicking the *table* produces the harvest option.
- `RimWorld.IngestibleProperties` (note: `RimWorld`, not `Verse`) -
  verified by reflection: `showIngestFloatOption` defaults **true**,
  `ingestCommandString` defaults **empty**. Only drugs set that string
  in Core. So corpses get our hardcoded `"Consume " + LabelShort`.
- The label matches vanilla exactly - base RimWorld also says
  "Consume human corpse". Not a divergence, a design question.

## The logging trap - resolved, but know the history

`Logger.VerboseLogging` was hardcoded `false` from Phase 1 until
2026-08-16, making every `Logger.Message` a no-op. Several playtest logs
showed zero `[DoNotBeLazy]` lines and were read as "nothing fired". It's
now driven by a settings checkbox and **confirmed working in game**.

Still: before concluding anything from an absence of log lines, check
whether that code path logs at all (see the float-menu note above).

## Workflow: log extraction

**Say `/pull-logs` and Claude does all of this.** The command lives at
`.claude/commands/pull-logs.md` and is checked into the repo. It checks
which build actually produced the log (installed DLL vs built DLL vs log
timestamp - step 1, because that has been wrong twice), extracts the
`[DoNotBeLazy]` trace *and* vanilla's own exception lines *and* the
active mod block, archives all three to a gitignored `logs/` folder with
a timestamp, and summarizes without pasting. Pass it a focus if you have
one: `/pull-logs standing still`.

**Never extract only `[DoNotBeLazy]` again.** Every extraction before
2026-08-22 did exactly that, which is why vanilla's `Exception in
WorkGiver X` lines - the loud half of any job bug - were never once in
front of us.

1. Options > Mod Settings > Do Not Be Lazy > tick "Verbose logging",
   and "Job diagnostics" for anything job/idle related. Close the
   settings window - that's what writes it to disk.
2. Reproduce.
3. `/pull-logs`. The manual version, if you want it by hand:

```
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log" -Pattern '\[DoNotBeLazy\]' | ForEach-Object { $_.Line }
```

RimWorld truncates `Player.log` on launch - extract before restarting.

**An exception line never names the mod that threw it.** The owning
assembly is only in the ` at Namespace.Class.Method ()` frames beneath
it, and RimWorld collapses repeats to `[Ref ABCD1234] Duplicate
stacktrace, see ref for original` - so the frames appear exactly **once**,
on the first occurrence. `/pull-logs` captures those frames as of
2026-08-22 evening; before that it didn't, which is why the 232
`MissingMethodException` lines in that pull had to be traced back to
Automatic Hunting by hand in the raw log.

Diagnostic lines emitted when "Job diagnostics" is on (new 2026-08-22,
all untested in game): `pipeline <method>: prefix=<owner>, ...` once at
startup / `job <pawn>: <JobDef> from <ThinkNode> [<assembly>] (tree X)`
for idle-shaped jobs only / `idle <pawn>: no job - drafted=.. mental=..
hunting=..` every 250 ticks. **An assembly other than `Assembly-CSharp`
in a `job` line names the mod outright**, and an `idle` pawn with no
`job` lines at all means `DetermineNextJob` returned NoJob - a different
bug from a pawn looping on wait jobs.

Sweep trace lines emitted:

```
scan <def> r=N at <cell> for <pawn>: N targets
BeginSweep <def>: N targets, M pawns
BeginSweep <def> at <bench>: <pawn> of N ranked, first job <JobDef>      (new 08-22 pm)
BeginSweep <def>: no job on <bench> for <pawn>, trying next              (new 08-22 pm)
BeginSweep <def>: no job on <bench> for any of N pawns, no sweep started (new 08-22 pm)
<pawn>: <JobDef> on <target> plant=<def> (N left)
<pawn>: skipping <target> (<def>) - <reason>                             (new 08-22 pm)
<pawn>: no job for <target> (<def>), left in pool, N total
<pawn>: nothing left within N of <cell>, ending sweep (<def>)            (new 08-22 pm)
<pawn>: job ended <condition>
<pawn> paused from sweep: ...
<pawn>: needs satisfied, resuming sweep (<def>)
<pawn>: still under threshold after N ticks paused, ending sweep.
```

Reading them:

- `scan ... : N targets` paired with `BeginSweep ... : N targets, M
  pawns` answers "did the radius scan find anything".
- **`M pawns` much larger than `N targets` used to mean M-N pawns were
  silently dropped.** That was the standing-still bug; it should not
  happen any more, and if it does, the fix regressed.
- `skipping ... - <reason>` names the check that refused: `reserved`,
  `forbidden`, `outside allowed area`, `unreachable`, `burning`,
  `sow settings`, `out of bounds`, `gone`. A pile of `reserved` on one
  sweep is pawns fighting each other for the same targets.
- `no job for ... left in pool` is the WorkGiver itself declining -
  usually a full or reserved haul destination. The target stays
  available for another pawn now, so these are no longer a leak.
- `nothing left within N of <cell>` is a sweep ending cleanly. Before
  08-22 pm this was silent, which is why a finished sweep looked
  identical to a pawn wandering off.
- The pause/resume pair is the one to grep when checking the need
  fix - the old bug showed as a pause line after *every* task with no
  resume line between them. **Confirmed working in the 08-22 evening
  log**: four pause/resume pairs, all resumed.

## Modlist

**~60 mods**, RimWorld 1.5.4409, no DLC. Authoritative list comes from
the `Loading game from file ... with mods:` block in any log.

On our code paths:

- **Performance Fish** patches `WorkGiverDef::get_Worker()` (which
  `EligibleDefs()` calls on every def), `ListerThings.ThingsMatching`,
  `WorkGiver_Haul.PotentialWorkThingsGlobal`.
- **TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`.
- **Sense of Urgency** adds parallel "urgent" WorkGiverDefs, but they
  are workType `Urgent`/`Doctor`/`Cooking`/`Hunting` and none of their
  givers is a `WorkGiver_DoBill` - so **none of them is sweep-eligible**
  and it cannot duplicate or alter any `*` option. Verified 2026-08-21
  from its own defs; it had been listed as a likely duplicate source on
  no evidence.
- **Reclaim, Reuse, Recycle** adds the harvesting/refurbishment tables
  (both `WorkGiver_DoBill`, so both are sweep-eligible).
- **Vehicle Framework** (Workshop `3014915404`) prefixes
  `Selector.HandleMapClicks` and **suppresses vanilla's entire
  multi-select float menu over a vehicle** - see open item 1. It also
  adds nine WorkGiverDefs, of which the Hauling ones (`PackVehicle`,
  `PackVehicleTurret`, `RefuelVehicle`, `LoadUpgradeMaterials`) all
  target the `VehiclePawn` itself and all want repeat-on-the-same-target
  semantics we don't have. **Vanilla Vehicles Expanded** (`3014906877`)
  sits on top of it and adds a garage bench (`WorkGiver_DoBill`, so
  sweep-eligible) plus `VVE_RestoreWreck`.

## Two broken third-party mods - ignore their log noise

**Corrected 2026-08-21 - there is only one, and it is not the one this
file named for three sessions.**

- **Automatic Hunting** (`Arylice.Rimworld.AutomaticHunting`, Workshop
  3340648302) - `MissingMethodException` on `TraverseParms.For`, thrown
  every tick out of
  `ARY_AutomaticHunting.AnimalHuntingManager.GameComponentTick`; 232 of
  them in the 08-22 evening log. Compiled against a different RimWorld
  build; the mod does nothing at all. **Cleared as a cause of standing
  still on 2026-08-22** - `GameComponentUtility.GameComponentTick` wraps
  each component in its own try/catch, so the throw is contained. It
  was "prime suspect" in this file for two sessions on the strength of
  the stack trace alone. Recommend disabling it for the log noise.
- **Sense of Urgency** (`ZombiePhil.Urgency`, Workshop 3001253573) is
  **cleared**. It ships a real 1.5 assembly
  (`1.5/Assemblies/ZombiePhil.Urgency.v15.dll`, April 2025) which
  contains no `WaitWith`, no `Toils_General`, no `JobDriver` and no
  Harmony reference - WorkGivers only. The "hunting is completely
  broken" symptom was pinned on it because it has a hunting-priority
  WorkGiver, not because anything in it throws.

## Still-open bugs in our code (not yet fixed)

- **The shared pool assumes one job per target.** Correct for
  haul/mine/cut/sow, wrong for every "keep bringing things to this one
  thing" WorkGiver - vehicle packing, and `WorkGiver_Refuel` /
  `HaulToContainer` / `FillFermentingBarrel` in vanilla. The
  `WorkstationTarget` path already does the right thing but is gated on
  `scanner is WorkGiver_DoBill` and is single-pawn by design.
- `ScanCells` has no `IsForbidden` check (`ScanThings` has one).
  Harmless for sow only because `GrowerSow.JobOnCell` checks it
  downstream - latent for any future cell-based WorkGiver.
- The `CanReserve` pre-filter in `TaskScanner` omits
  `ignoreOtherReservations` while `JobOnCell` passes `forced` through,
  so the pre-filter is stricter than the job it gates. Most likely
  reason for a pool smaller than the visible work. Also applies to
  fires: vanilla only reservation-checks a fire past 15 tiles, we check
  every one.
- `IsPreparatoryJob` is not scoped to cell targets, so construction
  frames also get one extra re-queue. Plausibly an improvement, capped,
  but untested beyond sow.
- The pool is scanned against one driver pawn; per-pawn filters
  (allowed area, reachability, reservation) therefore apply the driver's
  answer to the whole group. See the radius note above. **Partly
  mitigated 2026-08-22**: a refused target now stays in the pool for
  another pawn instead of being destroyed, and the rescan re-runs the
  scan against whichever pawn asked - but the rescan then inherits *that*
  pawn's restrictions, so it cuts both ways.
- **New 2026-08-22, from the rescan:** an area sweep no longer has a
  natural end. `* Clean until done` will keep rescanning and keep
  finding new filth for as long as pawns track it in. Deliberate - it is
  what "until done" was asked to mean - but it is a behaviour change,
  and drafting or a manual order is now the only thing that ends such a
  sweep. Watch it in the next playtest before deciding whether it needs
  a cap.
- Perf watch: `CanReachTarget` now runs per cell in the radial scan
  (~800 at default radius 16, ~7,800 at the max of 50).

## Still unanswered from earlier sessions

1. **"Cannot force-haul stone blocks."** Two candidate answers now: a
   stockpile in reach that doesn't accept Blocks (`NoEmptyPlaceLower`,
   which the new disabled entries will surface), or Hauling sitting at
   priority 0 for the selected pawns (which they will **not** surface -
   review finding 1).
2. **"The `* forced delivery to (ITEM)` is gone."** Needs a repro.
   Sense of Urgency is no longer a candidate explanation - none of its
   defs is sweep-eligible (2026-08-21).

## Wishlist - further out than "planned"

`RW-Wishlist.md` (new 2026-08-23) holds ideas that have not been
architected at all, one rung below the section that follows this one.
Currently: **ConfigureKeys / interface changes** (captured but not
understood - needs the requester to expand it before anything happens)
and **Do Not Be Lazy focus from centre out** (understood, not designed -
`NearestTargetIndex` sorts by distance from the *pawn*; centre-out would
sort by distance from `order.ScanCenter`, and the design work is the
weighting between the two, not the sort). The wishlist entry notes that
centre-out and the dead `showSweepOverlay` checkbox are worth doing
together.

## Planned but NOT implemented - do not build without a fresh go-ahead

- **Vehicle fix 1** - Harmony prefix on the `Vehicles.FloatMenuMulti`
  constructor `(List<FloatMenuOption>, List<Pawn>, Pawn, string,
  Vector3)`, injecting our options into VF's group menu. The prefix runs
  before the base `Verse.FloatMenu` constructor caches option sizes, so
  mutating the list in place is safe. Patched by reflection
  (`AccessTools.TypeByName`) so VF stays a soft dependency. Would be the
  first patch in this mod aimed at another mod's type.
- **Vehicle fix 2** - generalise `WorkstationTarget` to "persistent
  target" (identified via a new `VehicleCompat` matching
  `Vehicles.WorkGiver_CarryToVehicle` subclasses by reflected type), and
  allow multiple pawns on one such target. VF reserves per *item* inside
  `FindThingToPack`, so parallel haulers are safe.
- `DoNotBeLazy_Architecture.md` section 5.4 - idle-pawn nudge
  ("standing" rule). See open item 2 for why this is not the fix for the
  standing-still report.
- The sweep radius overlay - `showSweepOverlay` exists as a setting with
  no code behind it.
- `DoNotFreakOut_Architecture.md` - a separate, Harmony-free mod. Not
  started, no folder.
- Drafted pawns in fire sweeps - vanilla allows drafted firefighting
  (`canBeDoneWhileDrafted`, `autoTakeablePriorityDrafted: 20`) and we
  don't. Needs a per-WorkGiver exception in both `PawnValidator.CanSweep`
  and `SweepManager.MapComponentTick`. **For now: undraft before
  ordering a fire sweep.** Ask before building it.

## Method notes (these work well, keep using them)

- **Reflect on the real game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom`. `GetType()`
  returns null for a wrong namespace rather than throwing - and
  `GetTypes()` throws `ReflectionTypeLoadException` on this assembly, so
  catch it and read the Types list. Caught this way:
  `IngestibleProperties` / `ThingDefGenerator_Corpses` are in `RimWorld`
  not `Verse`; `ReservationManager` is in `Verse.AI` not `RimWorld`.
- **Reflecting on a *mod* DLL needs two extra tricks**, learned on
  `Vehicles.dll`:
  - Preload every referenced assembly first
    (`$asm.GetReferencedAssemblies()` lists them) from
    `RimWorldWin64_Data\Managed\` and the mod's own `Assemblies\`
    folder. An `AssemblyResolve` handler that calls `LoadFrom` inside
    itself recurses into a **StackOverflowException** that kills the
    PowerShell process outright - preload instead, or have the handler
    return only already-loaded assemblies.
  - PowerShell wraps the failure in a `MethodInvocationException`, so
    `$_.Exception.Types` is **null** - walk `.InnerException` down to
    the real `ReflectionTypeLoadException` first. Types whose base class
    failed to load are simply absent from the list, which looks exactly
    like "that type doesn't exist" - check `LoaderExceptions` before
    concluding anything.
- **Mod source beats decompiling the mod.** Vehicle Framework is on
  GitHub with per-version branches - `release/1.5` matches the shipped
  1.5 DLL, while `develop` is 1.6 and has already refactored classes we
  care about (`WorkGiver_CarryToVehicle` became generic there). Use
  `https://api.github.com/repos/<owner>/<repo>/git/trees/<branch>?recursive=1`
  to find file paths, then `raw.githubusercontent.com` for the source.
  Same rule as the RimWorld decompile: **check the branch matches the
  DLL you are actually running.**
- **The github decompile is an OLDER BUILD than our DLL - always
  cross-check signatures by reflection before compiling against them.**
  The decompile has `pawn.story.WorkTagIsDisabled(...)`, but in 1.5 it's
  `pawn.WorkTagIsDisabled(...)` on `Pawn` itself. Same for
  `FirstRespectedReserver`, 3-arg here and 2-arg there. **Bodies and
  control flow are still trustworthy** - that's what confirmed
  `FireCompat`.
- `curl -s https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<Class>.cs`
  returns the full verbatim source and is better than a summarizing
  fetch when you need to diff logic line by line. `Verse.AI` classes sit
  under a literal `Verse.AI/` folder - that's where `HaulAIUtility`
  lives, not `RimWorld/`.
- **Read vanilla's own solution before inventing one.** The menu
  feedback work was mostly reading `AddJobGiverWorkOrders` and copying
  its scoping rules; guessing would have produced a menu full of grey.
- **Grep the Workshop folder for label text** to identify which mod owns
  an unexpected menu entry - `<label>[^<]*harvest[^<]*</label>` across
  `E:\SteamLibrary\steamapps\workshop\content\294100` found the
  harvesting table in seconds. To map folder ids to mod names, read each
  `<id>/About/About.xml` and pull its `<name>` tag.
- **Grep Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` - note
  `WorkGiverDefs\` is a single `WorkGivers.xml`. This is where
  `directOrderable` / `canBeDoneWhileDrafted` on `FightFires` came from,
  and where the absent `scanThings` (hence default true) was confirmed.
- **Generalized lesson, now three times burned:** vanilla WorkGivers put
  real preconditions in `Potential*Global` / `ExtraRequirements`, not in
  `JobOn*`. This mod calls `JobOn*` directly and silently bypasses all
  of them. Firefighting is the third instance and the first where the
  bypass was what we *wanted* - but it still had to be done by hand, in
  `FireCompat`, rather than falling out for free.
- **New generalized lesson from the vehicle report: when an option is
  missing, check whether our patch even ran before theorising about the
  WorkGiver.** A day's worth of plausible WorkGiver explanations was
  available and every one of them was wrong - another mod had suppressed
  the method we postfix. `Selector.HandleMapClicks` and
  `FloatMenuMakerMap.TryMakeFloatMenu` both sit upstream of our two
  entry points, and either can be prefixed away by anything in the
  modlist.

## Workflow notes

- User wants `DoNotBeLazy_Architecture.md` updated after essentially
  every turn - keep doing that. Per CLAUDE.md, doc edit precedes code.
- User tests by manually copying the built DLL into their RimWorld
  `Mods/DoNotBeLazy/` folder and fully restarting the game. **Don't ask
  whether it was copied - look.** Compare
  `DoNotBeLazy/Assemblies/DoNotBeLazy.dll` against
  `E:\SteamLibrary\steamapps\common\RimWorld\Mods\DoNotBeLazy\Assemblies\DoNotBeLazy.dll`;
  `/pull-logs` does this as step 1.
- `gh` CLI is not installed on this machine.
- Commit message style: detailed body explaining *why*. **Only commit
  when explicitly asked.**
- The model plan in architecture doc section 5 assigns Opus to
  `FloatMenuPatch` / `SweepManager` work and Sonnet to the test
  checklist. Reactive playtest bug-fixing was never given a model
  assignment - don't claim it was.

<!-- Inventory of every discrete functional piece of code in the DoNotBeLazy mod. Created 2026-08-29. A lookup table, not a design document - it says what exists and where, never why it should. Rationale lives in DoNotBeLazy_Architecture.md. -->

# DNBL Manifest

Every functional unit in `DoNotBeLazy/Source/DoNotBeLazy/`, by file. A
unit is something that does one job and can be pointed at: a class, a
method, a Harmony patch, or a piece of static state that outlives a
call. Pure data holders are listed once with their fields; they are not
split per property.

**Line numbers are as of 2026-08-29 and will drift.** Names will not -
search by name, treat the line as a hint.

Fourteen `.cs` files, four namespaces, 2,824 lines. Two files
(`SweepManager.cs` 806, `FloatMenuPatch.cs` 758) are 55% of the mod.

---

## Core - `DoNotBeLazy.Core`

### `DoNotBeLazyMod.cs` (35)

| Unit | Line | Does |
|---|---|---|
| `DoNotBeLazyMod : Mod` | 10 | Entry point. RimWorld builds one per load. |
| `HarmonyId` const | 12 | `phildeluca.donotbelazy`. |
| `Settings` static | 14 | The single global settings handle. Everything reads `DoNotBeLazyMod.Settings.x`; there is no other access path. |
| ctor | 16 | Loads settings, runs `harmony.PatchAll()`. |
| `DoSettingsWindowContents` | 25 | Delegates to the settings class. |
| `SettingsCategory` | 30 | The name in Options > Mod Settings. |

### `DoNotBeLazySettings.cs` (115)

| Unit | Line | Does |
|---|---|---|
| `DoNotBeLazySettings : ModSettings` | 8 | Persisted fields: `sweepRadius` (16), `centerOutOrder` (true), `needThreshold` (.05), `moodThreshold` (.10), `showSweepOverlay` (true), `verboseLogging`, `jobDiagnostics`. |
| `ExposeData` | 25 | Scribe round-trip, then pushes both log flags into `Logger` - without that a saved "on" reads off until the window is opened. |
| `DoWindowContents` | 42 | The whole settings UI: three sliders, one two-entry radio group, three checkboxes. Re-runs `PipelineCensus` when job diagnostics is switched on mid-session. |

`centerOutOrder` is the sweep-ordering radio group (added 2026-08-29).
`Listing_Standard.RadioButton` returns true on the *click*, not the
current state, so each entry only writes when picked and neither needs
to clear the other.

**Dead field:** `showSweepOverlay` is persisted and drawn as a checkbox
and read by nothing. No overlay code exists anywhere in the mod.

### `Logger.cs` (51)

| Unit | Line | Does |
|---|---|---|
| `Logger` static class | 12 | Named to dodge `Verse.Log`. |
| `VerboseLogging` flag | 14 | Gates `Message`. Driven from settings, not hardcoded (it sat hardcoded false from Phase 1 to 2026-08-16 and silenced the entire mod). |
| `JobDiagnostics` flag | 19 | Gates `Diag`. A separate switch on purpose. |
| `Message` / `Diag` | 23, 33 | Gated writes, both under the `[DoNotBeLazy] ` prefix so one log extraction catches everything. |
| `Warning` / `Error` | 41, 46 | Ungated. |

---

## Components - `DoNotBeLazy.Components`

### `SweepManager.cs` (806) - the core of the mod

| Unit | Line | Does |
|---|---|---|
| `SweepOrder` | 20 | Data holder for one order: `WorkGiverDef`, `SharedPool` (one List shared by every pawn on the order, so claiming a target removes it for all), `WorkstationTarget`, `ScanCenter`, `ScanRadius`, `CenterOut`, `Requeued` set. |
| `SweepManager : MapComponent` | 81 | One per map. Owns every live sweep. |
| `StateCheckIntervalTicks` | 83 | 60 - how often the tick sanity pass runs. |
| `MaxConsecutiveFailures` | 91 | 8 - failure cutoff per pawn. |
| `MaxPauseTicks` | 96 | 30000 - a need-pause older than this is abandoned. |
| `activeSweeps` | 98 | `Pawn -> SweepOrder`. The authoritative "is this pawn sweeping" map. |
| `consecutiveFailures` | 103 | Per-pawn failure counter. |
| `pausedForNeed` | 113 | `Pawn -> tick paused at`. |
| `AssigningJob` static | 121 | Reentrancy guard. Set while we issue a job so `JobTrackerPatch` ignores the job-end we ourselves caused. |
| `GiveJob` | 128 | The only place a sweep job is issued. Wraps `TryTakeOrderedJob` in the guard. |
| `MapComponentTick` | 141 | Every 60 ticks: drops pawns who died, went down, drafted or left the map, and expires stale pauses. Chaining is *not* done here - it is event-driven. |
| `TryGetActiveSweep` | 159 | Lookup for `JobTrackerPatch`. |
| `GetSweptPawns` | 166 | Enumeration for `NeedMonitor`. |
| `RemoveSweep` | 171 | Cancel. |
| `IsPaused` | 178 | Pause query. |
| `PauseForNeed` | 191 | Marks a pawn paused and ends its current job so vanilla AI takes over. |
| `BeginSweep` | 221 | Entry point from `FloatMenuPatch`. Forks on `WorkGiver_DoBill`. |
| `BeginWorkstationSweep` | 243 | Single best pawn at one bill giver. Empty pool; continuation happens in `JobTrackerPatch`. |
| `RankForWorkstation` | 294 | Skill, then `WorkSpeedGlobal`, then `MoveSpeed`. |
| `SkillLevelOf` | 321 | Null-safe skill read. |
| `BeginAreaSweep` | 331 | Scans a pool around the click, builds the `SweepOrder`, fans every eligible pawn onto it. **The one place `centerOutOrder` is read** - stamped onto the order here and never re-read. Emits a rejection message when the scan finds nothing. |
| `Notify_JobEnded` | 381 | Called by `JobTrackerPatch`. Decides resume-vs-continue-vs-drop, counts failures, handles preparatory jobs and re-queues. |
| `TargetFailureIsRecoverable` | 478 | Which `JobCondition`s are worth retrying. |
| `AssignNextTask` | 485 | Picks a target, asks the WorkGiver for a job on it, issues it. Rescans once per call when the pool runs dry. Only claims a target once a job actually exists for it. |
| `TargetRefusalReason` | 646 | Turns a WorkGiver "no" into a log line. |
| `TargetIsGone` | 710 | Destroyed / despawned check. |
| `AddNewTargets` | 725 | Merges rescan results into the shared pool without duplicating. |
| `NextTargetIndex` | 770 | **The ordering rule.** See below. |

**`NextTargetIndex` is the single point where sweep order is decided.**
Nothing else ranks targets. It runs in one of two modes, stamped onto
the order at click time (`SweepOrder.CenterOut`):

- **Centre-out** (default): rank by distance from `ScanCenter`, with
  distance from the pawn breaking exact ties only.
- **Pawn-nearest**: rank by distance from the pawn, ignoring the click.

Both distances are `LengthHorizontalSquared`, which is `int` on
`IntVec3`, so the tie-break branch is a real equality and actually
fires.

### `NeedMonitor.cs` (121)

| Unit | Line | Does |
|---|---|---|
| `NeedMonitor : GameComponent` | 40 | Auto-instantiated per game. Needs no registration. |
| `CheckIntervalTicks` | 42 | 60. |
| `ResumeMargin` | 49 | 0.05 - resume needs more than the threshold that paused, or a need sitting on the line thrashes. |
| `GameComponentTick` | 55 | Pauses any swept pawn whose food/joy/rest is at or below `needThreshold`, or mood below `moodThreshold`. Skips already-paused pawns. |
| `NeedIsCritical` (x2) | 93, 116 | The four-need check and its per-need helper. |
| `NeedsSatisfied` | 103 | What `SweepManager` asks before resuming. Same four needs, thresholds raised by the margin. |

### `IdleProbe.cs` (81)

| Unit | Line | Does |
|---|---|---|
| `IdleProbe : MapComponent` | 21 | Diagnostic instrument 3. Catches the silent no-job case vanilla never reports. |
| `ProbeIntervalTicks` | 26 | 250. |
| `MapComponentTick` | 32 | Logs every free colonist with `curJob == null`. Off unless `jobDiagnostics`. |
| `Describe` | 53 | Drafted / downed / mental / queue length / hunting priority / thinker present. |

**Deliberate non-behaviour:** it never re-runs WorkGivers to ask why.
Doing so reads and writes the same static state the real scan uses.

---

## Patches - `DoNotBeLazy.Patches`

### `FloatMenuPatch.cs` (758) - the entire player-facing surface

| Unit | Line | Does |
|---|---|---|
| `SupportedWorkTypeDefNames` | 45 | Allowlist of sweepable work types. |
| `ExcludedDefNames` | 79 | Specific WorkGivers pulled back out. |
| `eligibleDefs` cache | 89 | Built once, reused. |
| `MaxFeedbackOptions` | 93 | 3 - cap on greyed-out "why not" entries. |
| `SingleSelect.Postfix` | 96 | Patches `FloatMenuMakerMap.ChoicesAtFor`. |
| `MultiSelect.Postfix` | 110 | Patches `ChoicesAtForMultiSelect`. |
| `AddSweepOptions` | 123 | Shared front door for both patches. |
| `Build` | 140 | The big one. Decides which `*` entries exist for this click, in what state, with what labels. |
| `AllDrafted` | 300 | Gate: suppress everything when the *whole* selection is drafted, so vanilla's auto-take still moves them. |
| `MoveHereOption` | 325 | Rebuilds vanilla's goto entry via `PawnGotoAction` + `BestOrderedGotoDestNear`, added only when we are the reason a multi-select menu became non-empty. Priority `GoHere`, so it sorts on top. |
| `DisabledOption` | 372 | Greyed entry carrying a refusal reason. |
| `AddConsumeOption` | 398 | The group consume entry. |
| `FindIngestibleThing` | 431 | Picks the consumable under the cursor. |
| `CanConsume` | 443 | Per-pawn ingestibility. |
| `ConsumeAll` | 474 | Issues the ingest jobs. |
| `EligibleDefs` | 490 | Builds and caches the WorkGiverDef list. |
| `IsSweepEligible` | 518 | Allowlist + exclusion + scanner-type test. |
| `FindTargetWithJob` | 571 | Does any selected pawn actually have a job here? Returns the target, or the reason and the thing that refused. |
| `WantsSomethingHere` | 683 | Cheap pre-filter before the expensive job test. |
| `FirstRefusal` | 719 | First pawn-level refusal reason, for the greyed entry. |
| `EligiblePawns` | 740 | Selection filtered through `PawnValidator`. |

### `JobTrackerPatch.cs` (95)

| Unit | Line | Does |
|---|---|---|
| `Prefix` | 44 | Captures `curJob` into `__state` before `EndCurrentJob` clears it. Per-call, so nesting is safe. |
| `Postfix` | 49 | Bails on the `AssigningJob` guard. Then: a succeeded `DoBill` re-asks the same scanner for another job at the same bill giver (workstation continuation); everything else goes to `Notify_JobEnded`. |

### `JobSourcePatch.cs` (82)

| Unit | Line | Does |
|---|---|---|
| `IdleJobDefNames` | 33 | The eight idle-shaped job defs worth logging. |
| `Postfix` | 45 | Diagnostic instrument 2, on `StartJob`. Logs colonist idle jobs with the issuing ThinkNode and its assembly - that assembly names the responsible mod. Unthrottled on purpose; wrapped in try/catch so a diagnostic can never break the game. |

---

## Utility - `DoNotBeLazy.Utility`

### `TaskScanner.cs` (229)

| Unit | Line | Does |
|---|---|---|
| `TargetIsBurning` | 39 | Per-target fire filter. Diverges from vanilla, which skips a whole grow zone. |
| `FindTargets` | 50 | The radial scan. Public entry; picks cells vs things by scanner type. |
| `ScanCells` | 92 | Cell-based WorkGivers. Applies the `GrowerCompat` reset and sow-settings check per cell. |
| `ScanThings` | 156 | Thing-based WorkGivers. Falls back when `PotentialWorkThingsGlobal` returns null on the base class. |

### `PawnValidator.cs` (117)

| Unit | Line | Does |
|---|---|---|
| `Refusal` enum | 19 | Nine reasons a pawn is turned away. |
| `CanSweep` | 32 | Yes/no. Used per-tick. |
| `RefusalReason` | 43 | Words, for the menu. Hardcoded English, not vanilla's keys. |
| `Check` | 71 | The actual test. Downed, mental, drafted, never-works, type disabled, unassigned, no manipulation. |

### `GrowerCompat.cs` (126)

Exists because this mod calls `JobOnCell`/`HasJobOnCell` directly
instead of enumerating `PotentialWorkCellsGlobal`, so the setup vanilla
does inside that enumerator has to be done by hand.

| Unit | Line | Does |
|---|---|---|
| `WantedPlantDef` ref | 39 | Cached `FieldRef` to `WorkGiver_Grower.wantedPlantDef`. |
| `BuildWantedPlantDefRef` | 41 | Builds it once. |
| `ResetWantedPlantDef` | 58 | Clears the shared static before every grower call. |
| `SowSettingsAllow` | 77 | Reimplements `WorkGiver_GrowerSow.ExtraRequirements`. Sow only. |
| `CanReachTarget` | 108 | Reachability the framework normally supplies. |
| `IsPreparatoryJob` | 121 | "Do this other thing first" detection, so blocker work is not mistaken for the target being finished. |

### `FireCompat.cs` (117)

| Unit | Line | Does |
|---|---|---|
| `ReserveCheckDistSquared` | 31 | 225. |
| `HandledDistSquared` | 35 | 25. |
| `IsFirefighting` | 39 | WorkGiver identity test, checked first by three other call sites. |
| `HasFireJob` | 48 | Stands in for `WorkGiver_FightFires.HasJobOnThing`, **minus the home-area restriction** - a deliberate override, on the grounds that the click is the authorisation. |
| `FireIsBeingHandled` | 99 | Is someone else already on this fire. |

### `PipelineCensus.cs` (91)

| Unit | Line | Does |
|---|---|---|
| `alreadyRan` | 25 | Runs at most twice per session. |
| `Run` | 27 | Diagnostic instrument 1: lists every Harmony patch on the job pipeline and which assembly owns it. |
| `Report` | 42 | Per-method reporting. |
| `Collect` | 67 | Gathers prefix/postfix/transpiler lists. |
| `PipelineCensusStartup` | 84 | `[StaticConstructorOnStartup]`, so it fires after every mod's constructor has run. |

---

## Non-code

| File | Does |
|---|---|
| `DoNotBeLazy/About/About.xml` | Mod metadata for the RimWorld launcher. |
| `DoNotBeLazy/Source/DoNotBeLazy/DoNotBeLazy.csproj` | References local DLLs in `../../../lib` (never NuGet); `OutputPath` writes straight to `DoNotBeLazy/Assemblies/`. |

**No XML defs.** The mod ships no `Defs/` folder - everything is code.
That matters for anything wanting a `KeyBindingDef`, which would be the
first XML in the project.

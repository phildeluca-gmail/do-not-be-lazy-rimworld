---
description: Extract, archive and summarize the RimWorld log for this mod
---

Pull the current RimWorld log, archive it, and tell me what's in it.
Do all of this yourself - do not hand the user commands to run.

`$ARGUMENTS`, if present, is what to look for (e.g. "standing still",
"sow", "the fire sweep"). Weight the summary toward it. With no
arguments, summarize everything notable.

## 1. Check the build actually under test, first

RimWorld runs the copy in the game folder, not the one in this repo.
Compare the three timestamps and say plainly which build produced the
log **before** interpreting a single line of it:

```powershell
$log  = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
$game = "E:\SteamLibrary\steamapps\common\RimWorld\Mods\DoNotBeLazy\Assemblies\DoNotBeLazy.dll"
$built = "C:\Users\ninja\OneDrive\Claude Projects\RimWorld-DoNotBeLazy\DoNotBeLazy\Assemblies\DoNotBeLazy.dll"
foreach ($f in @($log, $game, $built)) { "{0}  {1}" -f (Get-Item $f).LastWriteTime, $f }
```

- Installed DLL older than the built one → the log does **not** contain
  today's changes. Say so up front; it invalidates most conclusions.
- Log older than the installed DLL → the game hasn't been restarted
  since the copy.

This check exists because "was the new DLL actually in the game folder"
has cost this project time more than once.

## 2. Extract

`Player.log` is UTF-16-ish and greps as binary - use `Select-String`,
not `grep`. RimWorld truncates it on launch, so archive before anything
else.

```powershell
$log = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = "C:\Users\ninja\OneDrive\Claude Projects\RimWorld-DoNotBeLazy\logs"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir | Out-Null }
Select-String -Path $log -Pattern '\[DoNotBeLazy\]' | ForEach-Object { $_.Line } | Out-File "$dir\$stamp-dnbl.log" -Encoding utf8
Select-String -Path $log -Pattern 'Exception|WorkGiver|error recover|threw exception|jobs in 10 ticks|Could not resolve|MissingMethod|HarmonyException|^\s+at [A-Za-z]|Existing reservers|Could not reserve' | ForEach-Object { "$($_.LineNumber): $($_.Line)" } | Out-File "$dir\$stamp-errors.log" -Encoding utf8
Select-String -Path $log -Pattern 'with mods:' -Context 0,80 | Out-File "$dir\$stamp-mods.log" -Encoding utf8
"dnbl: $((Get-Content "$dir\$stamp-dnbl.log").Count)  errors: $((Get-Content "$dir\$stamp-errors.log").Count)"
```

`logs/` is gitignored. Never delete an older pull - comparing against
the previous one is half the value.

**Why `^\s+at [A-Za-z]` is in that pattern:** an exception line names the
*type* thrown, never the mod. The owning assembly is only in the ` at
Namespace.Class.Method ()` frames underneath it, and the first version of
this command didn't match those - so the 2026-08-22 pull archived 232
`MissingMethodException` lines with no way to tell whose they were, and
naming Automatic Hunting meant going back to the raw `Player.log` by hand.
RimWorld also collapses repeats to `[Ref ABCD1234] Duplicate stacktrace,
see ref for original`, so **the frames appear exactly once, on the first
occurrence** - grep the ref id to count the rest.

`Could not reserve` / `Existing reservers` are in there for a different
reason: they are the direct evidence for reservation contention between
pawns, which is what makes a shared sweep pool fall apart. They name the
holding pawn and its job.

## 3. Read the three files and report

Read what you saved (they're plain UTF-8) rather than re-parsing
`Player.log`. Do **not** paste the logs back - the user has said so
repeatedly. Report:

- **Build under test** - from step 1, stated first.
- **Sweep trace** (`BeginSweep` / `scan` / job lines): which sweeps ran,
  pool sizes, how many pawns were assigned, whether any ended early.
  A `BeginSweep ... N targets, 1 pawns` when several were selected is
  worth calling out.
- **Job diagnostics**, when the setting was on:
  - `pipeline ...` lines - which mods patch the job pipeline. TKS
    Priority Treatment is expected here; anything else is news.
  - `idle <pawn>: no job - ...` - the silent case. Note whether the
    same pawn appears across several probes (genuinely stuck) or once
    (just between jobs).
  - `job <pawn>: <def> from <node> [<assembly>]` - if these are looping
    for a pawn, the think tree is alive and `JobGiver_Work` is declining
    the work. If an idle pawn has **no** job lines at all, that's
    `DetermineNextJob` returning NoJob. Different causes, say which.
  - An assembly other than `Assembly-CSharp` in a `job` line names the
    mod outright. Lead with it if it's there.
- **Vanilla errors**, grouped by the mod that owns them, with counts
  rather than repetitions. `Exception in WorkGiver X` names its own
  culprit; attribute it to the assembly in the stack trace, **not** to
  whichever mod sounds related to the symptom. (That mistake cost three
  sessions on the Sense of Urgency / Automatic Hunting mix-up.)
  - Before blaming a throwing mod for a symptom, check **where** it
    throws. Vanilla wraps a lot of things in its own try/catch:
    `GameComponentUtility.GameComponentTick` catches per component,
    `JobGiver_Work.TryIssueJobPackage` catches per WorkGiver, and
    `DetermineNextJob` routes think-tree throws to
    `TryStartErrorRecoverJob`. A mod throwing inside one of those is
    broken and loud, but it is contained - it cannot be the cause of a
    pawn standing still. Read the decompile and say which it is.
- **Reservation contention** (`Could not reserve ... Existing reservers`):
  count them and name the jobs involved. Heavy contention on haul
  destinations is what empties a shared sweep pool.
- **What's absent that should be there** - no `[DoNotBeLazy]` lines at
  all usually means the verbose checkbox didn't persist, or the DLL is
  stale. Check the float-menu path is untraced before concluding a menu
  bug produced nothing.

Then say what you'd do next, in one or two sentences.

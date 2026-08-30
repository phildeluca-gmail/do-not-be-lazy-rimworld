# CLAUDE.md - Do Not Be Lazy (RimWorld 1.5 Mod)

**The shared rules live one level up**, in `Rimworld Mods\CLAUDE.md` - repo layout, build paths, the never-commit-DLLs rule, git, the C#/RimWorld coding overrides, and how an idea becomes code. Read that first. This file covers only what is specific to Do Not Be Lazy.

Prefer opening Claude Code in the umbrella folder rather than here, so both files are in scope.

---

## Referenced documents

Load these before any implementation work in this mod.

- `..\NEXT_SESSION.md` - **Read this first.** Session pickup: what is and is not committed, what is and is not verified in a real game, open bugs, the log-extraction workflow, and traps that have already cost time once. Lives at umbrella level because it covers all the mods.
- `.\DoNotBeLazy_Architecture.md` - intent, core behaviours, component structure, edge cases, execution plan. **Re-read the relevant section before any architectural decision, new Harmony patch, new file, or change to the component structure** - and before touching the float menu patch, sweep manager, task scanner, or need monitor, even if you have read it already this session.
- `.\DNBL-manifest.md` - inventory of every discrete functional unit in this mod, by file, with line numbers. Read it before grepping the tree or adding something that may already exist. **Update it in the same change that adds, removes or renames a unit** - a stale manifest is worse than none.
- `.\TEST_PLAN.md` - the playtest plan. Phases 7 and 8 are the outstanding ones.
- `..\human-style-coding-260327.md` - the shared coding guide.

Also at umbrella level, not required reading: `..\RW-Wishlist.md` (idea capture; nothing in it is a go-ahead).

---

## Mod-specific notes

- **This is the only mod here with real behaviour.** It is played in a ~60-mod save, so an odd menu entry or a job that dies on arrival may be a legitimate mod interaction rather than a bug here. Check the modlist notes in `..\NEXT_SESSION.md` before assuming.
- **The installed DLL is the one the game loads, not the repo build.** Compare hashes before believing any claim about which build is installed - that sentence has been wrong three times:
  ```
  md5sum DoNotBeLazy/Assemblies/DoNotBeLazy.dll "E:/SteamLibrary/steamapps/common/RimWorld/Mods/DoNotBeLazy/Assemblies/DoNotBeLazy.dll"
  ```
- **Verbose logging must be on for any log to mean anything.** `Logger.Message` was a no-op for six weeks and several playtests were misread as "nothing fired". An absence of `[DoNotBeLazy]` lines proves nothing unless the setting was on.
- **`/pull-logs`** extracts, archives and summarises the RimWorld log for this mod. Use it rather than pasting whole logs.

---

## Conflict resolution

If `DoNotBeLazy_Architecture.md` and the shared coding guide appear to conflict, stop and ask. Do not resolve it yourself - state the conflict and wait.

If a task falls outside the scope of both, ask before assuming.

---

## Document updates

The architecture doc and the coding guide are living documents. When a change requires updating one, **make the edit and confirm it before implementing any code that depends on it.**

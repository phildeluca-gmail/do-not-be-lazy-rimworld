<!-- Updated: 2026-08-13 20:22 EDT -->

# CLAUDE.md - Do Not Be Lazy (RimWorld 1.5 Mod)

This file governs how Claude Code operates in this project. Read it fully before taking any action.

---

## This repo is multi-mod as of 2026-08-29

It is still named after Do Not Be Lazy, and Do Not Be Lazy is still the only mod with any behaviour in it. But five more now sit beside it at the top level, each as a buildable scaffold that loads and does nothing:

`HighlightCorpsesWithTech/`, `NotifyRipe/`, `NotifyStillBeingAttacked/`, `UninstallHotkey/`, `MenuHotkeys/`

**None of the five has agreed behaviour.** Each has a `<Name>_Architecture.md` stub at the repo root holding the verbatim ask and the questions still open. They were ordered as folders and explicitly not as behaviour. **Do not implement any of them without a fresh go-ahead**, and when one is ordered, answer its stub's section 3 questions before writing code.

Layout for every mod, including new ones:

```
<Name>/
  About/About.xml
  Source/<Name>/<Name>.csproj      <- refs ../../../lib, OutputPath ../../Assemblies/
  Source/<Name>/Core/<Name>Mod.cs
<Name>_Architecture.md             <- repo root, beside the others
```

`lib/` is shared by all of them and is gitignored. Build output is ignored via `*/Assemblies/*.dll`, so a new mod folder is covered automatically. A scaffold needs `/t:Restore,Rebuild` on its first build; the assets file does not exist yet. This adds no NuGet packages and does not relax the no-NuGet rule below.

---

## Referenced Documents

Load all referenced documents at the start of every session and before any implementation work begins.

- `./NEXT_SESSION.md` - Session pickup file. **Read this first, before the other two.** Fast orientation on current state: what is and is not committed, what is and is not verified in game, open bugs, the log-extraction workflow, and traps that have already cost time once. Rewritten at the end of most sessions. The `claude --resume <id>` command for the most recent conversation sits at the very top of that file, with older session ids listed below it - resume rather than starting fresh if a session was cut short. Current session: `7254dd70-3fd5-4c55-a0f6-fcab3652315a`.
- `./DoNotBeLazy_Architecture.md` - Mod architecture document. Defines intent, core behaviors, component structure, edge cases, Claude Code execution plan with model assignments, and dependency setup.
- `./human-style-coding-260327.md` - Human-style coding guide. Defines comment style, naming conventions, abstraction rules, and stylistic drift. Written for JS/Python examples but applies to C# with the overrides below.

Not required session reading, but know it exists:

- `./DNBL-manifest.md` - Inventory of every discrete functional piece of code in the Do Not Be Lazy mod, by file, with line numbers and one line on what each unit does. A lookup table for "where does X live" and "what already exists" - read it before grepping the whole tree, and before adding anything that might duplicate something already there. It records what exists, never why; rationale stays in the architecture doc. Line numbers drift, names do not. **Update it in the same change that adds, removes or renames a functional unit** - a stale manifest is worse than none.
- `./RW-Wishlist.md` - Capture file for RimWorld mod ideas that have not been architected. **Nothing in it is a go-ahead.** An entry graduates by being written up in the relevant architecture document first. Read it when asked about future work, or before proposing something that might already be parked there.

---

## When to Consult Each Document

**At the start of every session**, read `./NEXT_SESSION.md` before anything else. It is the only document that states whether the working tree is committed and whether the current code has been tested in a real game - assume neither without checking it.

**Before writing any code**, read all three documents above.

**Before making architectural decisions, adding Harmony patches, creating new files, or changing the component structure**, re-read the relevant section of `./DoNotBeLazy_Architecture.md`.

**Before working on the float menu patch, sweep manager, task scanner, or need monitor**, re-read the component description and edge cases even if you have read them already in this session.

---

## C# and RimWorld Overrides to Coding Guide

The human-style coding guide applies with these modifications:

- This is C# targeting .NET Framework 4.7.2. Use C# conventions (PascalCase for methods/properties, camelCase for locals/params).
- RimWorld modding works against a decompiled API with no official documentation. Comments explaining what a RimWorld method actually does ARE valuable here. The "don't comment the what" rule is relaxed for Verse/RimWorld API calls.
- Harmony patch classes should have a brief comment stating which method they patch and why, since the attribute annotations alone can be cryptic.
- Use `Verse` and `RimWorld` namespaces. Do not alias them.
- Prefer Harmony postfix patches. Do not use transpilers unless the architecture doc explicitly calls for one (it currently does not).

---

## Model Assignments

Follow the model plan in Section 5 of `./DoNotBeLazy_Architecture.md`. Switch models using `/model haiku`, `/model sonnet`, or `/model opus` as specified per phase. Do not use a more expensive model than the plan calls for unless the task fails or produces errors at the assigned tier.

---

## Conflict Resolution

If instructions in `./DoNotBeLazy_Architecture.md` and `./human-style-coding-260327.md` appear to conflict, stop and ask before proceeding. Do not resolve the conflict yourself. State the conflict clearly and wait for a decision.

If a task falls outside the scope of both documents, ask before making assumptions.

---

## General Behavior

- **Keep chat replies brief where possible.** Short answers, no restating the question, no padding around a result. Go deeper when asked, or when a finding genuinely needs the detail. This governs conversation only - the docs in this repo stay dense on purpose.
- Do not add Harmony patches, files, or dependencies not described in `./DoNotBeLazy_Architecture.md` unless explicitly instructed.
- Do not add NuGet packages. All dependencies are local DLLs in `./lib/` (see Section 6.0 of the architecture doc).
- Do not deviate from the code style in `./human-style-coding-260327.md` for reasons of preference or convention.
- When in doubt about scope or approach, ask. A short clarifying question is better than an incorrect assumption.
- Do not silently fix or normalize code you did not write in this session.

---

## Git

Repository setup is manual. The developer creates the repo on GitHub and runs `git init` / `git remote add` before Claude Code handles commits.

For commits during a session, use:
```
git add -A && git commit -m "message" && git push
```

---

## Document Updates

Both referenced documents are living documents and will be updated during development. When asked to implement a change that requires updating `./DoNotBeLazy_Architecture.md` or `./human-style-coding-260327.md`, make the edit to the document and confirm the change before implementing any code that depends on it.

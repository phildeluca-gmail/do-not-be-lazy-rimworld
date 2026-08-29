<!-- Architecture stub for Menu Hotkeys. Created 2026-08-29 alongside the scaffold. Holds the verbatim ask and nothing agreed. NOTHING HERE IS A GO-AHEAD TO BUILD BEHAVIOUR. -->

# Menu Hotkeys - Architecture

**Status: scaffold only. No behaviour is designed, costed or agreed.**

The folder `MenuHotkeys/` exists, builds clean, and loads in RimWorld printing
one line. That is the whole of it. This document exists so the ask is
recorded somewhere better than a wishlist bullet, and so this mod can
graduate the way CLAUDE.md requires - by being written up here first.

**Ordered:** 2026-08-27, as one of five names in a single instruction.
**Scaffolded:** 2026-08-29.

---

## 1. The ask, verbatim

> "New mods coming, just build separate folders for each: Highlight
> Corpses With Tech; Notify Ripe (options: Ambrosia, Berries); Notify
> Still Being Attacked; Uninstall Hotkey; Menu Hotkeys."

**That instruction was a go-ahead for folders and explicitly not for
behaviour.** Everything in section 2 below is a reading of the name, not
a specification. Do not build from it.

---

## 2. What the name appears to mean - A GUESS

Keyboard access to menu entries generally.

**This overlaps wishlist entry 1, `ConfigureKeys - interface changes`,
and may be the same idea restated.** Entry 1 second reading was "a
separate mod about configuring keys" - which is exactly what a mod
called Menu Hotkeys would be. **Settle entry 1 and this one together;
do not design either in isolation.**

Which menu is also unstated. At least three candidates:

- The **right-click float menu** - the surface Do Not Be Lazy already
  patches most heavily. Number keys to pick the Nth entry would be the
  obvious shape.
- The **architect menu** - vanilla already has some bindings here.
- **Mod settings and options windows** generally.

---

## 3. Questions to settle before this becomes a plan

- **Is this the same idea as wishlist entry 1?** If yes the two collapse
  into one project and entry 1 questions apply here. Answer this first.
- Which menu? Float menu, architect menu, or all menus?
- Fixed keys (1-9 pick the Nth entry) or player-configurable bindings
  per entry? The second is a much larger mod.
- If the float menu: does this belong in Do Not Be Lazy instead, given
  that mod already owns the float-menu patches and a second mod patching
  the same surface invites conflicts?
- How does it interact with `Uninstall Hotkey` above - is that mod just
  one instance of this one?

---

## 4. What exists now

```
MenuHotkeys/
  About/About.xml            <- packageId phildeluca.menuhotkeys, no dependencies declared yet
  Source/MenuHotkeys/
    MenuHotkeys.csproj            <- net472, refs ../../../lib, outputs to MenuHotkeys/Assemblies/
    Core/MenuHotkeysMod.cs        <- Mod subclass, one log line, nothing else
```

Add the Harmony `modDependencies` block to `About.xml` (copy it from
`DoNotBeLazy/About/About.xml`) if and when this mod actually patches
something.

**No `Defs/` folder.** Anything needing a `KeyBindingDef`, `LetterDef`
or similar creates the first XML in this mod.

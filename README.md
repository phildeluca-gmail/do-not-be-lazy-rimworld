# Do Not Be Lazy - Install Guide

Status as of this writing: builds clean and has been played, but the current build is **not yet tested in a running game** - the 2026-08-22 evening sweep-pool fixes have never run. Install at your own risk on a save you don't mind backing up first.

## Requirements

- RimWorld 1.5
- The [Harmony](steam://url/CommunityFilePage/2009463077) mod, subscribed via Steam Workshop (this mod depends on it and won't load without it)

## 1. Get the mod files

The compiled DLL is already built at `DoNotBeLazy/Assemblies/DoNotBeLazy.dll` in this repo (it's gitignored, so it won't be there on a fresh clone - see "Building from source" below if you need to rebuild it).

You need the whole `DoNotBeLazy/` folder - not the repo root. It should look like this:

```
DoNotBeLazy/
  About/
    About.xml
  Assemblies/
    DoNotBeLazy.dll
```

## 2. Copy it into RimWorld's Mods folder

Copy (or symlink) the entire `DoNotBeLazy/` folder into your RimWorld `Mods` directory:

```
<RimWorld install>/Mods/DoNotBeLazy/
```

On a typical Windows Steam install, that's:

```
C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\DoNotBeLazy\
```

(adjust the drive/library path if your Steam library is elsewhere - this repo builds against a copy at `E:\SteamLibrary\steamapps\common\RimWorld`, yours may differ)

## 3. Enable it and set load order

In RimWorld's mod list:

1. Enable **Harmony**
2. Enable **Core** (should already be on)
3. Enable **Do Not Be Lazy**, placed *after* Harmony

The mod's `About.xml` already declares Harmony as a dependency and sets `loadAfter`, so RimWorld should sort this automatically - just make sure Harmony is enabled at all.

## 4. What it does

Select one or more pawns, right-click a valid target, and an asterisked (`*`) option appears below the normal float menu entries for actions that support area-sweep (hauling, construction, workstation bills, cleaning, mining). Choosing it sends all eligible selected pawns to do that type of task repeatedly within a radius of the click, until nothing's left or a pawn's hunger/rest/recreation need drops critically low.

Settings (Options → Mod Settings → Do Not Be Lazy):

- **Sweep radius** - how far from the click point to search for matching tasks (default 16 tiles)
- **Need interrupt threshold** - how low a need has to drop before a pawn is pulled out of the sweep (default 5%)
- **Show sweep radius overlay on hover** - currently has no effect; the setting exists but the overlay itself hasn't been built yet

## Building from source

If `DoNotBeLazy/Assemblies/DoNotBeLazy.dll` is missing (e.g. fresh clone):

1. Put the four required DLLs in `lib/` (see `DoNotBeLazy_Architecture.md` section 6 for exactly which ones and where to find them - they come from your RimWorld install and the Harmony Workshop mod, and aren't included in this repo since they're not ours to redistribute).
2. From `DoNotBeLazy/Source/DoNotBeLazy/`, run:
   ```
   dotnet build
   ```
   This drops `DoNotBeLazy.dll` into `DoNotBeLazy/Assemblies/` directly.

## Known gaps in the current build

- Not yet verified in an actual running game - see the caveat at the top.
- Right-click sweep options require RimWorld's own float menu to already offer a normal action on whatever you clicked (i.e. you're clicking a haulable, a bill-giving workstation, a mineable rock, etc.) - clicking empty ground won't produce a sweep option.
- The overlay setting mentioned above doesn't do anything yet.

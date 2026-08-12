# NuclearOption-Target-Group-Recall-and-Missile-Defense

A controller-friendly Nuclear Option mod that lets you save target groups, switch targets, and recall a saved group with one button. It can also quickly target incoming missiles without losing your previous target group.

## What it does

- Save and recall up to two target groups.
- Quickly restore a group after changing or clearing your targets.
- Target all detected incoming missiles with one button hold.
- Sort incoming missiles nearest first, making the closest missile your primary target.
- Save your current targets to Group 1 before switching to incoming missiles. This is enabled by default.
- Skip destroyed or unavailable targets when recalling a group.
- Clear saved groups when a new mission starts.

If no incoming missiles are detected, your current targets and saved groups are not changed.

## Installation

1. Install BepInEx 5 for Nuclear Option.
2. Download the latest ZIP from the GitHub Releases page.
3. Extract the ZIP into your Nuclear Option game folder.
4. Check that the mod DLL is located here:

   `Nuclear Option/BepInEx/plugins/NuclearOption-Target-Group-Recall-and-Missile-Defense/NuclearOption-Target-Group-Recall-and-Missile-Defense.dll`

5. Start or restart the game.

## Default controls

### Target Group 1

- Keyboard: `L`
- Controller: D-pad Left
- Quick press: recall the saved group
- Hold for 0.4 seconds: save the currently selected targets

D-pad Left still opens the normal Aircraft Systems menu while held.

### Target Group 2

Group 2 is disabled and unbound by default. It can be enabled and assigned a button in the configuration file.

### Missile Defense

- Keyboard: `K`
- Controller: D-pad Up
- Hold for 0.25 seconds: target all detected incoming missiles

A quick D-pad Up press still changes your selected countermeasure normally. Holding it for 0.25 seconds activates Missile Defense instead, so the two actions do not overlap.

Before Missile Defense changes your targets, the mod saves your current non-missile targets to Group 1 by default. Recall Group 1 to return to them.

## Configuration

Start the game once with the mod installed, then edit:

`Nuclear Option/BepInEx/config/nuclearoption.targetgrouprecallandmissiledefense.cfg`

You can change the buttons, hold times, enable Group 2, disable Group 1 auto-save, or turn the mod off.

## NO Tactitools compatibility

NO Tactitools has its own target-group controls. When both mods are installed, this mod blocks only NO Tactitools' duplicate save and recall actions by default. Its other target controls continue to work.

## Building from source

Place the repository folder directly inside the Nuclear Option game folder, then run:

```powershell
dotnet restore .\NuclearOption-Target-Group-Recall-and-Missile-Defense.csproj --ignore-failed-sources
dotnet build .\NuclearOption-Target-Group-Recall-and-Missile-Defense.csproj -c Release --no-restore
```

The project builds against the game and BepInEx files already installed on your computer. It does not download any code packages.

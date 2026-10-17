# Slopbox

A physics sandbox, vibe coded almost entirely with AI on the [Prowl](https://github.com/ProwlEngine/Prowl) engine. If you've played Garry's Mod you already know what this is: spawn props, weld them together, strap thrusters on, blow it all up.

It's slop. It's a box. Have fun.

[![Slopbox showcase](https://img.youtube.com/vi/9lswUuOXsS4/maxresdefault.jpg)](https://www.youtube.com/watch?v=9lswUuOXsS4)

## What's in it

**Equipment**

- **Physics Gun**: pick up, move, rotate and freeze physics objects.
- **Gravity Gun**: pull, carry and launch physics objects.
- **Tool Gun**: runs whichever tool you've picked in the spawn menu.
- **Rocket Launcher**: explosive rockets that unfreeze and scatter nearby props.

**Tools**

| Category | Tools |
| --- | --- |
| Construction | Remover, Physical Properties, Duplicator, Scale, Thruster |
| Constraints | Weld, Ball Socket, Rope, Joint |
| Render | Colour, Material, Light |

The Duplicator saves contraptions as `.dupe` files. When running in the Prowl editor they are read from and saved to `dupes/` in the project root, which already holds Compound and the 2000-prop Rainbow Fortress. A built game uses `%AppData%/Slopbox/dupes` instead.

**Props**: blocks, plates and round shapes built in, plus every model and map from the Valve games you have installed (see [Mounting games](#mounting-games)).

## Mounting games

Slopbox borrows content from Steam games you already own, the way Garry's Mod does. On start it finds each supported game in your Steam libraries (by reading `libraryfolders.vdf` and the app manifests, so Steam doesn't need to be running) and mounts it on a background thread. Everything it finds shows up under the **Mounts** tab of the spawn menu, grouped by game. Nothing is copied into the project.

| Engine | Games |
| --- | --- |
| GoldSrc | Half-Life, Opposing Force, Blue Shift, Counter-Strike, Condition Zero, Ricochet, Team Fortress Classic, Deathmatch Classic, Day of Defeat, Sven Co-op, Cry of Fear |
| Source | Half-Life 2 (with Episodes One and Two and Lost Coast), Counter-Strike: Source, Half-Life 2: Deathmatch, Garry's Mod, Portal, Team Fortress 2 |

What you get from a mounted game:

- **Models** (`.mdl`): spawn as physics props with their textures, skins and animations.
- **Maps** (`.bsp`): spawn as one static object you can build on, with lighting, the props and models the map places, and spawn points. Source maps also play their ambient sounds and soundscapes.
- **Materials and sounds**: used by models and maps. Walking on a Source map plays that game's footsteps for each surface.

Source games read their VPKs and loose files in the same order the engine does. Games built on Half-Life 2, such as Counter-Strike: Source, also read its `hl2` folder for shared content.

**Custom maps**: put Source `.bsp` files in `maps/` in the project root (or `%AppData%/Slopbox/maps` in a built game). They're listed with Half-Life 2 and can use content from every mounted Source game. `rpw_downtown_v2.bsp` is there already.

To add a game, subclass `GoldSrc.GameMount` or `Source.SourceMount` in `Assets/code/Mounting/` with its Steam app ID and game folders. Every mount type is picked up automatically. The mounting system is ported from s&box's `Sandbox.Mounting` (MIT).

## Controls

| Key | Action |
| --- | --- |
| W A S D | Move |
| Space / Ctrl | Jump / duck |
| Shift / Alt | Sprint / walk |
| V | Noclip |
| Mouse 1 / Mouse 2 | Primary / secondary fire |
| R | Reload (tool-specific action) |
| E | Use |
| F | Flashlight |
| Z | Undo |
| Q | Spawn menu |
| 1–9 | Equipment slots |
| F1 | Toggle HUD |
| Esc | Pause menu and settings |

Bindings live in `Assets/code/Core/Controls.cs`.

## Running it

1. Build or download the Prowl editor.
2. Open this folder as a project. The editor generates `Slopbox.Game.csproj` and `Slopbox.Editor.csproj` itself.
3. Open `Assets/scenes/flatgrass.scene` and press Play.

## Folder structure

```
slopbox/
├── slopbox.prowl          Prowl project file
├── dupes/                 Duplicator saves used when running in the editor
├── maps/                  Custom Source maps, listed with Half-Life 2
├── Assets/
│   ├── code/              All game code (C#, namespace Slopbox)
│   │   ├── Core/          GameManager, controls, settings, audio, undo, snapshots, dupes
│   │   ├── Player/        Player, camera, flashlight, inventory; Movement/ holds walk and noclip modes
│   │   ├── Equipment/     Physics gun, gravity gun, tool gun, rocket launcher, view models
│   │   ├── Tools/         Tool gun tools (BaseTool + one class per tool) and the tool registry
│   │   ├── Props/         Prop component, spawn catalog, material catalog, light and thruster props
│   │   ├── Mounting/      Steam game mounting; GoldSrc/ and Source/ read each engine's models, maps, textures and sounds
│   │   └── UI/            HUD, spawn menu, pause menu, theme tokens, icons (Paper UI)
│   ├── sounds/            The game's own sounds (CC0), referenced from GameManager.Sounds; sources in SOURCES.json
│   ├── fonts/             Geist Black for titles
│   ├── models/            Player model and animations
│   ├── scenes/            flatgrass.scene, the only built-in map
│   ├── prefabs/
├── ProjectSettings/       Engine settings (physics, audio, layers, build)
├── .claude/skills/        Prowl reference guides for AI agents
├── AGENTS.md / CLAUDE.md  Instructions for AI agents (generated by Prowl)
└── Library/ Temp/ Logs/ Backups/ Builds/ obj/   Editor-generated, not source
```

`GameManager` is the root of everything. A scene only needs that one component; it builds the player, HUD, spawn catalog, tool registry and undo history in code.

To add a tool, create a class in `Assets/code/Tools/` that extends `BaseTool` and tag it with `[Tool("Name", "Description", Category = "...")]`. The registry finds it automatically.

## Working on it with AI

That's the whole point. While the Prowl editor has the project open, agents can drive it through `Library/prowl` to compile, enter play mode, evaluate C#, read logs and take screenshots. See `AGENTS.md` and `.claude/skills/`.

## License

The code is under the [MIT License](LICENSE). The sounds in `Assets/sounds/` are CC0 (mostly [Kenney](https://kenney.nl)), and `SOURCES.json` lists where each came from. The Geist font is under the SIL Open Font License (`Assets/fonts/Geist-OFL.txt`). Content from mounted games is read from your own installs and stays under its owners' terms.

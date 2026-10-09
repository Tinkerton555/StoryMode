# Story Mode

A Dalamud plugin for Final Fantasy XIV that keeps other players' glamours from breaking the story.

On your screen only, other players appear in gear that fits the world:

- **Job stone equipped:** that job's artifact gear and weapon for their level bracket (30–59 → the level 50 set, 60–69 → the level 60 set, and so on). Jobs that start above level 30 use their unlock gear until their first artifact set.
- **No job stone:** a starter outfit themed to their class, so job gear always means a job stone.
- **Crafters and gatherers:** a starter outfit below 50, then their job's own scrip gear for each bracket.

Nothing is sent to the server. Other players see no change, and your own appearance is untouched unless you choose otherwise.

## Settings

Open with `/storymode config`, or toggle the plugin on and off with `/storymode`.

- **Who to change:** all other players, all except your party, or all except your party and alliance. Optionally skip friends and your free company, or include yourself.
- **When:** everywhere, or only during cutscenes and/or duties.
- **What to replace:** weapons, accessories and facewear can each be switched on or off.

## Installing

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add:
   `https://raw.githubusercontent.com/OWNER/StoryMode/main/repo.json`
3. Tick the checkbox, save, and install **Story Mode** from `/xlplugins`.

## Gear table

`StoryMode/Data/gear-table.json` lists the item used for each job, level bracket and slot. It is generated from the game's own data by `Tools/GearTableGenerator`:

```
dotnet run --project Tools/GearTableGenerator -- "<game folder>\game\sqpack"
```

If a set looks wrong, please open an issue naming the job and level.

## License

Story Mode is licensed under the [GNU Affero General Public License v3.0](LICENSE). You're free to use, change and share it, as long as anything you share based on it is released under the same license with its source.

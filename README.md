# Story Mode

A Dalamud plugin that puts other players in their job's artifact gear, so glamours don't ruin the story.

- Players with a job stone wear the artifact gear for their level (level 30-59 get the level 50 set, 60-69 the level 60 set, and so on).
- Players without a job stone wear a basic starter outfit.

## Install

1. Type `/xlsettings` in game and open the **Experimental** tab.
2. Paste this into **Custom Plugin Repositories** and click **+**:
   ```
   https://raw.githubusercontent.com/Tinkerton555/StoryMode/main/repo.json
   ```
3. Click **Save**.
4. Type `/xlplugins`, search for **Story Mode** and install it.

## Use

- `/storymode` turns it on or off.
- `/storymode config` opens the settings, where you can choose who gets changed (everyone, or everyone except your party, friends or FC) and when (always, or only in cutscenes or duties).

By default it's on everywhere, changes everyone except you and your party, and also swaps weapons, removes accessories and hides facewear.

## Problems

If a job is wearing the wrong gear, open an issue with the job and level.

## License

[AGPL-3.0](LICENSE)

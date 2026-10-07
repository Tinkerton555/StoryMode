// Builds StoryMode/Data/gear-table.json from the game's own item data.
// Usage: dotnet run -- "<game folder>\game\sqpack" [output path]
using System.Text.Json;
using Lumina.Excel.Sheets;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: GearTableGenerator <path to game\\sqpack> [output path]");
    return 1;
}

Report.Init(args[0]);
var items = Report.GD.GetExcelSheet<Item>()!;
var classJobs = Report.GD.GetExcelSheet<ClassJob>()!;
uint ByName(string name) => items.FirstOrDefault(i => i.Name.ToString() == name).RowId;

int[] tiers = [50, 60, 70, 80, 90, 100];
string[] slotKeys = ["Head", "Body", "Hands", "Legs", "Feet", "MainHand", "OffHand"];
uint[] arrJobs = [19, 20, 21, 22, 23, 24, 25, 27, 28, 30];

// Hand-picked weapons where "the weapon that belongs to the set" isn't right.
var weaponOverrides = new Dictionary<(uint Job, int Tier), (uint Main, uint Off)>
{
    [(19, 50)] = (0, 2306), // Paladin: Holy Shield to go with Curtana
};

// ARR artifact sets had no weapon, so level 50 uses each job's base relic weapon. Named here
// because trial weapons share its item level.
var arrRelics = new Dictionary<uint, string>
{
    [19] = "Curtana", [20] = "Sphairai", [21] = "Bravura", [22] = "Gae Bolg", [23] = "Artemis Bow",
    [24] = "Thyrus", [25] = "Stardust Rod", [27] = "The Veil of Wiyu", [28] = "Omnilex", [30] = "Yoshimitsu",
};

var missing = 0;
uint Require(string name)
{
    var id = ByName(name);
    if (id == 0)
    {
        Console.Error.WriteLine($"Missing item: {name}");
        missing++;
    }
    return id;
}

// ---- Job gear, one entry per level bracket ----

var jobsOut = new SortedDictionary<uint, List<object>>();
foreach (var cj in classJobs.Where(c => c.RowId is >= 8 and <= 42 and not (26 or 29)))
{
    var abbr = cj.Abbreviation.ToString();
    var mine = Report.JobItems(abbr);
    var list = new List<(int Min, object Entry)>();

    foreach (var t in tiers)
    {
        // The set for a bracket is the lowest item level job-only body piece in it; the rest of the
        // set is found by its shared name ("Gallant Surcoat" -> "Gallant ...").
        var inRange = mine.Where(i => i.LevelEquip > t - 10 && i.LevelEquip <= t).ToList();
        var body = inRange.Where(i => Report.SlotOf(i) == 1).OrderBy(i => i.LevelItem.RowId).ThenBy(i => i.RowId).FirstOrDefault();
        if (body.RowId == 0)
            continue;

        var il = body.LevelItem.RowId;
        var bodyName = body.Name.ToString();
        var family = bodyName.Contains(' ') ? bodyName[..bodyName.LastIndexOf(' ')] : bodyName;
        var blocks = body.EquipSlotCategory.Value;
        var picked = new Dictionary<string, Item> { ["Body"] = body };

        for (var s = 0; s < 5; s++)
        {
            if (s == 1)
                continue;
            // Robes that cover the legs etc. leave those slots empty.
            if ((s == 0 && blocks.Head == -1) || (s == 2 && blocks.Gloves == -1) || (s == 3 && blocks.Legs == -1) || (s == 4 && blocks.Feet == -1))
                continue;

            var slotItems = inRange.Where(i => Report.SlotOf(i) == s).ToList();
            var pick = slotItems.Where(i => i.Name.ToString().StartsWith(family + " "))
                .OrderBy(i => i.LevelItem.RowId != il).ThenBy(i => i.LevelItem.RowId).ThenBy(i => i.RowId).FirstOrDefault();
            if (pick.RowId == 0)
                pick = slotItems.OrderBy(i => i.LevelItem.RowId != il).ThenBy(i => i.LevelItem.RowId).ThenBy(i => i.RowId).FirstOrDefault();
            if (pick.RowId != 0)
                picked[slotKeys[s]] = pick;
        }

        // Weapons: ARR jobs get their base relic (item level 80), since ARR artifact sets had no
        // weapon. Everyone else gets the weapon released alongside the set.
        var weaponIl = t == 50 && arrJobs.Contains(cj.RowId) ? 80u : il;
        var main = inRange.Where(i => Report.SlotOf(i) == 5 && i.LevelItem.RowId == weaponIl).OrderBy(i => i.RowId).FirstOrDefault();
        if (main.RowId == 0)
            main = inRange.Where(i => Report.SlotOf(i) == 5).OrderBy(i => i.LevelItem.RowId).ThenBy(i => i.RowId).FirstOrDefault();

        var offs = inRange.Where(i => Report.SlotOf(i) == 6).ToList();
        var off = offs.Where(i => i.LevelItem.RowId == weaponIl).OrderBy(i => i.RowId).FirstOrDefault();
        if (off.RowId == 0 && main.RowId != 0)
        {
            var mainFamily = main.Name.ToString().Split(' ')[0];
            off = offs.Where(i => i.Name.ToString().StartsWith(mainFamily + " ")).OrderBy(i => i.LevelItem.RowId).FirstOrDefault();
        }

        if (t == 50 && arrRelics.TryGetValue(cj.RowId, out var relic))
            main = items.GetRow(Require(relic));
        if (weaponOverrides.TryGetValue((cj.RowId, t), out var ov))
        {
            if (ov.Main != 0) main = items.GetRow(ov.Main);
            if (ov.Off != 0) off = items.GetRow(ov.Off);
        }
        if (main.RowId != 0) picked["MainHand"] = main;
        if (off.RowId != 0) picked["OffHand"] = off;

        list.Add((t, Entry($"{abbr} {t}: {family}", t, picked)));
    }

    // Jobs that start above level 1 or 30 with their own outfit (Samurai, Gunbreaker, Viper, Blue
    // Mage, ...) get exactly what their unlock quest hands out, from their starting level.
    if (UnlockOutfit(cj, abbr) is { } unlock)
    {
        int start = cj.StartingLevel;
        list.RemoveAll(e => e.Min <= start);
        list.Insert(0, (start, Entry($"{abbr} {start}: {unlock.Family} (unlock)", start, unlock.Picked)));
    }

    jobsOut[cj.RowId] = list.Select(e => e.Entry).ToList();
}

static object Entry(string name, int minLevel, Dictionary<string, Item> picked) => new
{
    name,
    minLevel,
    items = picked.ToDictionary(p => p.Key, p => p.Value.RowId),
    itemNames = picked.ToDictionary(p => p.Key, p => p.Value.Name.ToString()),
};

// The outfit and weapon a job's unlock quest rewards, when it rewards a "<Name> Attire Coffer"
// (or "<Name> Armor Coffer").
(string Family, Dictionary<string, Item> Picked)? UnlockOutfit(ClassJob cj, string abbr)
{
    if (cj.UnlockQuest.ValueNullable is not { } quest)
        return null;

    var rewards = quest.Reward.Select(r => items.TryGetRow(r.RowId, out var it) ? it : default).Where(it => it.RowId != 0).ToList();
    var coffer = rewards.FirstOrDefault(it => System.Text.RegularExpressions.Regex.IsMatch(it.Name.ToString(), " (Attire|Armor) Coffer"));
    if (coffer.RowId == 0)
        return null;

    var family = System.Text.RegularExpressions.Regex.Match(coffer.Name.ToString(), "^(.*?) (Attire|Armor) Coffer").Groups[1].Value;
    var picked = new Dictionary<string, Item>();
    var jobGear = Report.JobItems(abbr, minItemLevel: 0);

    for (var s = 0; s < 5; s++)
    {
        var pick = jobGear.Where(i => Report.SlotOf(i) == s && i.Name.ToString().StartsWith(family + " "))
            .OrderBy(i => i.LevelItem.RowId).ThenBy(i => i.RowId).FirstOrDefault();
        if (pick.RowId != 0)
            picked[slotKeys[s]] = pick;
    }
    foreach (var it in rewards)
    {
        if (Report.SlotOf(it) == 5) picked["MainHand"] = it;
        if (Report.SlotOf(it) == 6) picked["OffHand"] = it;
    }

    if (!picked.ContainsKey("Body"))
    {
        Console.Error.WriteLine($"{abbr}: couldn't find the {family} set from its unlock quest");
        missing++;
        return null;
    }
    return (family, picked);
}

// ---- Starter outfits: no job stone, or a crafter/gatherer below 50 ----
// Each class gets a combination of the game's level 1 "Weathered" clothing that suits it.
// Order: head, body, hands, legs, feet. "" leaves the slot empty.

var classOutfits = new Dictionary<uint, (string Name, string[] Pieces)>
{
    [1] = ("Gladiator", ["", "Weathered Acton (Grey)", "Weathered Halfgloves", "Weathered Chausses", "Weathered Jackboots (Taupe)"]),
    [2] = ("Pugilist", ["Weathered Bandana", "Weathered Shirt", "", "Weathered Sarouel (Brown)", "Weathered Moccasins"]),
    [3] = ("Marauder", ["", "Weathered Jerkin (Brown)", "Weathered Work Gloves", "Weathered Kecks (Brown)", "Weathered Workboots"]),
    [4] = ("Lancer", ["Weathered Coif (Grey)", "Weathered Tabard", "Weathered Fingerless Gloves", "Weathered Breeches (Grey)", "Weathered Thighboots (Grey)"]),
    [5] = ("Archer", ["Weathered Beret (Beige)", "Weathered Hunting Tunic (Brown)", "Weathered Fingerless Gloves", "Weathered Tights", "Weathered Thighboots"]),
    [6] = ("Conjurer", ["", "Weathered Robe", "", "Weathered Slops (Beige)", "Weathered Pattens"]),
    [7] = ("Thaumaturge", ["", "Weathered Dalmatica", "", "Weathered Slops (Grey)", "Weathered Crakows"]),
    [26] = ("Arcanist", ["Weathered Spectacles", "Weathered Halfrobe (Beige)", "", "Weathered Culottes (Grey)", "Weathered Shoes (Taupe)"]),
    [29] = ("Rogue", ["Weathered Bandana", "Weathered Doublet Vest (Grey)", "Weathered Fingerless Gloves", "Weathered Kecks (Grey)", "Weathered Shoes"]),
    [8] = ("Carpenter", ["", "Weathered Doublet (Yellow)", "Weathered Work Gloves", "Weathered Trousers", "Weathered Workboots"]),
    [9] = ("Blacksmith", ["", "Weathered Shirt", "Weathered Smithy's Gloves", "Weathered Kecks (Brown)", "Weathered Workboots (Grey)"]),
    [10] = ("Armorer", ["", "Weathered Acton (Grey)", "Weathered Smithy's Gloves", "Weathered Chausses (Grey)", "Weathered Workboots (Grey)"]),
    [11] = ("Goldsmith", ["Weathered Spectacles", "Weathered Gown (Grey)", "Weathered Fingerless Gloves", "Weathered Slops (Brown)", "Weathered Shoes"]),
    [12] = ("Leatherworker", ["", "Weathered Jerkin (Brown)", "Weathered Work Gloves", "Weathered Breeches (Grey)", "Weathered Moccasins"]),
    [13] = ("Weaver", ["Weathered Beret (Beige)", "Weathered Gown (Beige)", "Weathered Halfgloves", "Weathered Culottes (Grey)", "Weathered Shoes (Taupe)"]),
    [14] = ("Alchemist", ["Weathered Spectacles", "Weathered Dalmatica (Grey)", "Weathered Halfgloves", "Weathered Slops (Grey)", "Weathered Pattens (Grey)"]),
    [15] = ("Culinarian", ["Weathered Coif (Grey)", "Weathered Shirt", "", "Weathered Trousers", "Weathered Shoes"]),
    [16] = ("Miner", ["Weathered Bandana", "Weathered Doublet Vest (Grey)", "Weathered Work Gloves", "Weathered Kecks (Grey)", "Weathered Workboots"]),
    [17] = ("Botanist", ["Weathered Sun Hat", "Weathered Hunting Tunic (Brown)", "Weathered Work Gloves", "Weathered Slops (Brown)", "Weathered Moccasins"]),
    [18] = ("Fisher", ["Weathered Beach Hat (Blue)", "Weathered Shirt", "Weathered Fingerless Gloves", "Weathered Trousers", "Weathered Duckbills"]),
};

// Classes without a starting weapon in the ClassJob sheet.
var starterWeapons = new Dictionary<uint, (string Main, string Off)>
{
    [29] = ("Weathered Daggers", ""),
    [8] = ("Weathered Saw", ""),
    [9] = ("Weathered Cross-pein Hammer", ""),
    [10] = ("Weathered Doming Hammer", ""),
    [11] = ("Weathered Chaser Hammer", ""),
    [12] = ("Weathered Head Knife", ""),
    [13] = ("Rusty Needle", ""),
    [14] = ("Weathered Alembic", ""),
    [15] = ("Weathered Skillet", ""),
    [16] = ("Weathered Pickaxe", "Weathered Sledgehammer"),
    [17] = ("Weathered Hatchet", "Weathered Scythe"),
    [18] = ("Weathered Fishing Rod", ""),
};

var classOut = new SortedDictionary<uint, object>();
foreach (var (id, (name, pieces)) in classOutfits)
{
    var picked = new Dictionary<string, uint>();
    for (var s = 0; s < 5; s++)
    {
        if (pieces[s] != "" && Require(pieces[s]) is var itemId and not 0)
            picked[slotKeys[s]] = itemId;
    }

    if (starterWeapons.TryGetValue(id, out var w))
    {
        if (Require(w.Main) is var m and not 0) picked["MainHand"] = m;
        if (w.Off != "" && Require(w.Off) is var o and not 0) picked["OffHand"] = o;
    }
    else
    {
        var cj = classJobs.GetRow(id);
        if (cj.ItemStartingWeaponMainHand.RowId != 0) picked["MainHand"] = cj.ItemStartingWeaponMainHand.RowId;
        if (cj.ItemStartingWeaponOffHand.RowId != 0) picked["OffHand"] = cj.ItemStartingWeaponOffHand.RowId;
    }

    classOut[id] = new
    {
        name = $"{name} starter outfit",
        items = picked,
        itemNames = picked.ToDictionary(p => p.Key, p => items.GetRow(p.Value).Name.ToString()),
    };
}

var output = args.Length > 1 ? args[1] : Path.Combine("..", "..", "StoryMode", "Data", "gear-table.json");
File.WriteAllText(output, JsonSerializer.Serialize(new { classOutfits = classOut, jobs = jobsOut }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {output}: {classOut.Count} class outfits, {jobsOut.Sum(j => j.Value.Count)} job tiers, {missing} missing items.");
return missing == 0 ? 0 : 1;

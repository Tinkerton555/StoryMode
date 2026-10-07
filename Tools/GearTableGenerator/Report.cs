using Lumina;
using Lumina.Excel.Sheets;

/// <summary>Helpers for finding a job's own gear in the game data.</summary>
static class Report
{
    public static GameData GD { get; private set; } = null!;

    public static void Init(string sqpackPath) => GD = new GameData(sqpackPath);

    /// <summary>0 head, 1 body, 2 hands, 3 legs, 4 feet, 5 main hand, 6 off hand, -1 anything else.</summary>
    public static int SlotOf(Item i)
    {
        var e = i.EquipSlotCategory.Value;
        return e.Head == 1 ? 0 : e.Body == 1 ? 1 : e.Gloves == 1 ? 2 : e.Legs == 1 ? 3 : e.Feet == 1 ? 4
            : e.MainHand == 1 ? 5 : e.OffHand == 1 ? 6 : -1;
    }

    // Base classes whose gear can also be worn by the job.
    private static readonly Dictionary<string, string> Bases = new()
    {
        ["PLD"] = "GLA", ["MNK"] = "PGL", ["WAR"] = "MRD", ["DRG"] = "LNC", ["BRD"] = "ARC",
        ["WHM"] = "CNJ", ["BLM"] = "THM", ["SMN"] = "ACN", ["SCH"] = "ACN", ["NIN"] = "ROG",
    };

    private static Dictionary<uint, HashSet<string>>? categoryJobs;

    /// <summary>Gear only this job (or its base class) can wear: artifact sets, relics, job quest gear.</summary>
    public static List<Item> JobItems(string abbr, uint minItemLevel = 6)
    {
        if (categoryJobs == null)
        {
            var props = typeof(ClassJobCategory).GetProperties().Where(p => p.PropertyType == typeof(bool)).ToList();
            categoryJobs = GD.GetExcelSheet<ClassJobCategory>()!
                .ToDictionary(c => c.RowId, c => props.Where(p => (bool)p.GetValue(c)!).Select(p => p.Name).ToHashSet());
        }

        var allowed = new HashSet<string> { abbr };
        if (Bases.TryGetValue(abbr, out var b))
            allowed.Add(b);

        return GD.GetExcelSheet<Item>()!.Where(i => !i.IsPvP && i.ModelMain != 0 && i.LevelItem.RowId >= minItemLevel
                && !i.Name.ToString().Contains("Replica")
                && categoryJobs.TryGetValue(i.ClassJobCategory.RowId, out var cj) && cj.Contains(abbr) && cj.IsSubsetOf(allowed)
                && SlotOf(i) >= 0)
            .ToList();
    }
}

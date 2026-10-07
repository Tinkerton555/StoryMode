using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace StoryMode.Gear;

/// <summary>A full appearance to put on a character, as game model IDs.</summary>
public sealed class Outfit
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Indexed by <see cref="DrawDataContainer.EquipmentSlot"/>. A zero value means an empty slot.</summary>
    public EquipmentModelId[] Equipment { get; } = new EquipmentModelId[10];

    public WeaponModelId MainHand;
    public WeaponModelId OffHand;
}

/// <summary>A job's outfit, starting from <see cref="MinLevel"/>.</summary>
public sealed record OutfitTier(int MinLevel, Outfit Outfit);

/// <summary>
/// The artifact gear for every job and the starter outfit for every class, loaded from the
/// embedded gear-table.json (item IDs) and turned into model IDs using the game's Item sheet.
/// </summary>
public sealed class GearTable
{
    private static readonly string[] SlotKeys =
        ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary>Outfits for players without a job stone (and crafters/gatherers below 50), by ClassJob ID.</summary>
    public Dictionary<uint, Outfit> ClassOutfits { get; } = new();

    /// <summary>Artifact tiers by ClassJob ID, sorted by level.</summary>
    public Dictionary<uint, List<OutfitTier>> JobTiers { get; } = new();

    public static GearTable Load(IDataManager data)
    {
        using var stream = typeof(GearTable).Assembly.GetManifestResourceStream("StoryMode.gear-table.json")
            ?? throw new InvalidOperationException("gear-table.json is missing from the plugin.");
        var file = JsonSerializer.Deserialize<GearTableFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("gear-table.json could not be read.");

        var items = data.GetExcelSheet<Item>();
        var table = new GearTable();

        foreach (var (classJob, entry) in file.ClassOutfits)
            table.ClassOutfits[classJob] = BuildOutfit(entry, items);

        foreach (var (classJob, tiers) in file.Jobs)
        {
            table.JobTiers[classJob] = tiers
                .Select(t => new OutfitTier(t.MinLevel, BuildOutfit(t, items)))
                .OrderBy(t => t.MinLevel)
                .ToList();
        }

        return table;
    }

    private static Outfit BuildOutfit(OutfitEntry entry, Lumina.Excel.ExcelSheet<Item> items)
    {
        var outfit = new Outfit { Name = entry.Name };

        for (var i = 0; i < SlotKeys.Length; i++)
        {
            if (entry.Items.TryGetValue(SlotKeys[i], out var itemId) && itemId != 0 && items.TryGetRow(itemId, out var item))
                outfit.Equipment[i] = ToEquipmentModel(item.ModelMain);
        }

        if (entry.Items.TryGetValue("MainHand", out var mainId) && items.TryGetRow(mainId, out var main))
        {
            outfit.MainHand = ToWeaponModel(main.ModelMain);

            // Paired weapons (fists, daggers, bows with quivers) carry their off hand on the main item.
            if (main.ModelSub != 0)
                outfit.OffHand = ToWeaponModel(main.ModelSub);
        }

        if (outfit.OffHand.Value == 0 && entry.Items.TryGetValue("OffHand", out var offId) && items.TryGetRow(offId, out var off))
            outfit.OffHand = ToWeaponModel(off.ModelMain);

        return outfit;
    }

    private static EquipmentModelId ToEquipmentModel(ulong model) => new()
    {
        Id = (ushort)(model & 0xFFFF),
        Variant = (byte)((model >> 16) & 0xFF),
    };

    private static WeaponModelId ToWeaponModel(ulong model) => new()
    {
        Id = (ushort)(model & 0xFFFF),
        Type = (ushort)((model >> 16) & 0xFFFF),
        Variant = (ushort)((model >> 32) & 0xFFFF),
    };

    private sealed class GearTableFile
    {
        public Dictionary<uint, OutfitEntry> ClassOutfits { get; set; } = new();
        public Dictionary<uint, List<OutfitEntry>> Jobs { get; set; } = new();
    }

    private sealed class OutfitEntry
    {
        public string Name { get; set; } = string.Empty;
        public int MinLevel { get; set; }
        public Dictionary<string, uint> Items { get; set; } = new();
    }
}

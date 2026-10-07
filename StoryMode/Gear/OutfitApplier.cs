using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace StoryMode.Gear;

/// <summary>
/// Puts affected characters into their Story Mode outfit and keeps them there.
///
/// Two parts work together:
/// - Hooks on the game's "load equipment" and "load weapon" functions swap in our models whenever
///   the game changes a tracked character's gear (gear changes, job changes, glamour plates).
/// - A per-frame check catches characters the hooks didn't see (new arrivals, settings changes)
///   and restores real gear for anyone who should no longer be changed.
/// </summary>
public sealed unsafe class OutfitApplier : IDisposable
{
    private delegate void LoadEquipmentDelegate(DrawDataContainer* self, DrawDataContainer.EquipmentSlot slot, EquipmentModelId* model, byte force);
    private delegate void LoadWeaponDelegate(DrawDataContainer* self, DrawDataContainer.WeaponSlot slot, WeaponModelId model, byte redrawOnEquality, byte a5, byte skipGameObject, byte a7, byte a8);

    /// <summary>A character's real appearance, kept so it can be put back.</summary>
    private sealed class RealLook
    {
        public uint EntityId;
        public readonly EquipmentModelId[] Equipment = new EquipmentModelId[10];
        public ushort Glasses;

        // Real weapons aren't stored here: weapon swaps only change what's drawn, so the game's
        // own record (DrawData.Weapon(slot).ModelId) always holds the real weapon.
        // The outfit and weapon setting the drawn weapons were last set for.
        public Outfit? WeaponsFor;
        public bool WeaponsReplaced;
    }

    private readonly Configuration config;
    private readonly OutfitResolver resolver;
    private readonly Hook<LoadEquipmentDelegate> loadEquipmentHook;
    private readonly Hook<LoadWeaponDelegate> loadWeaponHook;

    // Characters currently shown in a Story Mode outfit, by address.
    private readonly Dictionary<nint, RealLook> changed = new();
    private readonly HashSet<nint> seenThisFrame = new();

    // Cutscene copy address -> the real player's address, rebuilt each frame.
    private readonly Dictionary<nint, nint> cutsceneSources = new();
    private readonly Dictionary<(string Name, ushort HomeWorld), nint> playersByName = new();

    public OutfitApplier(Configuration config, OutfitResolver resolver)
    {
        this.config = config;
        this.resolver = resolver;

        loadEquipmentHook = Plugin.GameInterop.HookFromAddress<LoadEquipmentDelegate>(
            (nint)DrawDataContainer.MemberFunctionPointers.LoadEquipment, LoadEquipmentDetour);
        loadWeaponHook = Plugin.GameInterop.HookFromAddress<LoadWeaponDelegate>(
            (nint)DrawDataContainer.MemberFunctionPointers.LoadWeapon, LoadWeaponDetour);
        loadEquipmentHook.Enable();
        loadWeaponHook.Enable();

        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        RestoreAll();
        loadEquipmentHook.Dispose();
        loadWeaponHook.Dispose();
    }

    private bool IsActive()
    {
        if (!config.Enabled)
            return false;
        if (!config.OnlyInCutscenes && !config.OnlyInDuties)
            return true;

        var inCutscene = Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
                         || Plugin.Condition[ConditionFlag.WatchingCutscene]
                         || Plugin.Condition[ConditionFlag.WatchingCutscene78];
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty]
                     || Plugin.Condition[ConditionFlag.BoundByDuty56]
                     || Plugin.Condition[ConditionFlag.BoundByDuty95];

        return (config.OnlyInCutscenes && inCutscene) || (config.OnlyInDuties && inDuty);
    }

    /// <summary>The outfit this character should wear right now, or null to leave them alone.</summary>
    private Outfit? DesiredOutfit(Character* character)
    {
        if (!IsActive() || character == null || character->GameObject.ObjectKind != ObjectKind.Pc)
            return null;

        // Decide using the real player, not a cutscene copy: copies have no level and no
        // party/friend flags, and a copy of you isn't the local player.
        var source = character;
        if (cutsceneSources.TryGetValue((nint)character, out var real))
            source = (Character*)real;

        var local = (Character*)Control.GetLocalPlayer();
        if (local == null)
            return null;
        return resolver.ShouldAffect(source, local) ? resolver.Resolve(source) : null;
    }

    // Object table slots 200-243 hold the copies of characters the game creates for cutscenes.
    private const int FirstCutsceneIndex = 200;
    private const int EndCutsceneIndex = 244;

    private static bool IsCutsceneCopy(Character* character)
        => character->GameObject.ObjectIndex is >= FirstCutsceneIndex and < EndCutsceneIndex;

    /// <summary>Links each cutscene copy to the real player it was made from, matched by name and home world.</summary>
    private void MapCutsceneCopies()
    {
        cutsceneSources.Clear();
        playersByName.Clear();

        foreach (var obj in Plugin.ObjectTable)
        {
            var character = (Character*)obj.Address;
            if (character == null || character->GameObject.ObjectKind != ObjectKind.Pc || IsCutsceneCopy(character))
                continue;
            playersByName.TryAdd((obj.Name.TextValue, character->HomeWorld), (nint)character);
            playersByName.TryAdd((obj.Name.TextValue, 0), (nint)character);
        }

        foreach (var obj in Plugin.ObjectTable)
        {
            var character = (Character*)obj.Address;
            if (character == null || character->GameObject.ObjectKind != ObjectKind.Pc || !IsCutsceneCopy(character))
                continue;
            if (playersByName.TryGetValue((obj.Name.TextValue, character->HomeWorld), out var real)
                || playersByName.TryGetValue((obj.Name.TextValue, 0), out real))
                cutsceneSources[(nint)character] = real;
        }
    }

    private EquipmentModelId DesiredEquipment(Outfit outfit, int slot, EquipmentModelId real)
    {
        // Accessories aren't part of artifact sets, so they're either removed or left as they are.
        if (slot >= (int)DrawDataContainer.EquipmentSlot.Ears)
            return config.RemoveAccessories ? outfit.Equipment[slot] : real;
        return outfit.Equipment[slot];
    }

    // ---- Per-frame check ----

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Without the local player (logging in, loading screens) we can't tell who "me", the party
        // or friends are, so make no decisions this frame.
        if (Control.GetLocalPlayer() == null)
            return;

        seenThisFrame.Clear();
        MapCutsceneCopies();

        foreach (var obj in Plugin.ObjectTable)
        {
            var character = (Character*)obj.Address;
            if (character == null || character->GameObject.ObjectKind != ObjectKind.Pc)
                continue;

            var address = (nint)character;
            seenThisFrame.Add(address);

            // An address can be reused by someone new after a player leaves.
            if (changed.TryGetValue(address, out var stale) && stale.EntityId != character->GameObject.EntityId)
                changed.Remove(address);

            var outfit = DesiredOutfit(character);
            if (outfit != null)
                Apply(character, outfit);
            else if (changed.ContainsKey(address))
                Restore(character);
        }

        // Forget characters that have left.
        if (changed.Count > seenThisFrame.Count)
        {
            foreach (var address in new List<nint>(changed.Keys))
            {
                if (!seenThisFrame.Contains(address))
                    changed.Remove(address);
            }
        }
    }

    private void Apply(Character* character, Outfit outfit)
    {
        ref var drawData = ref character->DrawData;
        var address = (nint)character;

        if (!changed.TryGetValue(address, out var real))
        {
            real = new RealLook { EntityId = character->GameObject.EntityId };

            // Cutscene and GPose copies are made from how the player looks right now, which may
            // already be our outfit. Their real look is the original player's real look.
            if (cutsceneSources.TryGetValue(address, out var source) && changed.TryGetValue(source, out var sourceReal))
            {
                Array.Copy(sourceReal.Equipment, real.Equipment, real.Equipment.Length);
                real.Glasses = sourceReal.Glasses;
            }
            else
            {
                for (var i = 0; i < 10; i++)
                    real.Equipment[i] = drawData.Equipment((DrawDataContainer.EquipmentSlot)i);
                real.Glasses = drawData.GlassesIds[0];
            }

            changed[address] = real;
        }

        fixed (DrawDataContainer* container = &drawData)
        {
            for (var i = 0; i < 10; i++)
            {
                var slot = (DrawDataContainer.EquipmentSlot)i;
                var want = DesiredEquipment(outfit, i, real.Equipment[i]);
                if (!SameModel(drawData.Equipment(slot), want))
                    loadEquipmentHook.Original(container, slot, &want, 1);
            }

            // Drawn weapons are set when the outfit or setting changes. Weapon changes the game
            // makes itself (job changes, gear changes) go through LoadWeaponDetour.
            var replace = ReplacesWeapons(outfit);
            if (real.WeaponsFor != outfit || real.WeaponsReplaced != replace)
            {
                // Real weapons only need redrawing if ours were showing; crafting tools are never touched.
                if (replace || real.WeaponsReplaced)
                {
                    ShowWeapon(container, DrawDataContainer.WeaponSlot.MainHand, replace ? outfit.MainHand : RealWeapon(container, DrawDataContainer.WeaponSlot.MainHand));
                    ShowWeapon(container, DrawDataContainer.WeaponSlot.OffHand, replace ? outfit.OffHand : RealWeapon(container, DrawDataContainer.WeaponSlot.OffHand));
                }
                real.WeaponsFor = outfit;
                real.WeaponsReplaced = replace;
            }

            var glasses = drawData.GlassesIds[0];
            if (config.HideFacewear)
            {
                if (glasses != 0)
                {
                    // Facewear doesn't go through our hooks, so a change shows up here first.
                    real.Glasses = glasses;
                    drawData.SetGlasses(0, 0);
                }
            }
            else if (glasses != real.Glasses)
            {
                drawData.SetGlasses(0, real.Glasses);
            }
        }
    }

    private void Restore(Character* character)
    {
        var address = (nint)character;
        if (!changed.Remove(address, out var real))
            return;

        ref var drawData = ref character->DrawData;
        fixed (DrawDataContainer* container = &drawData)
        {
            for (var i = 0; i < 10; i++)
            {
                var model = real.Equipment[i];
                loadEquipmentHook.Original(container, (DrawDataContainer.EquipmentSlot)i, &model, 1);
            }

            if (real.WeaponsReplaced)
            {
                ShowWeapon(container, DrawDataContainer.WeaponSlot.MainHand, RealWeapon(container, DrawDataContainer.WeaponSlot.MainHand));
                ShowWeapon(container, DrawDataContainer.WeaponSlot.OffHand, RealWeapon(container, DrawDataContainer.WeaponSlot.OffHand));
            }
            if (drawData.GlassesIds[0] != real.Glasses)
                drawData.SetGlasses(0, real.Glasses);
        }
    }

    private void RestoreAll()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            var character = (Character*)obj.Address;
            if (character != null && changed.ContainsKey((nint)character))
                Restore(character);
        }
        changed.Clear();
    }

    private static WeaponModelId RealWeapon(DrawDataContainer* container, DrawDataContainer.WeaponSlot slot)
        => container->Weapon(slot).ModelId;

    private bool ReplacesWeapons(Outfit outfit) => config.ReplaceWeapons && !outfit.KeepRealWeapons;

    /// <summary>
    /// Changes the weapon that's drawn without touching the game's record of the real weapon
    /// (skipGameObject = 1, as Glamourer does). The game uses that record for things like draw
    /// animations, so overwriting it broke them until the character was reloaded.
    /// </summary>
    private void ShowWeapon(DrawDataContainer* container, DrawDataContainer.WeaponSlot slot, WeaponModelId model)
        => loadWeaponHook.Original(container, slot, model, 1, 0, 1, 0, 0);

    private static bool SameModel(EquipmentModelId a, EquipmentModelId b)
        => a.Id == b.Id && a.Variant == b.Variant && a.Stain0 == b.Stain0 && a.Stain1 == b.Stain1;

    // ---- Hooks: the game is changing someone's gear ----

    private void LoadEquipmentDetour(DrawDataContainer* self, DrawDataContainer.EquipmentSlot slot, EquipmentModelId* model, byte force)
    {
        try
        {
            var character = self->OwnerObject;
            if (character != null && changed.TryGetValue((nint)character, out var real) && (int)slot < 10)
            {
                var outfit = DesiredOutfit(character);
                if (outfit != null)
                {
                    // Remember what they really changed to, then load ours instead.
                    real.Equipment[(int)slot] = *model;
                    var want = DesiredEquipment(outfit, (int)slot, *model);
                    loadEquipmentHook.Original(self, slot, &want, force);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error while swapping equipment");
        }

        loadEquipmentHook.Original(self, slot, model, force);
    }

    private void LoadWeaponDetour(DrawDataContainer* self, DrawDataContainer.WeaponSlot slot, WeaponModelId model, byte redrawOnEquality, byte a5, byte skipGameObject, byte a7, byte a8)
    {
        // Let the game load the real weapon first, so its own record stays correct.
        loadWeaponHook.Original(self, slot, model, redrawOnEquality, a5, skipGameObject, a7, a8);

        try
        {
            // Then draw ours over it straight away with the game's own flags, as Glamourer does.
            var character = self->OwnerObject;
            if (slot != DrawDataContainer.WeaponSlot.System && character != null
                && changed.TryGetValue((nint)character, out var real) && DesiredOutfit(character) is { } outfit)
            {
                var replace = ReplacesWeapons(outfit);
                if (replace)
                {
                    var ours = slot == DrawDataContainer.WeaponSlot.MainHand ? outfit.MainHand : outfit.OffHand;
                    loadWeaponHook.Original(self, slot, ours, 1, a5, 1, a7, a8);
                }
                real.WeaponsFor = outfit;
                real.WeaponsReplaced = replace;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error while swapping weapon");
        }
    }
}

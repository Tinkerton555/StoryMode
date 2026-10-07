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
        public WeaponModelId MainHand;
        public WeaponModelId OffHand;
        public ushort Glasses;
    }

    private readonly Configuration config;
    private readonly OutfitResolver resolver;
    private readonly Hook<LoadEquipmentDelegate> loadEquipmentHook;
    private readonly Hook<LoadWeaponDelegate> loadWeaponHook;

    // Characters currently shown in a Story Mode outfit, by address.
    private readonly Dictionary<nint, RealLook> changed = new();
    private readonly HashSet<nint> seenThisFrame = new();

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
        var local = (Character*)Control.GetLocalPlayer();
        return resolver.ShouldAffect(character, local) ? resolver.Resolve(character) : null;
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
        seenThisFrame.Clear();

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
            for (var i = 0; i < 10; i++)
                real.Equipment[i] = drawData.Equipment((DrawDataContainer.EquipmentSlot)i);
            real.MainHand = drawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
            real.OffHand = drawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId;
            real.Glasses = drawData.GlassesIds[0];
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

            if (config.ReplaceWeapons)
            {
                SetWeapon(container, DrawDataContainer.WeaponSlot.MainHand, outfit.MainHand);
                SetWeapon(container, DrawDataContainer.WeaponSlot.OffHand, outfit.OffHand);
            }
            else
            {
                SetWeapon(container, DrawDataContainer.WeaponSlot.MainHand, real.MainHand);
                SetWeapon(container, DrawDataContainer.WeaponSlot.OffHand, real.OffHand);
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

            SetWeapon(container, DrawDataContainer.WeaponSlot.MainHand, real.MainHand);
            SetWeapon(container, DrawDataContainer.WeaponSlot.OffHand, real.OffHand);
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

    private void SetWeapon(DrawDataContainer* container, DrawDataContainer.WeaponSlot slot, WeaponModelId want)
    {
        if (container->Weapon(slot).ModelId.Value != want.Value)
            loadWeaponHook.Original(container, slot, want, 1, 0, 0, 0, 0);
    }

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
        try
        {
            var character = self->OwnerObject;
            if (slot != DrawDataContainer.WeaponSlot.System && character != null
                && changed.TryGetValue((nint)character, out var real))
            {
                var outfit = DesiredOutfit(character);
                if (outfit != null)
                {
                    if (slot == DrawDataContainer.WeaponSlot.MainHand)
                        real.MainHand = model;
                    else
                        real.OffHand = model;

                    if (config.ReplaceWeapons)
                        model = slot == DrawDataContainer.WeaponSlot.MainHand ? outfit.MainHand : outfit.OffHand;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error while swapping weapon");
        }

        loadWeaponHook.Original(self, slot, model, redrawOnEquality, a5, skipGameObject, a7, a8);
    }
}

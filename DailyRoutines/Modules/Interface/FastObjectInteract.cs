using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ImGuiNET;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.Modules;

[ModuleDescription("FastObjectInteractTitle", "FastObjectInteractDescription", ModuleCategories.Interface)]
public unsafe class FastObjectInteract : IDailyModule
{
    public bool Initialized { get; set; }
    public bool WithConfigUI => true;
    internal static Overlay? Overlay { get; private set; }

    private class ObjectWaitSelected(GameObject* gameObject, string name, ObjectKind kind, float distance)
    {
        public GameObject* GameObject { get; set; } = gameObject;
        public string Name { get; set; } = name;
        public ObjectKind Kind { get; set; } = kind;
        public float Distance { get; set; } = distance;
    }

    private static float ConfigFontScale = 1f;
    private static HashSet<ObjectKind> ConfigSelectedKinds = new();

    private static bool IsResizeEnabled;

    private static readonly Dictionary<nint, ObjectWaitSelected> ObjectsWaitSelected = [];

    private static readonly Dictionary<ObjectKind, string> ObjectKindLoc = new()
    {
        { ObjectKind.BattleNpc, "战斗类 NPC (不建议)" },
        { ObjectKind.EventNpc, "一般类 NPC" },
        { ObjectKind.EventObj, "事件物体 (绝大多数要交互的都属于此类)" },
        { ObjectKind.Treasure, "宝箱" },
        { ObjectKind.Aetheryte, "以太之光" },
        { ObjectKind.GatheringPoint, "采集点" },
        { ObjectKind.MountType, "坐骑 (不建议)" },
        { ObjectKind.Companion, "宠物 (不建议)" },
        { ObjectKind.Retainer, "雇员" },
        { ObjectKind.Area, "地图传送相关" },
        { ObjectKind.Housing, "家具庭具" },
        { ObjectKind.CardStand, "固定类物体 (如无人岛采集点等)" },
        { ObjectKind.Ornament, "时尚配饰 (不建议)" }
    };

    public void Init()
    {
        Overlay ??= new Overlay(this, $"Daily Routines {Service.Lang.GetText("FastObjectInteractTitle")}");
        Overlay.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize |
                        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoCollapse;

        Service.Config.AddConfig(this, "FontScale", 1f);
        Service.Config.AddConfig(this, "SelectedKinds",
                                 new HashSet<ObjectKind>
                                 {
                                     ObjectKind.EventNpc, ObjectKind.EventObj, ObjectKind.Treasure,
                                     ObjectKind.Aetheryte, ObjectKind.GatheringPoint
                                 });
        ConfigFontScale = Service.Config.GetConfig<float>(this, "FontScale");
        ConfigSelectedKinds = Service.Config.GetConfig<HashSet<ObjectKind>>(this, "SelectedKinds");

        Service.Framework.Update += OnUpdate;
    }

    public void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{Service.Lang.GetText("FastObjectInteract-FontScale")}:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputFloat("###FontScaleInput", ref ConfigFontScale, 0f, 0f, ConfigFontScale.ToString(),
                             ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ConfigFontScale = Math.Max(0.1f, ConfigFontScale);
            Service.Config.UpdateConfig(this, "FontScale", ConfigFontScale);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{Service.Lang.GetText("FastObjectInteract-SelectedObjectKinds")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(370f);
        if (ImGui.BeginCombo("###ObjectKindsSelection",
                             Service.Lang.GetText("FastObjectInteract-SelectedObjectKindsAmount",
                                                  ConfigSelectedKinds.Count), ImGuiComboFlags.HeightLarge))
        {
            foreach (var kind in ObjectKindLoc)
            {
                var state = ConfigSelectedKinds.Contains(kind.Key);
                if (ImGui.Checkbox(kind.Value, ref state))
                {
                    if (!ConfigSelectedKinds.Remove(kind.Key))
                        ConfigSelectedKinds.Add(kind.Key);

                    Service.Config.UpdateConfig(this, "SelectedKinds", ConfigSelectedKinds);
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Checkbox(Service.Lang.GetText("FastObjectInteract-OverlayResizeMode"), ref IsResizeEnabled))
        {
            if (IsResizeEnabled)
                Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
            else
                Overlay.Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        }
    }

    public void OverlayUI()
    {
        foreach (var kvp in ObjectsWaitSelected)
        {
            if (kvp.Value.GameObject == null) continue;

            ImGui.BeginDisabled(!CanInteract(kvp.Value.Kind, kvp.Value.Distance));
            if (ButtonSelectable($"{kvp.Value.Name}###{kvp.Key}"))
            {
                var objSelected = kvp.Value.GameObject;
                if (objSelected != null)
                {
                    TargetSystem.Instance()->Target = objSelected;
                    TargetSystem.Instance()->InteractWithObject(objSelected);
                    if (kvp.Value.Kind != ObjectKind.EventNpc)
                        TargetSystem.Instance()->OpenObjectInteraction(objSelected);
                }
            }

            ImGui.EndDisabled();
        }
    }

    private void OnUpdate(Framework framework)
    {
        if (EzThrottler.Throttle("FastSelectObjects", 250))
        {
            if (Service.Condition[ConditionFlag.BetweenAreas])
            {
                ObjectsWaitSelected.Clear();
                Overlay.IsOpen = false;
                return;
            }

            var tempObjects = new SortedDictionary<float, ObjectWaitSelected>();

            foreach (var obj in Service.ObjectTable)
            {
                var objKind = obj.ObjectKind;
                if (!ConfigSelectedKinds.Contains(objKind)) continue;
                var gameObj = (GameObject*)obj.Address;
                var objDistance =
                    HelpersOm.GetGameDistanceFromObject((GameObject*)Service.ClientState.LocalPlayer.Address, gameObj);
                if (objDistance > 8 || !obj.IsTargetable || !obj.IsValid()) continue;

                while (tempObjects.ContainsKey(objDistance)) objDistance += 0.001f;

                tempObjects.Add(objDistance,
                                new ObjectWaitSelected(gameObj, obj.Name.ExtractText(), objKind, objDistance));
            }

            ObjectsWaitSelected.Clear();
            foreach (var tempObj in tempObjects.Values) ObjectsWaitSelected.Add((nint)tempObj.GameObject, tempObj);

            Overlay.IsOpen = ObjectsWaitSelected.Any() && !IsOccupied();
        }
    }

    private static bool CanInteract(ObjectKind kind, float distance)
    {
        return kind switch
        {
            ObjectKind.EventNpc => distance <= 6.5,
            ObjectKind.Aetheryte => distance <= 13,
            _ => distance <= 2.6
        };
    }

    public static bool ButtonSelectable(string text)
    {
        var style = ImGui.GetStyle();
        var padding = style.FramePadding;
        ImGui.SetWindowFontScale(ConfigFontScale);
        var textSize = ImGui.CalcTextSize(text);

        var size = new Vector2(Math.Max(ImGui.GetContentRegionAvail().X, textSize.X + (2 * padding.X)),
                               textSize.Y + (2 * padding.Y));

        var result = ImGui.Button(text, size);
        ImGui.SetWindowFontScale(1f);

        return result;
    }

    public void Uninit()
    {
        if (P.WindowSystem.Windows.Contains(Overlay)) P.WindowSystem.RemoveWindow(Overlay);
        Overlay = null;

        Service.Framework.Update -= OnUpdate;
        ObjectsWaitSelected.Clear();
    }
}

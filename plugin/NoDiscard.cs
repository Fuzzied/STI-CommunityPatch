// Space Travel Idle community mod - remove the discard (trash can) zone
//
// Discarding a card removes it from your hand AND ends your turn - the
// same cost as Skip, plus you lose the card. With the modded 6th hand
// slot the discarded card is also the only redraw candidate and comes
// straight back next round, so the mechanic is strictly worse than
// Skip. This patch removes the trash-can drop zone from the battle
// screen entirely. Skip is unaffected.
//
// The trash zone isn't a serialized field on BattlePanel, so it is
// located by its scene wiring: the EventTrigger whose persistent
// callback is BattlePanel.DiscardAreaDropped.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

[BepInPlugin("sti.community.nodiscard", "STI Community No Discard", "1.0.0")]
public class NoDiscardPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("no-discard", Logger))
        {
            enabled = false;
            return;
        }
        Harmony harmony = new Harmony("sti.community.nodiscard");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("No-discard active: trash-can drop zone hidden");
    }
}

[HarmonyPatch(typeof(BattlePanel), "PlayerAreaInsertCards")]
public static class HideDiscardZonePatch
{
    private static GameObject discardZone;

    public static void Postfix(BattlePanel __instance)
    {
        if (discardZone != null)
        {
            if (discardZone.activeSelf)
            {
                discardZone.SetActive(false);
            }
            return;
        }
        EventTrigger[] triggers = __instance.GetComponentsInChildren<EventTrigger>(true);
        for (int t = 0; t < triggers.Length; t++)
        {
            System.Collections.Generic.List<EventTrigger.Entry> entries = triggers[t].triggers;
            for (int e = 0; e < entries.Count; e++)
            {
                UnityEngine.Events.UnityEventBase callback = entries[e].callback;
                int count = callback.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                {
                    if (callback.GetPersistentMethodName(i) == "DiscardAreaDropped")
                    {
                        discardZone = triggers[t].gameObject;
                        discardZone.SetActive(false);
                        return;
                    }
                }
            }
        }
    }
}

// Space Travel Idle community mod - keep scroll positions across a research
// level-up (vanilla bug)
//
// Reported by Fuzzied: "when you research something your scrolling in loadouts
// or other places gets reset".
//
// Why it happens, from the decompiled game:
//
//   Research.CheckLevelUp()            sets UnlockManager.needsUnlockReqUpdate
//   UnlockManager.Update()             sees the flag and calls
//   UnlockManager.UnlockRequirementUpdate()
//        -> spaceshipPanel.LoadSpeedModules()
//        -> technoPanel.LoadStarData()      (UpdateStarScroll always does
//                                            starScrollContent.RemoveAllChildren())
//        -> sidePanel.SetupDestSelector()   (RemoveAllChildren, SidePanel.cs:149)
//        -> cardPanel.ReloadLoadoutControls()
//        -> cardBag.ForceReloadUI()         (raises six pending* flags)
//
// CardPanel.FixedUpdate then acts on those flags on a later tick and calls
// LoadDeck / LoadDeckBag / LoadPermSet / LoadPermBag / LoadCardLibs, each of
// which empties its content object into an ObjectPool before refilling it
// (CardPanel.DisplayEquipedCards -> SaveAllChildrenToPool).
//
// For the frame in which a content object is empty its RectTransform height
// collapses, so Unity's ScrollRect clamps the content back to the top edge -
// and there it stays once the list is refilled. Every research level-up
// therefore throws you back to the top of whatever list you were reading.
//
// The fix: remember where every ScrollRect was sitting immediately before
// UnlockRequirementUpdate runs, then put it back for the handful of frames
// the rebuild takes. Positions are held in content-local pixels rather than
// ScrollRect.normalizedPosition, so a list that grows by one newly unlocked
// row keeps the rows you were looking at in place instead of shifting them.
//
// Deliberately conservative:
//   - the hold is dropped the moment the player touches the wheel or holds
//     the left mouse button, so it can never fight a real drag;
//   - it ends five frames after the card bag's pending* flags all clear, and
//     in any case after 30 frames (half a second);
//   - it only ever arms from UnlockRequirementUpdate, so scroll resets that
//     the game does on purpose - switching star tab, loading a loadout -
//     still behave exactly as vanilla;
//   - the ScrollRect list is cached and only re-scanned every 20 seconds (or
//     when something in it has been destroyed), because a research level-up
//     can fire many times a second in the late game and FindObjectsOfType is
//     not cheap.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.scrollkeeper", "STI Community Scroll Keeper", "1.0.0")]
public class ScrollKeeperPlugin : BaseUnityPlugin
{
    // Frames to keep holding after the card bag says it has finished.
    private const int SETTLE_FRAMES = 5;
    // Absolute ceiling on one hold, so a stuck flag cannot freeze scrolling.
    private const int MAX_FRAMES = 30;
    // How long a scan of the scene's ScrollRects stays good for.
    private const float CACHE_SECONDS = 20f;

    internal static ManualLogSource Log;

    private static readonly List<ScrollRect> cachedRects = new List<ScrollRect>();
    private static float cacheStamp = -9999f;

    private static readonly List<ScrollRect> heldRects = new List<ScrollRect>();
    private static readonly List<Vector2> heldPos = new List<Vector2>();
    private static int framesLeft;
    private static int settleLeft;

    private static bool warned;

    private void Awake()
    {
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.scrollkeeper");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Scroll keeper active: lists hold their position "
            + "across a research/unlock rebuild");
    }

    // Called from the prefix on UnlockManager.UnlockRequirementUpdate, i.e.
    // before anything has been torn down.
    internal static void Snapshot()
    {
        try
        {
            RefreshCache();
            heldRects.Clear();
            heldPos.Clear();
            for (int i = 0; i < cachedRects.Count; i++)
            {
                ScrollRect rect = cachedRects[i];
                if (rect == null)
                {
                    continue;
                }
                RectTransform content = rect.content;
                if (content == null)
                {
                    continue;
                }
                heldRects.Add(rect);
                heldPos.Add(content.anchoredPosition);
            }
            framesLeft = MAX_FRAMES;
            settleLeft = SETTLE_FRAMES;
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void LateUpdate()
    {
        if (framesLeft <= 0)
        {
            return;
        }
        try
        {
            if (PlayerIsScrolling())
            {
                framesLeft = 0;
                return;
            }
            for (int i = 0; i < heldRects.Count; i++)
            {
                ScrollRect rect = heldRects[i];
                if (rect == null)
                {
                    continue;
                }
                RectTransform content = rect.content;
                if (content == null || content.anchoredPosition == heldPos[i])
                {
                    continue;
                }
                content.anchoredPosition = heldPos[i];
                // The clamp that moved it also leaves inertia behind; without
                // this the list slides after we let go.
                rect.velocity = Vector2.zero;
            }
            framesLeft--;
            if (RebuildPending())
            {
                settleLeft = SETTLE_FRAMES;
            }
            else
            {
                settleLeft--;
                if (settleLeft <= 0)
                {
                    framesLeft = 0;
                }
            }
        }
        catch (Exception e)
        {
            framesLeft = 0;
            Fail(e);
        }
    }

    // CardPanel.FixedUpdate consumes these a tick or more after the unlock
    // rebuild, so the hold has to outlive them.
    private static bool RebuildPending()
    {
        Player player = Player.shared;
        if (player == null || player.cardBag == null)
        {
            return false;
        }
        CardBag bag = player.cardBag;
        return bag.pendingDeckDisplayUpdate
            || bag.pendingDeckUpdate
            || bag.pendingDeckBagDisplayUpdate
            || bag.pendingPermBagDisplayUpdate
            || bag.pendingPermSetDisplayUpdate
            || bag.pendingLibDisplayUpdate;
    }

    private static bool PlayerIsScrolling()
    {
        return Input.GetMouseButton(0)
            || Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f;
    }

    private static void RefreshCache()
    {
        bool stale = cachedRects.Count == 0
            || Time.unscaledTime - cacheStamp > CACHE_SECONDS;
        if (!stale)
        {
            for (int i = 0; i < cachedRects.Count; i++)
            {
                if (cachedRects[i] == null)
                {
                    stale = true;
                    break;
                }
            }
        }
        if (!stale)
        {
            return;
        }
        cachedRects.Clear();
        // true = include inactive: the techno panel is rebuilt even while its
        // tab is hidden, and the player expects it where they left it when
        // they come back to it.
        ScrollRect[] found = UnityEngine.Object.FindObjectsOfType<ScrollRect>(true);
        for (int i = 0; i < found.Length; i++)
        {
            cachedRects.Add(found[i]);
        }
        cacheStamp = Time.unscaledTime;
    }

    private static void Fail(Exception e)
    {
        // This runs inside a per-frame update; logging every frame would be
        // worse than the bug, so it is said once and then dropped.
        if (warned || Log == null)
        {
            return;
        }
        warned = true;
        Log.LogWarning("Scroll keeper disabled itself after an error: " + e);
    }
}

[HarmonyPatch(typeof(UnlockManager), "UnlockRequirementUpdate")]
public static class ScrollKeeperSnapshotPatch
{
    public static void Prefix()
    {
        ScrollKeeperPlugin.Snapshot();
    }
}

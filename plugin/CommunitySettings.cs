// Space Travel Idle community mod - one settings section for the mod's rows
//
// Fuzzied: "Cards: Wrong sorting? I'd rather have them sorting under the English
// language vanilla setting. So that all of ours are easily identified under
// the vanilla settings."
//
// Seven plugins add a row to the game's settings panel: the dev console
// toggle, the tooltip duration, the zone key gate, the collector top-off, the
// cargo bay doors, the loadout snap back and the merge slider.
// Each of them builds its row by cloning SettingsManager.hideCardControlUIToggle,
// because the panel is a prefab and there is nothing else to clone. That has
// two consequences Fuzzied ran straight into:
//
//   - the rows land inside the vanilla "Card" group, where they read as
//     card settings and are impossible to tell apart from the game's own; and
//   - they inherit the size of a vanilla row, which on a wide monitor is
//     small enough to be unreadable. The canvas is scaled to a 1920 wide
//     reference matched on WIDTH, so a 21:9 screen gets about 810 units of
//     height rather than 1080 and everything vertical shrinks with it.
//
// So the rows now move into a section of their own, cloned from the Tutorial
// Progress block and dropped in underneath the Language dropdown at the foot
// of the same column - which is exactly where Fuzzied asked for it - and get
// the same measured row height and font ceiling that make the Auto start
// section readable.
//
// WHY THIS FILE IS COMPILED INTO SEVEN DLLs. build_plugin.ps1 compiles one .cs
// per plugin, and BepInEx gives every plugin its own assembly. There is no
// shared library to put this in without inventing one and making every plugin
// depend on it loading first. Seven copies of a static class in seven
// assemblies are seven separate types, which sounds like a problem and is not:
// nothing here keeps state that another copy needs. The section is found on
// the panel by name, so whichever plugin loads first builds it and the rest
// find it sitting there. The one thing that MUST stay in step between
// the copies is RowOrder, which is why it is a plain list of names rather
// than anything a plugin could pass in.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class CommunitySettings
{
    // The section itself, and the one name in the vanilla panel this has to
    // know. Everything else is found by looking, so a reshuffle upstream
    // costs the nicer position and nothing more.
    private const string SECTION_NAME = "CommunityModContainer";
    private const string GUIDE_CONTAINER = "GuideProgressContainer";
    private const string HEADING = "Community mod";

    // The rows the mod adds, top to bottom. Every plugin ships its own copy
    // of this file and therefore its own copy of this list, so whichever one
    // sorts last sorts them all the same way. A row not named here still
    // works; it just settles at the bottom.
    private static readonly string[] RowOrder =
    {
        "CommunityHideDevConsoleToggle",
        "CommunityTooltipTimeRow",
        "CommunityZoneKeyGateRow",
        "CommunityTopOffRow",
        "CommunityCargoDoorsRow",
        "CommunityCargoKeepRecRow",
        "CommunityLoadoutSnapBackRow",
        "CommunityMergeTopLevelRow"
    };

    private static readonly FieldInfo LangDropdownField =
        AccessTools.Field(typeof(SettingsManager), "langSelectDropdown");

    // How much bigger than the game's own buttons the mod's rows are.
    //
    // Four attempts at this all sized the rows to match a button in the
    // Tutorial Progress block, and Fuzzied said the text was too small every
    // single time - "Toggle text size is still small, are you registering
    // this? I've said it many times now." The log from the last of them says
    // the rows came out 40 tall with a font ceiling of 20, which IS a button,
    // exactly. So a button is not the target to hit. It is the thing that is
    // too small.
    //
    // The room is there. That same log line measured the column at 1547 units
    // with nothing else claiming any of it, and four rows at double size cost
    // 160 of that.
    private const float ROW_SCALE = 2f;

    // Measured off the Tutorial Progress block rather than written down, so
    // the mod's rows keep their relationship to the game's own if the game
    // ever changes them. rowHeight and fontCeiling are already scaled up;
    // fontFloor is the vanilla size, which is where auto sizing is allowed to
    // give up and no further.
    private static float rowHeight;
    private static float fontCeiling;
    private static float fontFloor;
    private static float measuredFont;
    private static float headingHeight;
    private static bool loggedShape;

    // Takes a freshly cloned row into the mod's own settings section, sized
    // and positioned to match. False means the panel was not the shape this
    // was written against and the caller should leave the row where it
    // cloned it - a row in the wrong group still works, no row at all does
    // not.
    internal static bool Adopt(SettingsManager owner, Toggle template,
        GameObject clone, ManualLogSource log)
    {
        try
        {
            Transform section = Section(template, log);
            if (section == null) { return false; }

            clone.transform.SetParent(section, false);
            StyleRow(clone, rowHeight, fontCeiling, fontFloor);
            Sort(section);
            Fit(owner, section, log);
            return true;
        }
        catch (Exception e)
        {
            if (log != null)
            {
                log.LogWarning("Could not move a row into the community "
                    + "settings section: " + e.Message);
            }
            return false;
        }
    }

    // Whether this row has already been added. LoadSystemSettings can run
    // more than once, and the row may be in either place - the section when
    // the panel looked the way we expected, beside the template when it did
    // not.
    internal static bool AlreadyAdded(Toggle template, string cloneName)
    {
        try
        {
            Transform parent = template.transform.parent;
            if (parent == null) { return false; }
            if (parent.Find(cloneName) != null) { return true; }
            Transform column = parent.parent;
            if (column == null) { return false; }
            Transform section = column.Find(SECTION_NAME);
            return section != null && section.Find(cloneName) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ the section

    // The mod's section, built the first time it is asked for. Cloned from
    // the Tutorial Progress block rather than built from nothing so that the
    // layout group, the size fitter and the heading's font all come along
    // without this file having to know what any of them were set to.
    private static Transform Section(Toggle template, ManualLogSource log)
    {
        Transform cardGroup = template.transform.parent;
        if (cardGroup == null) { return null; }
        Transform column = cardGroup.parent;
        if (column == null) { return null; }

        Transform existing = column.Find(SECTION_NAME);
        if (existing != null)
        {
            // Another plugin got here first. Measure anyway - this copy of
            // the file has its own idea of how tall a row is and has not
            // filled it in yet.
            if (rowHeight <= 0f)
            {
                Transform g = FindGuide(column);
                if (g != null) { Measure(g); }
                else { MeasureFrom(existing); }
            }
            return existing;
        }

        Transform guide = FindGuide(column);
        if (guide == null) { return null; }
        Measure(guide);

        GameObject clone = UnityEngine.Object.Instantiate(
            guide.gameObject, column);
        clone.name = SECTION_NAME;
        clone.transform.SetSiblingIndex(LanguageIndex(column) + 1);

        // Backwards, so "the first label" is the one nearest the top once the
        // list is reversed: the heading, not something inside a button.
        // Deactivated before Destroy because Destroy does not take effect
        // until the end of the frame and the rows go in now.
        LangText heading = null;
        for (int i = clone.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = clone.transform.GetChild(i);
            LangText text = child.GetComponent<LangText>();
            if (heading == null && text != null
                && child.GetComponent<Button>() == null)
            {
                heading = text;
                continue;
            }
            child.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(child.gameObject);
        }
        if (heading != null) { heading.SetLocalisedText(HEADING); }

        if (log != null)
        {
            log.LogInfo("Settings: built the community mod section under "
                + "Language (row " + rowHeight + ", font " + fontFloor
                + " to " + fontCeiling + ")");
        }
        return clone.transform;
    }

    // The Tutorial Progress block, found by name anywhere under the panel
    // that holds both columns. Null when the panel is not laid out the way
    // this was written against, which is a reason to fall back rather than a
    // reason to throw.
    private static Transform FindGuide(Transform column)
    {
        Transform panel = column.parent;
        if (panel == null) { return null; }
        Transform[] all = panel.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name == GUIDE_CONTAINER) { return all[i]; }
        }
        return null;
    }

    // Where the Language block sits in this column, so the section can go in
    // directly underneath it. The language dropdown is a serialized field on
    // SettingsManager, so this is the one part of the panel's shape that can
    // be asked rather than guessed. -1 when it is somewhere else entirely,
    // which puts the section at the foot of the column instead - the same
    // place, as long as Language is last.
    private static int LanguageIndex(Transform column)
    {
        try
        {
            if (LangDropdownField != null && SettingsManager.shared != null)
            {
                object value = LangDropdownField.GetValue(
                    SettingsManager.shared);
                Component dropdown = value as Component;
                if (dropdown != null)
                {
                    Transform node = dropdown.transform;
                    while (node != null && node.parent != column)
                    {
                        node = node.parent;
                    }
                    if (node != null)
                    {
                        return node.GetSiblingIndex();
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        return column.childCount - 1;
    }

    // Rows in RowOrder, under whatever the section already holds - which is
    // the heading and nothing else. Walking the list and sending each row to
    // the back leaves them in exactly the order the list gives, and leaves a
    // row the list has never heard of below them.
    private static void Sort(Transform section)
    {
        for (int i = 0; i < RowOrder.Length; i++)
        {
            Transform row = section.Find(RowOrder[i]);
            if (row != null) { row.SetAsLastSibling(); }
        }
    }

    // ------------------------------------------------------------ measuring

    // How tall a button in the Tutorial Progress block is, and how big its
    // text is.
    private static void Measure(Transform guide)
    {
        rowHeight = 40f;
        headingHeight = 30f;
        measuredFont = 0f;
        for (int i = 0; i < guide.childCount; i++)
        {
            Transform child = guide.GetChild(i);
            RectTransform rt = child as RectTransform;
            if (child.GetComponent<Button>() != null)
            {
                if (rt != null && rt.sizeDelta.y > 0f)
                {
                    rowHeight = rt.sizeDelta.y;
                }
                TMP_Text text = child.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                {
                    // An auto sizing label's fontSize is whatever it last
                    // drew at, which before the panel has ever been laid out
                    // is not a size at all. The number somebody actually
                    // chose is fontSizeMax.
                    float size = text.enableAutoSizing
                        ? text.fontSizeMax : text.fontSize;
                    if (size > 0f) { measuredFont = size; }
                }
            }
            else if (rt != null && rt.sizeDelta.y > 0f
                && child.GetComponent<LangText>() != null)
            {
                headingHeight = rt.sizeDelta.y;
            }
        }
        Scale();
    }

    // Fallback for a plugin that arrives after the section is built and
    // cannot find the block it was cloned from: measure the section's own
    // first row instead.
    private static void MeasureFrom(Transform section)
    {
        rowHeight = 40f;
        headingHeight = 30f;
        measuredFont = 0f;
        for (int i = 0; i < section.childCount; i++)
        {
            RectTransform rt = section.GetChild(i) as RectTransform;
            if (rt == null) { continue; }
            if (section.GetChild(i).GetComponent<Toggle>() != null
                && rt.sizeDelta.y > 0f)
            {
                // A row already in the section has been scaled once; take the
                // measurement back to vanilla so Scale does not do it twice.
                rowHeight = rt.sizeDelta.y / ROW_SCALE;
                break;
            }
        }
        Scale();
    }

    // Turns what was measured into the sizes the rows actually use.
    //
    // The floor is the vanilla size, the ceiling is ROW_SCALE times it, and
    // auto sizing picks whatever fits the column between the two. That is a
    // one way bet: a label short enough to be drawn twice as big is drawn
    // twice as big, and a label too long for that is drawn at the size it
    // would have had anyway. Nothing can come out smaller than it used to,
    // which after four goes at this is the property worth having.
    //
    // A measurement outside 0.4-0.6 of the row height is a number we misread
    // rather than a number to obey: before the panel has ever been laid out,
    // an auto sizing label's fontSize is whatever it last drew at, which is
    // how the auto start rows once ended up with a ceiling of about 12.
    private static void Scale()
    {
        float vanillaFont = measuredFont;
        if (vanillaFont < rowHeight * 0.4f || vanillaFont > rowHeight * 0.6f)
        {
            vanillaFont = rowHeight * 0.5f;
        }
        fontFloor = vanillaFont;
        fontCeiling = vanillaFont * ROW_SCALE;
        rowHeight = rowHeight * ROW_SCALE;
    }

    // ------------------------------------------------------------- the rows

    // A row at the height of the buttons beside it, with the tick box and the
    // words centred in it.
    private static void StyleRow(GameObject clone, float height, float font,
        float floor)
    {
        // The whole row answers the mouse, not just the tick box and the
        // words. A tooltip you have to take aim at is not much of a tooltip,
        // and a fully transparent Image still takes a raycast.
        if (clone.GetComponent<Image>() == null)
        {
            Image hit = clone.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
        }
        if (height <= 0f) { return; }

        RectTransform rt = clone.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
        }
        // Belt and braces: a vertical layout group with childControlHeight on
        // reads the LayoutElement and ignores sizeDelta, and with it off does
        // the opposite. Set both and it does not matter which.
        LayoutElement element = clone.GetComponent<LayoutElement>();
        if (element == null) { element = clone.AddComponent<LayoutElement>(); }
        element.minHeight = height;
        element.preferredHeight = height;

        float box = height * 0.55f;
        if (box < 18f) { box = 18f; }
        Transform background = clone.transform.Find("Background");
        if (background != null)
        {
            RectTransform brt = background as RectTransform;
            if (brt != null)
            {
                brt.anchorMin = new Vector2(0f, 0.5f);
                brt.anchorMax = new Vector2(0f, 0.5f);
                brt.pivot = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(box, box);
                brt.anchoredPosition = new Vector2(6f + box * 0.5f, 0f);
            }
            Transform check = background.Find("Checkmark");
            RectTransform crt = check == null ? null : check as RectTransform;
            if (crt != null) { crt.sizeDelta = new Vector2(box, box); }
        }

        Transform label = clone.transform.Find("Label");
        if (label == null) { return; }
        RectTransform lrt = label as RectTransform;
        if (lrt != null)
        {
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.offsetMin = new Vector2(box + 14f, 2f);
            lrt.offsetMax = new Vector2(-6f, -2f);
        }
        TMP_Text text = label.GetComponent<TMP_Text>();
        if (text != null && font > 0f)
        {
            // Auto sizing rather than a number, because how wide the column is
            // depends on the shape of the monitor. Twice the size of the
            // game's own rows where the words fit, and never smaller than the
            // size they would have been anyway.
            text.enableWordWrapping = false;
            text.enableAutoSizing = true;
            text.fontSizeMax = font;
            text.fontSizeMin = (floor > 0f && floor < font)
                ? floor : font * 0.7f;
            text.fontSize = font;
        }
    }

    // ------------------------------------------------------------ fitting in

    // Four full height rows plus a heading is about 200 units of a column
    // that already holds System, Card and Language, and a row that falls off
    // the bottom of the panel cannot be clicked at all. So measure what is
    // left and shrink to fit.
    //
    // A frame late, because at the moment LoadSystemSettings runs the panel
    // has never been laid out and every rect is still zero. If the coroutine
    // cannot be started - the settings object is switched off, which is the
    // usual state of it - the immediate attempt below bails on the zero and
    // the rows keep the size they were built at, which is the readable one.
    private static void Fit(SettingsManager owner, Transform section,
        ManualLogSource log)
    {
        try
        {
            if (owner != null && owner.isActiveAndEnabled)
            {
                owner.StartCoroutine(FitLater(section, log));
                return;
            }
        }
        catch (Exception)
        {
        }
        FitNow(section, log);
    }

    private static IEnumerator FitLater(Transform section, ManualLogSource log)
    {
        yield return null;
        FitNow(section, log);
    }

    private static void FitNow(Transform section, ManualLogSource log)
    {
        try
        {
            if (section == null || rowHeight <= 0f) { return; }
            RectTransform column = section.parent as RectTransform;
            RectTransform outer = column == null
                ? null : column.parent as RectTransform;
            if (outer == null) { return; }
            float available = outer.rect.height;
            if (available <= 1f) { return; }

            float used = 0f;
            for (int i = 0; i < column.childCount; i++)
            {
                RectTransform child = column.GetChild(i) as RectTransform;
                if (child == null) { continue; }
                if (child == section) { continue; }
                if (!child.gameObject.activeSelf) { continue; }
                used += child.rect.height;
            }

            int visible = 0;
            RectTransform first = null;
            for (int i = 0; i < section.childCount; i++)
            {
                Transform child = section.GetChild(i);
                if (child.GetComponent<Toggle>() == null) { continue; }
                visible++;
                if (first == null) { first = child as RectTransform; }
            }
            if (visible == 0) { return; }

            float free = available - used - headingHeight - 24f;
            float want = rowHeight;
            RectTransform labelRect = first == null
                ? null : first.Find("Label") as RectTransform;
            TMP_Text labelText = labelRect == null
                ? null : labelRect.GetComponent<TMP_Text>();
            if (free < want * visible) { want = free / visible; }
            if (want > rowHeight) { want = rowHeight; }
            if (want < 22f) { want = 22f; }

            // Once there is a laid out panel to measure, say what the rows
            // actually came out as. If a size is ever wrong again, this says
            // so outright rather than leaving it to a screenshot.
            if (!loggedShape && log != null)
            {
                loggedShape = true;
                Canvas canvas = column.GetComponentInParent<Canvas>();
                log.LogInfo("Settings: community mod row is "
                    + (first == null ? 0f : first.rect.height)
                    + " tall against a built " + rowHeight
                    + "; the column is " + column.rect.width + " wide and "
                    + available + " tall with " + used + " spoken for, so "
                    + visible + " row(s) get " + want
                    + "; the label is " + (labelRect == null
                        ? 0f : labelRect.rect.width)
                    + " wide and drew at " + (labelText == null
                        ? 0f : labelText.fontSize)
                    + ", allowed " + fontFloor + " to " + fontCeiling
                    + "; canvas scale " + (canvas == null
                        ? 0f : canvas.scaleFactor));
            }

            if (first != null && Mathf.Abs(want - first.rect.height) < 0.5f)
            {
                return;
            }
            // Only the height gives when the column is tight. The words
            // stay as big as they can be drawn, which is the whole point.
            for (int i = 0; i < section.childCount; i++)
            {
                Transform child = section.GetChild(i);
                if (child.GetComponent<Toggle>() == null) { continue; }
                StyleRow(child.gameObject, want, fontCeiling, fontFloor);
            }
        }
        catch (Exception e)
        {
            if (log != null)
            {
                log.LogWarning("Could not fit the community mod rows: "
                    + e.Message);
            }
        }
    }
}

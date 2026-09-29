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

    // The rows scroll. Players on a 1920x1080 screen saw the last three or
    // four rows fall off under the message bar, and the game's panel has no
    // scrolling of its own (Fuzzied, 29.09.2026: "Some people are experiencing
    // the screen not being big enough here, there is however no scroll
    // option?"). The old answer was to shrink the rows to fit, but it measured
    // the panel before the game had laid it out, saw 1547 units free with
    // nothing spoken for, and never shrank anything. Shrinking was the wrong
    // answer anyway: the rows are big because Fuzzied asked for big, and 0.2
    // moves every switch into this list. So the list got a scroll bar when it
    // does not fit. Then Fuzzied saw it beside Auto start: "The left side looks
    // fine compared to the massive text on the right side", and chose "Match
    // the left side". Both lists now size their rows by one rule, see
    // CommunitySettingsFit.
    private const string SCROLL_NAME = "CommunityModScroll";
    private const string ROWS_NAME = "CommunityModRows";

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
        // missed when LoadoutPlus added it, so it sat above them all
        "CommunityLoadoutBestLevelRow",
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
            Transform rows = Rows(section, log);
            if (rows == null) { return false; }

            clone.transform.SetParent(rows, false);
            StyleRow(clone, rowHeight, fontCeiling, fontFloor);
            Sort(rows);
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
            if (section == null) { return false; }
            Transform rows = section.Find(SCROLL_NAME + "/" + ROWS_NAME);
            return (rows != null && rows.Find(cloneName) != null)
                || section.Find(cloneName) != null;
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

    // Rows in RowOrder. Walking the list and sending each row to the back
    // leaves them in exactly the order the list gives, and leaves a row the
    // list has never heard of above them.
    private static void Sort(Transform rows)
    {
        for (int i = 0; i < RowOrder.Length; i++)
        {
            Transform row = rows.Find(RowOrder[i]);
            if (row != null) { row.SetAsLastSibling(); }
        }
    }

    // ------------------------------------------------------------ scrolling

    // The scrolling list the rows live in, built by whichever plugin asks
    // first; the rest find it by name.
    //
    // It sits outside the section's own layout (ignoreLayout) and is placed
    // by CommunitySettingsFit, under the heading, as tall as the room left
    // above the message bar and as wide as the room to the right of it. Kept
    // out of the layout because this file cannot know how the Tutorial
    // Progress block it cloned lays out its children, and a layout group
    // that sizes its children would squash the list to the width of a
    // button and clip the labels.
    private static Transform Rows(Transform section, ManualLogSource log)
    {
        return ScrollList(section, SCROLL_NAME, ROWS_NAME, "community mod list",
            rowHeight, fontCeiling, fontFloor, true, log);
    }

    // The same scrolling list for any section cloned off Tutorial Progress.
    // AutoStart compiles this file in for its Auto start list, so both
    // columns size their rows by one rule (see CommunitySettingsFit).
    // followPeerFont: this list draws its words no bigger than the other
    // one does. Fuzzied, 29.09.2026, looking at both: "The left side looks fine
    // compared to the massive text on the right side."
    internal static Transform ScrollList(Transform section, string scrollName,
        string rowsName, string title, float maxRow, float ceiling,
        float floor, bool followPeerFont, ManualLogSource log)
    {
        Transform existing = section.Find(scrollName + "/" + rowsName);
        if (existing != null) { return existing; }

        GameObject view = new GameObject(scrollName, typeof(RectTransform));
        view.transform.SetParent(section, false);
        RectTransform vrt = (RectTransform)view.transform;
        vrt.anchorMin = new Vector2(0f, 1f);
        vrt.anchorMax = new Vector2(0f, 1f);
        vrt.pivot = new Vector2(0f, 1f);
        vrt.sizeDelta = new Vector2(600f, 400f);
        view.AddComponent<LayoutElement>().ignoreLayout = true;
        // A see-through Image so the mouse wheel reaches the list between
        // rows too, not only over a row.
        Image hit = view.AddComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, 0f);
        hit.raycastTarget = true;
        view.AddComponent<RectMask2D>();

        GameObject content = new GameObject(rowsName, typeof(RectTransform));
        content.transform.SetParent(view.transform, false);
        RectTransform crt = (RectTransform)content.transform;
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0.5f, 1f);
        crt.offsetMin = new Vector2(0f, 0f);
        crt.offsetMax = new Vector2(-BAR_WIDTH - 6f, 0f);
        VerticalLayoutGroup list = content.AddComponent<VerticalLayoutGroup>();
        list.childControlHeight = true;
        list.childControlWidth = true;
        list.childForceExpandHeight = false;
        list.childForceExpandWidth = true;
        list.spacing = 0f;
        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject bar = new GameObject("Scrollbar", typeof(RectTransform));
        bar.transform.SetParent(view.transform, false);
        RectTransform brt = (RectTransform)bar.transform;
        brt.anchorMin = new Vector2(1f, 0f);
        brt.anchorMax = new Vector2(1f, 1f);
        brt.pivot = new Vector2(1f, 0.5f);
        brt.sizeDelta = new Vector2(BAR_WIDTH, 0f);
        brt.anchoredPosition = Vector2.zero;
        Image track = bar.AddComponent<Image>();
        track.color = new Color(1f, 1f, 1f, 0.08f);
        GameObject area = new GameObject("Sliding Area", typeof(RectTransform));
        area.transform.SetParent(bar.transform, false);
        RectTransform art = (RectTransform)area.transform;
        art.anchorMin = Vector2.zero;
        art.anchorMax = Vector2.one;
        art.offsetMin = Vector2.zero;
        art.offsetMax = Vector2.zero;
        GameObject handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(area.transform, false);
        RectTransform hrt = (RectTransform)handle.transform;
        hrt.offsetMin = Vector2.zero;
        hrt.offsetMax = Vector2.zero;
        Image thumb = handle.AddComponent<Image>();
        // the game's own line colour
        thumb.color = new Color(0.37f, 0.93f, 0.95f, 0.85f);
        Scrollbar scrollbar = bar.AddComponent<Scrollbar>();
        scrollbar.handleRect = hrt;
        scrollbar.targetGraphic = thumb;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        ScrollRect scroll = view.AddComponent<ScrollRect>();
        scroll.content = crt;
        scroll.viewport = vrt;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        // one wheel click moves half a row
        scroll.scrollSensitivity = maxRow > 0f ? maxRow * 0.5f : 40f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        CommunitySettingsFit fit = view.AddComponent<CommunitySettingsFit>();
        fit.section = section as RectTransform;
        fit.view = vrt;
        fit.content = crt;
        fit.scroll = scroll;
        fit.log = log;
        fit.title = title;
        fit.maxRow = maxRow > 0f ? maxRow : 80f;
        fit.fontCeiling = ceiling;
        fit.fontFloor = floor;
        fit.followPeerFont = followPeerFont;
        for (int i = 0; i < section.childCount; i++)
        {
            Transform child = section.GetChild(i);
            if (child != view.transform && child.gameObject.activeSelf)
            {
                fit.heading = child as RectTransform;
                break;
            }
        }
        Transform column = section.parent;
        fit.outer = column == null ? null : column.parent as RectTransform;
        Canvas canvas = section.GetComponentInParent<Canvas>();
        Transform root = canvas == null ? null : canvas.rootCanvas.transform;
        fit.messages = root == null ? null : root.Find("MessagesPanel") as RectTransform;

        // What the game draws over the panel's bottom left corner, outside
        // the columns: the Discord and Twitter icons, the version number and
        // the login line. The Auto start list ran straight under all of them.
        Transform panel = fit.outer == null ? null : fit.outer.parent;
        System.Collections.Generic.List<RectTransform> over =
            new System.Collections.Generic.List<RectTransform>();
        if (panel != null)
        {
            Transform social = panel.Find("SocialBtns");
            if (social != null)
            {
                for (int i = 0; i < social.childCount; i++)
                {
                    RectTransform icon = social.GetChild(i) as RectTransform;
                    if (icon != null) { over.Add(icon); }
                }
                if (social.childCount == 0) { over.Add(social as RectTransform); }
            }
            RectTransform version = panel.Find("VersionNumber") as RectTransform;
            if (version != null) { over.Add(version); }
            RectTransform login = panel.Find("LoginStatus") as RectTransform;
            if (login != null) { over.Add(login); }
        }
        fit.obstacles = over.ToArray();
        return crt;
    }

    internal const float BAR_WIDTH = 8f;

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
    internal static void StyleRow(GameObject clone, float height, float font,
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
}

// Places a scrolling list and sizes its rows, every frame the settings panel
// is open, from what the laid out panel actually measures. Done here rather
// than once when the rows are added, because at that moment the panel is
// switched off and every rect in it is zero: that is how the old shrink to
// fit came to believe it had 1547 units free. Only moves anything when a
// number changes.
//
// Top: the bottom of the list's heading. Bottom: whichever is highest of the
// panel's bottom, the top of the message bar, and the top of anything the
// game draws over the panel's corner under this list (the Discord and
// Twitter icons, the version number, the login line). Right: the next column
// along, or the panel's right edge when there is none.
//
// The rule, one for both columns since Fuzzied chose "Match the left side"
// (29.09.2026): rows are full size when there is room, shrink to fit when
// there is not, and stop shrinking at MIN_ROW, where the list scrolls
// instead. Both lists use the SMALLER of the two row heights, so the columns
// always look alike, and the community mod list draws its words no bigger
// than the Auto start list does. The two lists live in different DLLs with
// different copies of this class, so they tell each other their numbers
// through AppDomain data, which every assembly in the game shares.
//
// Compiled into every plugin with CommunitySettings.cs; the plugin that
// builds a list adds its own copy of this, and only that one runs.
internal class CommunitySettingsFit : MonoBehaviour
{
    internal const string COMMUNITY = "community mod list";
    internal const string AUTO_START = "auto start list";

    // Below this a row cannot hold the smallest text the rows allow (20),
    // so the list scrolls rather than squash any further. The old Auto start
    // floor of 22 drew its words taller than the row.
    internal const float MIN_ROW = 28f;

    internal RectTransform section;
    internal RectTransform heading;
    internal RectTransform view;
    internal RectTransform content;
    internal RectTransform outer;
    internal RectTransform messages;
    internal RectTransform[] obstacles;
    internal ScrollRect scroll;
    internal ManualLogSource log;
    internal string title = COMMUNITY;
    internal float maxRow = 80f;
    internal float fontCeiling = 40f;
    internal float fontFloor = 20f;
    internal bool followPeerFont;

    private readonly Vector3[] corners = new Vector3[4];
    private Vector4 last = new Vector4(-1f, -1f, -1f, -1f);
    private int loggedShape;
    private int steady;
    private bool failed;

    // What this list tells the other one: [0] the row height its own room
    // allows, [1] the biggest size its words are drawn at, [2] the frame.
    private double[] mine;
    private double[] peer;
    private float styledRow = -1f;
    private float styledFont = -1f;
    private int styledCount = -1;
    private int sinceStyled;

    private void LateUpdate()
    {
        try
        {
            Place();
        }
        catch (Exception e)
        {
            if (!failed && log != null)
            {
                failed = true;
                log.LogWarning("Could not place the " + title + ": " + e.Message);
            }
        }
    }

    // Everything is measured in screen pixels. The first go measured in the
    // section's own units and got 1445 units of room on a window where the
    // rows plainly had about 1190: the panel, the message bar and the canvas
    // do not share one flat plane, so distances between them only agree once
    // each is projected onto the screen the player looks at.
    private void Place()
    {
        if (section == null || view == null || content == null || heading == null) { return; }
        Canvas canvas = section.GetComponentInParent<Canvas>();
        if (canvas == null) { return; }
        Canvas root = canvas.rootCanvas;
        Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;

        // pixels per unit of the list itself, off its own current size
        view.GetWorldCorners(corners);
        Vector2 v0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 v2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        if (view.rect.height < 1f || view.rect.width < 1f) { return; }
        float pxY = (v2.y - v0.y) / view.rect.height;
        float pxX = (v2.x - v0.x) / view.rect.width;
        if (pxY <= 0.01f || pxX <= 0.01f) { return; }

        heading.GetWorldCorners(corners);
        float top = section.InverseTransformPoint(corners[0]).y;
        float headingPx = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;

        // right: the panel's right edge, or the left edge of the next column
        // along, so the Auto start list stops where the right column starts
        float floorPx = 0f;
        float panelPx = float.NaN, barPx = float.NaN, cornerPx = float.NaN;
        float rightPx = Screen.width;
        Transform column = section.parent;
        if (outer != null)
        {
            outer.GetWorldCorners(corners);
            panelPx = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;
            if (panelPx > floorPx) { floorPx = panelPx; }
            float r = RectTransformUtility.WorldToScreenPoint(cam, corners[2]).x;
            if (r < rightPx) { rightPx = r; }
            for (int i = 0; i < outer.childCount; i++)
            {
                RectTransform next = outer.GetChild(i) as RectTransform;
                if (next == null || next == column || !next.gameObject.activeInHierarchy) { continue; }
                if (next.rect.width < 1f) { continue; }
                next.GetWorldCorners(corners);
                float l = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).x;
                if (l > v0.x + 1f && l < rightPx) { rightPx = l; }
            }
        }

        // bottom: the panel's bottom, the icons and lines in its corner that
        // sit under this list, and the message bar, whichever is highest
        if (obstacles != null)
        {
            for (int i = 0; i < obstacles.Length; i++)
            {
                RectTransform o = obstacles[i];
                if (o == null || !o.gameObject.activeInHierarchy || !Shows(o)) { continue; }
                o.GetWorldCorners(corners);
                Vector2 o0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 o2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                if (o2.x <= v0.x || o0.x >= rightPx) { continue; }
                if (o2.y >= headingPx) { continue; }
                if (float.IsNaN(cornerPx) || o2.y > cornerPx) { cornerPx = o2.y; }
                if (o2.y > floorPx) { floorPx = o2.y; }
            }
        }
        if (messages != null && messages.gameObject.activeInHierarchy)
        {
            // the scroll view is the bar you see; the panel around it may be
            // the size of the whole screen
            RectTransform bar = messages.Find("Scroll View") as RectTransform;
            if (bar == null) { bar = messages; }
            bar.GetWorldCorners(corners);
            Camera barCam = cam;
            Canvas barCanvas = bar.GetComponentInParent<Canvas>();
            if (barCanvas != null && barCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                barCam = null;
            }
            barPx = RectTransformUtility.WorldToScreenPoint(barCam, corners[1]).y;
            // only a bar along the bottom counts; an opened up message log
            // is not a reason to squash the list to nothing
            if (barPx > floorPx && barPx < Screen.height * 0.25f) { floorPx = barPx; }
        }

        float room = (headingPx - floorPx - 8f) / pxY;

        // the rows: as tall as the room shares out, between MIN_ROW and full
        // size, and no taller than the other list's, so both columns match
        int count = 0;
        for (int i = 0; i < content.childCount; i++)
        {
            if (content.GetChild(i).gameObject.activeSelf) { count++; }
        }
        if (count == 0) { return; }
        float own = Mathf.Clamp(room / count, MIN_ROW, maxRow);
        Tell(own);
        double[] other = Hear();
        float row = own;
        if (other != null && other[0] > 1.0 && other[0] < row) { row = (float)other[0]; }
        float font = fontCeiling;
        if (followPeerFont && other != null && other[1] > 1.0 && other[1] < font)
        {
            font = (float)other[1];
        }
        if (Mathf.Abs(row - styledRow) >= 0.5f || Mathf.Abs(font - styledFont) >= 0.5f
            || count != styledCount)
        {
            styledRow = row;
            styledFont = font;
            styledCount = count;
            sinceStyled = 0;
            steady = 0;
            for (int i = 0; i < content.childCount; i++)
            {
                CommunitySettings.StyleRow(content.GetChild(i).gameObject, row, font, fontFloor);
            }
            // one wheel click moves half a row
            if (scroll != null) { scroll.scrollSensitivity = row * 0.5f; }
            return;
        }
        // the words are sized when they are next drawn, so only say how big
        // they came out once a few frames have gone by since the last restyle
        if (sinceStyled < 3) { sinceStyled++; }
        else { mine[1] = Drawn(); }

        float need = content.rect.height;
        // never less than two rows, however cramped: a list you can scroll
        // is still a list you can use
        float least = Mathf.Min(need, 2f * row);
        // but a row under the Twitter icon cannot be clicked, so where the
        // corner icons sit under the list it may come down to one row: on a
        // 2560x1080 screen the second Auto start row went under Twitter
        if (!float.IsNaN(cornerPx))
        {
            least = Mathf.Min(least, Mathf.Max((headingPx - cornerPx - 4f) / pxY, row));
        }
        float height = Mathf.Max(Mathf.Min(need, room), least);
        float width = Mathf.Clamp((rightPx - v0.x - 8f) / pxX, 200f, 900f);

        Vector4 now = new Vector4(top, height, width, need);
        if ((now - last).sqrMagnitude >= 0.25f)
        {
            last = now;
            steady = 0;
            view.anchoredPosition = new Vector2(0f, top - section.rect.yMax);
            view.sizeDelta = new Vector2(width, height);
            return;
        }

        // once per screen size, scale and row size, so a resized window says
        // what it got, and only after thirty frames without a change: the
        // panel slides in when it opens, and the first go logged the heading
        // from mid slide
        if (steady < 30) { steady++; return; }
        // the scale is part of the shape: after a resize the game rescales
        // its canvas a moment later, and the 1920x1080 line first said
        // "fits" at the old scale when at the new one it scrolls
        int shape;
        unchecked
        {
            shape = ((Screen.width * 10000 + Screen.height) * 31
                + Mathf.RoundToInt(pxY * 1000f)) * 31 + Mathf.RoundToInt(row * 10f);
        }
        if (shape != loggedShape && need > 1f && log != null)
        {
            loggedShape = shape;
            log.LogInfo("Settings: " + title + " is " + width + " wide and "
                + height + " tall; its " + count + " rows are " + row
                + " tall (its own room allows " + own + ", the other list "
                + (other == null ? "is not there" : "wants " + other[0])
                + "), words up to " + font + " drawn at " + mine[1]
                + "; they need " + need + " and there is room for "
                + room + ", so it "
                + (need > height + 0.5f ? "scrolls" : "fits without scrolling")
                + " (screen " + Screen.width + "x" + Screen.height + " px: heading bottom "
                + headingPx + ", panel bottom " + panelPx + ", corner icons top " + cornerPx
                + ", message bar top " + barPx + ", " + pxY + " px per unit)");
        }
    }

    // Whether something in the corner is actually drawn: the login line is
    // there all the time and only has words in it when there is something to
    // say.
    private static bool Shows(RectTransform o)
    {
        Graphic g = o.GetComponent<Graphic>();
        if (g == null) { return o.GetComponentInChildren<Graphic>() != null; }
        if (!g.enabled || g.color.a <= 0.01f) { return false; }
        TMP_Text text = g as TMP_Text;
        if (text != null && string.IsNullOrEmpty(text.text)) { return false; }
        return true;
    }

    private void Tell(float own)
    {
        if (mine == null)
        {
            mine = new double[3];
            AppDomain.CurrentDomain.SetData(Key(title), mine);
        }
        mine[0] = own;
        mine[2] = Time.frameCount;
    }

    // The other list's numbers, or null when it is not running right now:
    // the Auto start list is hidden until its first level is bought, and it
    // does not exist at all without the AutoStart plugin.
    private double[] Hear()
    {
        if (peer == null)
        {
            peer = AppDomain.CurrentDomain.GetData(
                Key(title == COMMUNITY ? AUTO_START : COMMUNITY)) as double[];
            if (peer == null || peer.Length < 3) { peer = null; return null; }
        }
        // gone quiet: look it up again next time, because going back to the
        // main menu and loading builds a new list with new numbers
        if (Time.frameCount - peer[2] > 2.0) { peer = null; return null; }
        return peer;
    }

    private static string Key(string name)
    {
        return "STI.CommunityPatch.SettingsList." + name;
    }

    // The biggest size the words in this list came out at. An auto sizing
    // label's fontSize is whatever it last drew at.
    private float Drawn()
    {
        float best = 0f;
        for (int i = 0; i < content.childCount; i++)
        {
            Transform row = content.GetChild(i);
            if (!row.gameObject.activeSelf) { continue; }
            Transform label = row.Find("Label");
            TMP_Text text = label == null ? null : label.GetComponent<TMP_Text>();
            if (text != null && text.fontSize > best) { best = text.fontSize; }
        }
        return best;
    }
}

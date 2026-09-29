// Space Travel Idle community mod - artwork for cards, loaded from a folder.
//
// A card draws its picture through one line, CardItem.SetImage:
//
//     cardImage.sprite = UIColorManager.GetCardIm(card.id);
//
// and GetCardIm is nothing but Utils.LoadSprite("Card/Image", id), which asks
// the packed Card sprite atlas for an entry with that name. An id the atlas has
// never heard of comes back null, and a card with a null sprite draws as an
// empty frame with the name underneath.
//
// That is what Storm Core looks like in the base game today - it is a real,
// shipping card with no artwork - and it is what every card this mod adds would
// look like as well.
//
// ---------------------------------------------------------------------------
// Why this exists when UIFixes already has CardIconFallbackPatch
//
// That patch borrows: it hands a new card one of the game's OWN sprites when
// the atlas has nothing (lightning_mastery borrows lightning_action). It cannot
// show art that is not already inside the game, so it can never fix Storm Core
// and it can never give a new card a picture of its own.
//
// This is the same folder-of-PNGs approach the Big Bang icons already use, and
// for the same reason: adding entries to the packed atlas means rewriting Sprite
// objects inside resources.assets, repacking a texture and keeping the atlas'
// render data map consistent - asset surgery that has to be redone after every
// game update and fails quietly when it goes wrong. Loading a PNG off disk and
// swapping the sprite afterwards does the same job, and it degrades honestly:
// no folder, no file, or a corrupt PNG, and the card simply keeps whatever it
// has today.
//
// The two patches sit on the same method and cooperate in either order. A file
// always wins: if this one runs first the fallback sees a sprite already there
// and leaves it alone, and if the fallback runs first this one replaces the
// borrowed stand-in with the real thing. Drop in lightning_mastery.png and the
// borrowing stops mattering; take it away and the stand-in is still there.
//
// Named by file, so <card id>.png is all it takes - including the id of one of
// the game's own cards, which replaces that card's picture. That is the obvious
// thing someone will want to try.
//
// ---------------------------------------------------------------------------
// What a card sprite actually is, measured from the game's own files rather
// than assumed (UnityPy over SpaceTravelIdle_Data):
//
//     100x100, pixelsToUnits 100, pivot (0.5,0.5), zero border, 100% opaque.
//     Pure black ground, one pale cyan (#A5FBFF), a 2px cyan frame flush to the
//     edge, hard flat edges and no gradients anywhere.
//
// The numbers below follow from that. Nothing here enforces the colour: a PNG
// that ignores the palette still loads, it just will not look like it belongs.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[BepInPlugin("sti.community.cardicons", "STI Community Card Icons", "1.0.0")]
public class CardIconsPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal const string ICON_FOLDER = "card_icons";

    // card id -> file on disk. Built once from the folder listing.
    private static Dictionary<string, string> files;

    // card id -> sprite. Filled the first time a card actually asks.
    private static readonly Dictionary<string, Sprite> cache =
        new Dictionary<string, Sprite>();

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("card-icons", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;
        LoadFileList();
        Harmony harmony = new Harmony("sti.community.cardicons");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    private void LoadFileList()
    {
        files = new Dictionary<string, string>();
        try
        {
            string dir = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                ICON_FOLDER);
            if (!Directory.Exists(dir))
            {
                Logger.LogWarning("Card icons: no '" + ICON_FOLDER
                    + "' folder next to the plugin, so every card keeps the "
                    + "picture it has today.");
                return;
            }
            string[] found = Directory.GetFiles(dir, "*.png");
            for (int i = 0; i < found.Length; i++)
            {
                files[Path.GetFileNameWithoutExtension(found[i])] = found[i];
            }
            Logger.LogInfo("Card icons: " + files.Count + " icon(s) found");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Card icons: could not read the icon folder: "
                + e.Message);
        }
    }

    // Returns null when there is no icon for this card, or it could not be read.
    internal static Sprite Get(string id)
    {
        if (id == null || files == null || files.Count == 0)
        {
            return null;
        }
        Sprite cached;
        if (cache.TryGetValue(id, out cached))
        {
            // A cached sprite can still have been destroyed underneath us -
            // a Big Bang reset calls Utils.ReloadScene - and Unity's == null
            // catches that; drop it and build a fresh one.
            if (cached != null)
            {
                return cached;
            }
            cache.Remove(id);
        }
        string path;
        if (!files.TryGetValue(id, out path))
        {
            return null;
        }
        Sprite sprite = Build(path);
        if (sprite != null)
        {
            cache[id] = sprite;
        }
        else
        {
            // Do not keep retrying a file that cannot be read - once is enough
            // to say so in the log, and this is asked on every card refresh.
            files.Remove(id);
        }
        return sprite;
    }

    // Every card sprite the game ships is 100x100 with pixelsToUnits 100, and
    // that ratio is what decides an Image's native size: rect / pixelsToUnits.
    // Vanilla works out to 1, so the picture asks the layout for 100x100.
    //
    // Scaling pixelsToUnits with the texture holds that ratio at 1 whatever
    // resolution someone's PNG happens to be, so a 400x400 drawing lands in
    // exactly the box a vanilla 100x100 one would.
    private const float VANILLA_SPRITE_SIZE = 100f;
    private const float VANILLA_PIXELS_TO_UNITS = 100f;

    private static float PixelsPerUnitFor(int width)
    {
        if (width <= 0)
        {
            return VANILLA_PIXELS_TO_UNITS;
        }
        return width / VANILLA_SPRITE_SIZE * VANILLA_PIXELS_TO_UNITS;
    }

    // Every card sprite the game ships is fully opaque - a black square with
    // the cyan drawing and frame painted on top - so whatever the card object
    // has behind the picture never shows through.
    //
    // Art drawn as an ordinary PNG is transparent everywhere the pen did not
    // go. Flattening onto the same black makes our pictures behave like the
    // game's own, and it matters for the mipmaps below as well: averaging a
    // half transparent pixel with an opaque one leaves a halo, and there is
    // nothing to halo once every pixel is opaque.
    //
    // Done here rather than baked into the PNGs so that anything dropped into
    // the folder later gets it too, without whoever drew it having to know.
    private static readonly Color32 VANILLA_BACKGROUND = new Color32(0, 0, 0, 255);

    private static Color32[] FlattenOntoBackground(Color32[] pixels)
    {
        int br = VANILLA_BACKGROUND.r;
        int bg = VANILLA_BACKGROUND.g;
        int bb = VANILLA_BACKGROUND.b;
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 p = pixels[i];
            if (p.a == 255)
            {
                continue;
            }
            // LoadImage gives straight (not premultiplied) alpha, so this is
            // the ordinary "source over background" blend.
            int a = p.a;
            int inv = 255 - a;
            pixels[i] = new Color32(
                (byte)((p.r * a + br * inv) / 255),
                (byte)((p.g * a + bg * inv) / 255),
                (byte)((p.b * a + bb * inv) / 255),
                255);
        }
        return pixels;
    }

    // Why the texture is built a second time instead of used as LoadImage
    // leaves it.
    //
    // Cards are drawn small - in the bag and the deck a picture is roughly 60
    // screen pixels - so art drawn at any sensible resolution is being shrunk
    // several times over. A texture with no mipmaps is point sampled when it
    // is shrunk that far: the shader reads one source pixel per screen pixel
    // and everything between those samples is simply not there.
    //
    // That is fine for a thick shape and fatal for a thin one, and a card's
    // frame is a 2px line on the outermost edge of a 100px square. Whether it
    // survives comes down to where the pixel grid happens to land, which is
    // how a frame disappears from one edge and not the other.
    //
    // Mipmaps are the fix: they hand the GPU a properly averaged smaller copy
    // instead of making it guess from one sample, so a line too thin to land on
    // becomes fainter rather than absent. LoadImage does not reliably give a
    // mip chain no matter what the texture was created with, so the pixels are
    // copied into one that definitely has one and Apply builds it.
    private static Texture2D BuildTexture(byte[] bytes)
    {
        Texture2D loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            // The 2x2 size is a placeholder; LoadImage resizes the texture to
            // whatever the PNG actually is.
            if (!loaded.LoadImage(bytes))
            {
                return null;
            }
            Texture2D texture = new Texture2D(
                loaded.width, loaded.height, TextureFormat.RGBA32, true);
            texture.SetPixels32(FlattenOntoBackground(loaded.GetPixels32()));
            texture.Apply(true, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            // Trilinear rather than Bilinear so the step between two mip levels
            // is blended instead of popping as a card resizes.
            texture.filterMode = FilterMode.Trilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }
        finally
        {
            UnityEngine.Object.Destroy(loaded);
        }
    }

    private static Sprite Build(string path)
    {
        try
        {
            Texture2D texture = BuildTexture(File.ReadAllBytes(path));
            if (texture == null)
            {
                Log.LogWarning("Card icons: '" + Path.GetFileName(path)
                    + "' is not a PNG this game can read");
                return null;
            }
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                PixelsPerUnitFor(texture.width));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        catch (Exception e)
        {
            Log.LogWarning("Card icons: could not load '"
                + Path.GetFileName(path) + "': " + e.Message);
            return null;
        }
    }
}

// Patching the lookup rather than one panel means every place a card is drawn -
// bag, permanent deck, alchemy preview, battle hand, tooltips - gets the same
// picture, because they all end up in CardItem.SetImage.
//
// This is asked on every card refresh, so the answer is cached in Get and the
// only work here is a dictionary lookup and a reference compare.
[HarmonyPatch(typeof(UIColorManager), "GetCardIm")]
public static class CardIconPatch
{
    public static void Postfix(string id, ref Sprite __result)
    {
        try
        {
            Sprite ours = CardIconsPlugin.Get(id);
            if (ours != null)
            {
                __result = ours;
            }
        }
        catch (Exception e)
        {
            // Swallowed on purpose: a missing picture is not worth taking a
            // card panel down over, and this runs constantly.
            CardIconsPlugin.Log.LogWarning(
                "Card icons: could not set a picture: " + e.Message);
        }
    }
}

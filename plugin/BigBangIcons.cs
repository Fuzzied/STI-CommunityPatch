// Space Travel Idle community mod - icons for the new Big Bang upgrade lines.
//
// The six lines added by the bigbang-plus data patch have no artwork in the
// game, so they all draw the same "no image" placeholder. That is because
// BigBangUpgradeItem.UpdateUI does:
//
//     image.sprite = Utils.LoadSprite("BigBang", upgradeSet.id);
//
// and Utils.LoadSprite goes through ResourceLoader, which loads the packed
// SpriteAtlas at Resources path "BigBang", asks it for a sprite by name, and
// falls back to the "no_image" sprite when the atlas has no such entry.
//
// ---------------------------------------------------------------------------
// Why the icons are loose PNGs rather than new entries in that atlas
//
// The atlas is a real packed Unity asset - one 512x512 DXT5 texture plus a
// sprite table - so adding to it means writing new Sprite objects into
// resources.assets, repacking the texture, and keeping the atlas' render data
// map consistent. That is a lot of asset surgery to get wrong quietly, and it
// would have to be redone every time the game updates.
//
// Loading PNGs from disk and swapping the sprite afterwards does the same job
// with none of that. It also degrades honestly: no folder, no files, or a
// corrupt PNG and the upgrade simply keeps the placeholder it has today.
//
// The folder is read by filename, so <upgrade id>.png is all it takes to give
// any upgrade an icon - including replacing one of the game's own, which is
// the obvious thing someone will want to try.
//
// ---------------------------------------------------------------------------
// UpdateUI is the hook rather than SetUpgradeSet, because UpdateUI is what
// writes image.sprite and it runs again after every purchase, downgrade and
// reset. Sprites are built once and cached; HideFlags.HideAndDontSave keeps
// Resources.UnloadUnusedAssets from collecting them, which matters because a
// Big Bang reset calls Utils.ReloadScene.
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
using UnityEngine.UI;

[BepInPlugin("sti.community.bigbangicons", "STI Community Big Bang Icons", "1.0.2")]
public class BigBangIconsPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal const string ICON_FOLDER = "bigbang_icons";

    // upgrade id -> file on disk. Built once from the folder listing.
    private static Dictionary<string, string> files;

    // upgrade id -> sprite. Filled the first time an upgrade actually asks.
    private static readonly Dictionary<string, Sprite> cache =
        new Dictionary<string, Sprite>();

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("bigbang-icons", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;
        LoadFileList();
        Harmony harmony = new Harmony("sti.community.bigbangicons");
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
                Logger.LogWarning("Big Bang icons: no '" + ICON_FOLDER
                    + "' folder next to the plugin, so the new upgrades keep "
                    + "the placeholder image.");
                return;
            }
            string[] found = Directory.GetFiles(dir, "*.png");
            for (int i = 0; i < found.Length; i++)
            {
                files[Path.GetFileNameWithoutExtension(found[i])] = found[i];
            }
            Logger.LogInfo("Big Bang icons: " + files.Count + " icon(s) found");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Big Bang icons: could not read the icon folder: "
                + e.Message);
        }
    }

    // Returns null when there is no icon for this id, or it could not be read.
    internal static Sprite Get(string id)
    {
        if (id == null || files == null || files.Count == 0)
        {
            return null;
        }
        Sprite cached;
        if (cache.TryGetValue(id, out cached))
        {
            // A cached sprite can still have been destroyed underneath us, and
            // Unity's == null catches that; drop it and build a fresh one.
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
            // to say so in the log.
            files.Remove(id);
        }
        return sprite;
    }

    // Every Big Bang sprite the game ships is 100x100 with pixelsToUnits 100,
    // which is what decides a UI Image's native size: rect / pixelsToUnits.
    // Vanilla works out to 1, so the icon asks the layout for 100x100.
    //
    // Scaling pixelsToUnits with the texture keeps that ratio at 1 no matter
    // what resolution someone's PNG happens to be, so a 400x400 icon lands in
    // exactly the same box a vanilla 100x100 one would.
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

    // The upgrade card draws its title and description in a band that runs
    // under the icon's slot. Every Big Bang sprite the game ships is fully
    // opaque - a solid black square with the red frame painted on top - so
    // vanilla simply covers that text and nobody ever sees it.
    //
    // Art drawn as a normal PNG is transparent everywhere the pen did not go,
    // so anything behind it shows through. Flattening onto the same opaque
    // black the game uses makes our icons behave like its own. It also matters
    // for the mipmaps below: averaging a half transparent pixel with an opaque
    // one leaves a halo, and there is nothing to halo once every pixel is
    // opaque.
    //
    // Done here rather than baked into the PNGs so that any icon dropped into
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
    // The card draws these icons at roughly 64 screen pixels. The art is
    // 400x400, so the GPU is shrinking it more than six times. A texture with
    // no mipmaps gets point sampled when it is shrunk that far: the shader
    // reads one source pixel per screen pixel and everything between those
    // samples is simply not there.
    //
    // That is fine for a thick shape and fatal for a thin one. These icons
    // have a 3 pixel border sitting on the outermost edge - under 1% of the
    // width, where the game's own icons use about 2% - so at 64 pixels the
    // border is thinner than a single screen pixel. Whether it survives comes
    // down to where the pixel grid happens to land, which is why it would
    // disappear from one edge and not the other. Measured on our own files:
    // point sampled, all four edges come out pure black; averaged, all four
    // keep the border.
    //
    // Mipmaps are the fix. They hand the GPU a properly averaged smaller copy
    // instead of making it guess from one sample, so a border too thin to land
    // on becomes a fainter but present line. LoadImage does not reliably give
    // a mip chain no matter what the texture was created with, so the pixels
    // are copied into a texture that definitely has one and Apply builds it.
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
            // Trilinear rather than Bilinear so the step between two mip
            // levels is blended instead of popping as the card resizes.
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
                Log.LogWarning("Big Bang icons: '" + Path.GetFileName(path)
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
            Log.LogWarning("Big Bang icons: could not load '"
                + Path.GetFileName(path) + "': " + e.Message);
            return null;
        }
    }
}

// UpdateUI sets image.sprite from the atlas on every refresh, so the swap has
// to happen after it, every time - not once when the row is built.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class BigBangUpgradeItemIconPatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");

    private static readonly FieldInfo ImageField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "image");

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        try
        {
            if (UpgradeSetField == null || ImageField == null)
            {
                return;
            }
            BigBangUpgradeSet set =
                UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            if (set == null)
            {
                return;
            }
            Sprite sprite = BigBangIconsPlugin.Get(set.id);
            if (sprite == null)
            {
                return; // no icon for this line; leave the game's own alone
            }
            Image image = ImageField.GetValue(__instance) as Image;
            if (image == null || image.sprite == sprite)
            {
                return;
            }
            image.sprite = sprite;
        }
        catch (Exception e)
        {
            // Swallowed on purpose: this runs on every upgrade row refresh, so
            // a throw here would spam the log and could break the panel. A
            // missing icon is not worth that.
            BigBangIconsPlugin.Log.LogWarning(
                "Big Bang icons: could not set an icon: " + e.Message);
        }
    }
}

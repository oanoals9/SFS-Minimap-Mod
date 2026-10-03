using System;
using System.Collections.Generic;
using SFS.World;
using SFS.World.Maps;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// The pictures the map is made of. Everything is taken from the game's own map view, so the
    /// minimap looks like the original map; whatever cannot be read is replaced by a texture that is
    /// generated once at startup, so the window always has something to show.
    ///
    /// Nothing here writes to the game; the sprites are used as they are and the material is copied
    /// before it is touched, otherwise our line width would follow the original map's zoom.
    /// </summary>
    public static class MmAssets
    {
        public static Sprite WhiteSprite;        // filled disc, as used for the planet in the map view
        public static Sprite SoiSprite;          // ring, as used for a sphere of influence
        public static Sprite ShipSprite;         // vehicle marker
        public static Sprite ShadedSprite;       // disc shaded like a ball, for the planet
        public static Material LineMaterial;     // copy of the original map line material
        public static LineRenderer LineTemplate; // the original map line, for width curve / texture mode
        public static Font TextFont;             // the font the original map labels use

        public static string WhiteSource = "none";
        public static string SoiSource = "none";
        public static string ShipSource = "none";
        public static string LineSource = "none";

        private static bool loaded;

        /// <summary>Sprites this mod created itself, so that they (and only they) are cleaned up when
        /// the scene goes away.</summary>
        private static readonly List<Sprite> generatedSprites = new List<Sprite>();

        public static bool Loaded
        {
            get { return loaded; }
        }

        public static void Load()
        {
            if (loaded)
                return;

            loaded = true;

            try
            {
                MapEnvironment environment = Map.environment;
                if (environment != null)
                {
                    WhiteSprite = environment.white_Sprite;
                    SoiSprite = environment.SOI_Sprite;
                    WhiteSource = WhiteSprite != null ? "game (MapEnvironment.white_Sprite)" : "none";
                    SoiSource = SoiSprite != null ? "game (MapEnvironment.SOI_Sprite)" : "none";
                }
                else
                {
                    WhiteSource = "Map.environment is null";
                    SoiSource = "Map.environment is null";
                }
            }
            catch (Exception e)
            {
                MmLog.Warn("the map sprites could not be read (" + e.Message + "), generated ones are used");
            }

            if (WhiteSprite == null)
            {
                WhiteSprite = GeneratedDisc();
                WhiteSource = "generated disc";
            }

            if (SoiSprite == null)
            {
                SoiSprite = GeneratedRing();
                SoiSource = "generated ring";
            }

            // Always built: it is one 64x64 texture, and the player can switch to it at any time.
            ShadedSprite = GeneratedShadedDisc();

            LoadLineMaterial();
            LoadShipSprite();

            MmLog.Detail("assets: planet=" + WhiteSource + ", soi=" + SoiSource +
                         ", ship=" + ShipSource + ", line=" + LineSource);
        }

        /// <summary>
        /// Called once per second or so for the first seconds of a flight. The game's map view may not
        /// exist yet on the very first frame, in which case the generated fallbacks were used; this
        /// picks up the real thing as soon as it is there.
        /// </summary>
        public static void TryReload()
        {
            if (loaded && WhiteSource.StartsWith("game") && LineSource.StartsWith("copy"))
                return;

            loaded = false;
            Reset();
            Load();
        }

        public static void Reset()
        {
            loaded = false;
            DestroyGenerated(WhiteSprite);
            DestroyGenerated(SoiSprite);
            DestroyGenerated(ShipSprite);
            DestroyGenerated(ShadedSprite);
            WhiteSprite = null;
            SoiSprite = null;
            ShipSprite = null;
            ShadedSprite = null;
            LineTemplate = null;
            TextFont = null;
            WhiteSource = "none";
            SoiSource = "none";
            ShipSource = "none";
            LineSource = "none";

            // Our own copy of the line material points at a texture that belongs to the scene that is
            // being unloaded, so it goes as well.
            if (LineMaterial != null)
            {
                UnityEngine.Object.Destroy(LineMaterial);
                LineMaterial = null;
            }
        }

        /// <summary>Destroys a sprite only when this mod created it; the game's own sprites belong to
        /// the game.</summary>
        private static void DestroyGenerated(Sprite sprite)
        {
            if (sprite == null)
                return;

            try
            {
                if (generatedSprites.Contains(sprite))
                {
                    generatedSprites.Remove(sprite);
                    Texture2D texture = sprite.texture;
                    UnityEngine.Object.Destroy(sprite);
                    if (texture != null)
                        UnityEngine.Object.Destroy(texture);
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("a generated sprite could not be destroyed: " + e.Message);
            }
        }

        private static void LoadLineMaterial()
        {
            try
            {
                LineDrawer drawer = Map.solidLine;
                if (drawer == null || drawer.linePrefab == null)
                {
                    LineSource = "Map.solidLine is null";
                }
                else
                {
                    LineRenderer template = drawer.linePrefab.GetComponent<LineRenderer>();
                    if (template != null)
                    {
                        LineTemplate = template;
                        Material original = template.sharedMaterial;
                        if (original != null)
                        {
                            // A copy on purpose: the original map changes its own texture scale while
                            // zooming, and the minimap must not follow that.
                            LineMaterial = new Material(original);
                            LineSource = "copy of " + original.name;
                        }
                        else
                        {
                            LineSource = "line prefab has no material";
                        }
                    }
                    else
                    {
                        LineSource = "line prefab has no LineRenderer";
                    }
                }
            }
            catch (Exception e)
            {
                LineSource = "failed: " + e.Message;
            }

            if (LineMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                    shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
                if (shader != null)
                {
                    LineMaterial = new Material(shader);
                    LineSource += " (generated from " + shader.name + ")";
                }
                else
                {
                    LineSource += " (no shader found - lines may not be visible)";
                }
            }
        }

        private static void LoadShipSprite()
        {
            try
            {
                PlayerController controller = PlayerController.main;
                if (MmLib.IsNull(controller) || MmLib.IsNull(controller.player))
                {
                    ShipSource = "no player";
                }
                else
                {
                    Player player = controller.player.Value;
                    MapPlayer mapPlayer = MmLib.IsNull(player) ? null : player.mapPlayer;
                    MapIcon icon = MmLib.IsNull(mapPlayer) ? null : mapPlayer.mapIcon;
                    GameObject iconObject = MmLib.IsNull(icon) ? null : icon.mapIcon;

                    if (MmLib.IsNull(iconObject))
                    {
                        ShipSource = "the vehicle has no map icon";
                    }
                    else
                    {
                        SpriteRenderer renderer = iconObject.GetComponentInChildren<SpriteRenderer>(true);
                        if (renderer != null && renderer.sprite != null)
                        {
                            ShipSprite = renderer.sprite;
                            ShipSource = "game (vehicle map icon)";
                        }
                        else
                        {
                            ShipSource = "the map icon has no sprite";
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ShipSource = "failed: " + e.Message;
            }

            if (ShipSprite == null)
            {
                ShipSprite = GeneratedTriangle();
                ShipSource += " -> generated triangle";
            }
        }

        // ------------------------------------------------------------------ generated fallbacks

        private const int TexSize = 64;

        private static Sprite MakeSprite(Texture2D texture)
        {
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                          new Vector2(0.5f, 0.5f), texture.width);
            generatedSprites.Add(sprite);
            return sprite;
        }

        private static Texture2D NewTexture()
        {
            Texture2D texture = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private static Sprite GeneratedDisc()
        {
            Texture2D texture = NewTexture();
            Color[] pixels = new Color[TexSize * TexSize];
            float aa = 2f / TexSize;
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = ((x + 0.5f) / TexSize) * 2f - 1f;
                    float dy = ((y + 0.5f) / TexSize) * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01((1f - d) / aa);
                    pixels[y * TexSize + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return MakeSprite(texture);
        }

        /// <summary>
        /// A disc that reads as a ball: the texture is a grey sphere lit from the upper left, and the
        /// planet's own colour is used as a tint over it, so the result is a shaded planet in the
        /// colour the game gives that planet. This is what the reference picture shows.
        /// </summary>
        private static Sprite GeneratedShadedDisc()
        {
            // Light direction: the reference picture is lit from the upper left and a little in front.
            Vector3 light = new Vector3(-0.45f, 0.55f, 0.70f).normalized;

            Texture2D texture = NewTexture();
            Color[] pixels = new Color[TexSize * TexSize];
            float aa = 2f / TexSize;

            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = ((x + 0.5f) / TexSize) * 2f - 1f;
                    float dy = ((y + 0.5f) / TexSize) * 2f - 1f;
                    float squared = dx * dx + dy * dy;

                    float alpha = Mathf.Clamp01((1f - Mathf.Sqrt(squared)) / aa);
                    if (alpha <= 0f)
                    {
                        pixels[y * TexSize + x] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    // The visible point of a sphere seen from straight on sits at this depth.
                    float z = Mathf.Sqrt(Mathf.Max(0f, 1f - squared));
                    float lambert = Mathf.Max(0f, dx * light.x + dy * light.y + z * light.z);

                    float brightness = 0.16f + 0.84f * Mathf.Pow(lambert, 0.9f);
                    brightness = Mathf.Clamp01(brightness);

                    pixels[y * TexSize + x] = new Color(brightness, brightness, brightness, alpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return MakeSprite(texture);
        }

        private static Sprite GeneratedRing()
        {
            Texture2D texture = NewTexture();
            Color[] pixels = new Color[TexSize * TexSize];
            float aa = 2f / TexSize;
            const float thickness = 0.03f;
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = ((x + 0.5f) / TexSize) * 2f - 1f;
                    float dy = ((y + 0.5f) / TexSize) * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float outer = Mathf.Clamp01((1f - d) / aa);
                    float inner = Mathf.Clamp01((d - (1f - thickness)) / aa);
                    float alpha = Mathf.Min(outer, inner);
                    pixels[y * TexSize + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return MakeSprite(texture);
        }

        private static Sprite GeneratedTriangle()
        {
            const float half = 0.09f;      // half width of the triangle, in sprite units
            const float top = 0.5f;
            const float bottom = -0.5f;

            Texture2D texture = NewTexture();
            Color[] pixels = new Color[TexSize * TexSize];
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float u = ((x + 0.5f) / TexSize) - 0.5f;   // -0.5 .. 0.5
                    float v = ((y + 0.5f) / TexSize) - 0.5f;

                    float width = Mathf.Lerp(half, 0.01f, Mathf.InverseLerp(top, bottom, v));
                    bool inside = v <= top && v >= bottom && Mathf.Abs(u) <= width;
                    pixels[y * TexSize + x] = inside ? Color.white : new Color(1f, 1f, 1f, 0f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return MakeSprite(texture);
        }

        // ------------------------------------------------------------------ diagnostics

        public static string Describe()
        {
            return "planet=" + WhiteSource + " soi=" + SoiSource + " ship=" + ShipSource +
                   " line=" + LineSource + " font=" + (TextFont != null ? TextFont.name : "none");
        }
    }
}

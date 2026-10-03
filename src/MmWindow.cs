using System;
using SFS.UI.ModGUI;
using TMPro;
using UITools;
using UnityEngine;
using UnityEngine.UI;
using Type = SFS.UI.ModGUI.Type;

namespace SFSMiniMap
{
    /// <summary>
    /// The window itself: a title, the altitude of the apoapsis on top, the map in the middle, the
    /// altitude of the periapsis below. The map is an ordinary RawImage that shows the render texture
    /// of <see cref="MmRenderer"/>.
    ///
    /// The window is built from the game's own GUI prefabs (SFS.UI.ModGUI.Builder, with UITools'
    /// closable window when it is available), so it matches the rest of the game's windows.
    /// </summary>
    public static class MmWindow
    {
        private const string Title = "Mini Map";
        private const float RowSpacing = 6f;

        /// <summary>The size the altitude and info lines would like to use. It is only a wish: the
        /// lines are never allowed to be bigger than the window's own title, which is why the size
        /// actually used is worked out from the title at run time (see <see cref="ChooseFontSize"/>).</summary>
        private const float DesiredLabelFontSize = 24f;

        /// <summary>Used when the title's own size cannot be read. Deliberately below the wish above,
        /// so that a failed measurement cannot produce lines bigger than the title.</summary>
        private const float FallbackTitleFontSize = 22f;
        private const float MinLabelFontSize = 12f;

        /// <summary>Everything above the map except the info line: the title bar, the padding and the
        /// spacing between rows.</summary>
        private const int ChromeHeight = 114;

        /// <summary>How much taller than its text a line is, so that taller letters are not clipped.</summary>
        private const int LinePadding = 8;

        private static float labelFontSize = DesiredLabelFontSize;
        private static int rowHeight = Mathf.CeilToInt(DesiredLabelFontSize) + LinePadding;

        private static bool hasApoapsis = true;
        private static bool hasPeriapsis = true;

        private static GameObject holder;
        private static Window window;
        private static RawImage mapImage;
        private static RectTransform mapRect;
        private static LayoutElement mapLayout;
        private static Label apLabel;
        private static Label peLabel;

        public static bool Created { get { return window != null; } }
        public static GameObject Holder { get { return holder; } }

        // ------------------------------------------------------------------ lifetime

        public static void Create()
        {
            Destroy();

            try
            {
                int width = Mathf.Max(120, (int)MmConfig.Width);
                int height = Mathf.Max(120, (int)MmConfig.Height);
                float opacity = MmConfig.Opacity;

                holder = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "SFS Mini Map");
                if (holder == null)
                {
                    MmLog.Error("the GUI holder could not be created");
                    return;
                }

                holder.AddComponent<MmRuntime>();

                int id = Builder.GetRandomID();

                // Prefer UITools' closable window, but never depend on it.
                try
                {
                    window = UIToolsBuilder.CreateClosableWindow(
                        holder.transform, id, width, height, 0, 0, true, true, opacity, Title);
                }
                catch (Exception e)
                {
                    MmLog.Warn("UITools' window failed (" + e.Message + "), using the game's own window");
                    window = null;
                }

                if (window == null)
                {
                    window = Builder.CreateWindow(
                        holder.transform, id, width, height, 0, 0, true, true, opacity, Title);
                }

                if (window == null)
                {
                    MmLog.Error("the window could not be created");
                    return;
                }

                try
                {
                    PositionSaver.RegisterPermanentSaving(window, "SFSMiniMap World");
                }
                catch (Exception e)
                {
                    MmLog.Detail("the window position will not be remembered: " + e.Message);
                }

                window.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, RowSpacing,
                                         new RectOffset(8, 8, 8, 8), true);

                ChooseFontSize();

                int rowWidth = Mathf.Max(80, width - 20);

                apLabel = Builder.CreateLabel(window, rowWidth, rowHeight, 0, 0, "Apoapsis --");
                Style(apLabel, TextAlignmentOptions.Center);
                SetText(apLabel, "Apoapsis --");

                CreateMap(rowWidth);

                peLabel = Builder.CreateLabel(window, rowWidth, rowHeight, 0, 0, "Periapsis --");
                Style(peLabel, TextAlignmentOptions.Center);
                SetText(peLabel, "Periapsis --");

                ApplySettings();
                MmLog.Detail("window created (" + width + "x" + height + ")");
            }
            catch (Exception e)
            {
                MmLog.Error("creating the window failed: " + e);
                Destroy();
            }
        }

        /// <summary>
        /// The map is a plain GameObject with a RectTransform instead of one of the game's prefabs,
        /// because the window has to show a texture and nothing else. A LayoutElement is what tells
        /// the window's layout group how tall this row is.
        /// </summary>
        private static void CreateMap(int rowWidth)
        {
            int size = Mathf.Min(MmConfig.MapSize, Mathf.Max(64, rowWidth));

            GameObject mapObject = new GameObject("Mini Map View", typeof(RectTransform));
            mapObject.transform.SetParent(window.ChildrenHolder, false);
            mapObject.layer = LayerMask.NameToLayer("UI") >= 0 ? LayerMask.NameToLayer("UI") : 5;

            mapRect = (RectTransform)mapObject.transform;
            mapRect.sizeDelta = new Vector2(size, size);
            mapRect.localScale = Vector3.one;

            mapLayout = mapObject.AddComponent<LayoutElement>();
            mapLayout.preferredWidth = size;
            mapLayout.preferredHeight = size;
            mapLayout.minWidth = size;
            mapLayout.minHeight = size;

            mapImage = mapObject.AddComponent<RawImage>();
            mapImage.color = Color.white;
            mapImage.raycastTarget = false;
        }

        private static void Style(Label label, TextAlignmentOptions alignment)
        {
            if (label == null)
                return;

            try
            {
                label.TextAlignment = alignment;
                label.AutoFontResize = false;
                label.FontSize = labelFontSize;
            }
            catch (Exception e)
            {
                MmLog.Detail("a label could not be styled: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ the line size

        /// <summary>What the title's text is actually set to, so that our own lines can stop short of
        /// it, or 0 when it cannot be read.</summary>
        public static float TitleFontSize { get; private set; }

        /// <summary>What the altitude and info lines ended up using, for the report.</summary>
        public static float LabelFontSize { get { return labelFontSize; } }

        /// <summary>
        /// Picks the size of the three text lines that are not the title. They are asked to be
        /// <see cref="DesiredLabelFontSize"/>, but never larger than the title: this is measured from
        /// the title the game itself built, rather than guessed, because the prefab decides it.
        /// </summary>
        private static void ChooseFontSize()
        {
            TitleFontSize = MeasureTitleFontSize();

            float size = TitleFontSize > 0f ? Mathf.Min(DesiredLabelFontSize, TitleFontSize)
                                            : Mathf.Min(DesiredLabelFontSize, FallbackTitleFontSize);

            labelFontSize = Mathf.Max(MinLabelFontSize, size);
            rowHeight = Mathf.CeilToInt(labelFontSize) + LinePadding;

            MmLog.Detail("line size: " + labelFontSize.ToString("0.#") + " (the title is " +
                         (TitleFontSize > 0f ? TitleFontSize.ToString("0.#") : "unreadable") + ")");
        }

        /// <summary>
        /// How big the window title is. The title is the prefab's own text object, a child named
        /// "Title" (that is how SFS.UI.ModGUI.Window finds it too), and it is a TextMeshPro text or,
        /// on older prefabs, a legacy Text. Anything that goes wrong here is not worth a warning: a
        /// slightly wrong line size is not a failure, so the fallback is simply used.
        /// </summary>
        private static float MeasureTitleFontSize()
        {
            try
            {
                if (window == null || window.gameObject == null)
                    return 0f;

                Transform title = window.gameObject.transform.Find("Title");
                if (title != null)
                {
                    TMP_Text tmp = title.GetComponent<TMP_Text>();
                    if (tmp != null && tmp.fontSize > 0f)
                        return tmp.fontSize;

                    UnityEngine.UI.Text legacy = title.GetComponent<UnityEngine.UI.Text>();
                    if (legacy != null && legacy.fontSize > 0)
                        return legacy.fontSize;
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the title's text size could not be read: " + e.Message);
            }

            return 0f;
        }

        private static void SetText(Label label, string text)
        {
            if (label == null)
                return;

            try
            {
                label.Text = text;
            }
            catch
            {
            }
        }

        public static void Destroy()
        {
            try
            {
                if (window != null)
                    UnityEngine.Object.Destroy(window.gameObject);
            }
            catch
            {
            }

            try
            {
                if (holder != null)
                    UnityEngine.Object.Destroy(holder);
            }
            catch
            {
            }

            window = null;
            holder = null;
            mapImage = null;
            mapRect = null;
            mapLayout = null;
            apLabel = null;
            peLabel = null;
        }

        // ------------------------------------------------------------------ per frame

        public static void ApplySettings()
        {
            if (window == null)
                return;

            try
            {
                window.WindowOpacity = MmConfig.Opacity;
            }
            catch
            {
            }

            int size = Mathf.Max(64, MmConfig.MapSize);

            try
            {
                if (mapRect != null)
                    mapRect.sizeDelta = new Vector2(size, size);
                if (mapLayout != null)
                {
                    mapLayout.preferredWidth = size;
                    mapLayout.preferredHeight = size;
                    mapLayout.minWidth = size;
                    mapLayout.minHeight = size;
                }
            }
            catch
            {
            }

            // The window has to be tall enough for the map plus the lines around it. It is not left
            // to the height setting alone, because a window shorter than its contents clips the map:
            // at the default of 300 the bottom of a 256 pixel map was cut off.
            Resize();
        }

        /// <summary>
        /// Shows or hides the two altitude lines. When there is no orbit there is nothing to put in
        /// them, and the game's map shows nothing either, so they are taken away rather than left
        /// sitting there reading "Apoapsis --" - and the window shrinks to match.
        /// </summary>
        public static void SetApsidesVisible(bool apoapsis, bool periapsis)
        {
            if (apoapsis == hasApoapsis && periapsis == hasPeriapsis)
                return;

            hasApoapsis = apoapsis;
            hasPeriapsis = periapsis;

            if (apLabel != null)
                apLabel.Active = apoapsis;
            if (peLabel != null)
                peLabel.Active = periapsis;

            Resize();
        }

        private static void Resize()
        {
            if (window == null)
                return;

            try
            {
                int size = Mathf.Max(64, MmConfig.MapSize);
                int width = Mathf.Max(120, (int)MmConfig.Width);

                // The window is exactly as tall as what is in it: the map, the lines above and below
                // it, and the title bar. The height setting is deliberately NOT used any more - it
                // used to be a floor, and a value larger than the contents (787, say) left the window
                // with a big empty area under the map. Whatever does not fit is still protected
                // against: the height is worked out from the contents, so it can never clip them.
                int around = ChromeHeight;
                if (hasApoapsis)
                    around += rowHeight + (int)RowSpacing;
                if (hasPeriapsis)
                    around += rowHeight + (int)RowSpacing;

                int height = size + around;

                if (Mathf.Abs(window.Size.y - height) > 0.5f || Mathf.Abs(window.Size.x - width) > 0.5f)
                    window.Size = new Vector2(width, height);
            }
            catch (Exception e)
            {
                MmLog.Detail("the window could not be resized to fit the map: " + e.Message);
            }
        }

        public static void SetTexture(Texture texture)
        {
            if (mapImage == null || texture == null)
                return;

            if (mapImage.texture != texture)
                mapImage.texture = texture;
        }

        public static void SetVisible(bool visible)
        {
            if (window == null)
                return;

            try
            {
                window.Active = visible;
            }
            catch (Exception e)
            {
                MmLog.Detail("the window could not be shown/hidden: " + e.Message);
            }

            if (mapImage != null)
                mapImage.enabled = visible;
        }

        public static void SetApoapsis(string text)
        {
            SetText(apLabel, text);
        }

        public static void SetPeriapsis(string text)
        {
            SetText(peLabel, text);
        }

        public static string Describe()
        {
            if (window == null)
                return "not created";

            string text = "window " +
                          (window.gameObject != null
                              ? (window.gameObject.activeInHierarchy ? "visible" : "hidden")
                              : "?");

            try
            {
                text += ", holder " + (holder != null ? holder.name : "none") +
                        " under " + (holder != null && holder.transform.parent != null
                            ? holder.transform.parent.name : "?");
            }
            catch
            {
                text += ", holder unreadable";
            }

            if (mapImage == null || mapRect == null)
                return text + ", map image missing";

            text += ", map image " +
                    (mapImage.enabled ? "enabled" : "disabled") +
                    ", texture " + (mapImage.texture != null ? mapImage.texture.width + "px" : "none") +
                    ", size " + mapRect.rect.width.ToString("0") + "x" + mapRect.rect.height.ToString("0");

            try
            {
                Canvas canvas = mapImage.canvas;
                Camera canvasCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera
                    : null;

                Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvasCamera, mapRect.position);
                text += ", centre at screen " + screen.x.ToString("0") + "," + screen.y.ToString("0") +
                        " of " + Screen.width + "x" + Screen.height +
                        ", canvas " + (canvas != null ? canvas.name : "none");
            }
            catch (Exception e)
            {
                text += ", screen position unreadable: " + e.Message;
            }

            return text;
        }
    }
}

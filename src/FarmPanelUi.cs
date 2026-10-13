using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace FesticalAutoFarm
{
    internal sealed class FarmPanelUi
    {
        private const float PanelX = 20f;
        private const float PanelY = 20f;
        private const float PanelWidth = 320f;
        private const float PanelHeight = 540f;
        private const float SlotRowHeight = 72f;
        private const float SpinnerInterval = 0.15f;

        private const string ModName = "FESTICAL AUTO FARM";
        private const string ModVersion = "1.0.0";
        private const string TelegramHandle = "@DoomsdayFTGroup";
        private const string YouTubeChannel = "DoomsdayFT";

        // Cohesive dark-slate + teal palette.
        private static readonly Color BackgroundColor = new Color(0.055f, 0.078f, 0.125f, 1f);
        private static readonly Color SurfaceColor = new Color(0.086f, 0.118f, 0.18f, 1f);
        private static readonly Color CardColor = new Color(0.106f, 0.145f, 0.212f, 1f);
        private static readonly Color CardSelectedColor = new Color(0.129f, 0.196f, 0.278f, 1f);
        private static readonly Color AccentColor = new Color(0.176f, 0.831f, 0.749f, 1f);
        private static readonly Color AccentSoftColor = new Color(0.129f, 0.6f, 0.56f, 1f);
        private static readonly Color FarmingColor = new Color(0.133f, 0.773f, 0.369f, 1f);
        private static readonly Color DangerColor = new Color(0.973f, 0.443f, 0.443f, 1f);
        private static readonly Color TextColor = new Color(0.902f, 0.929f, 0.953f, 1f);
        private static readonly Color MutedTextColor = new Color(0.58f, 0.64f, 0.72f, 1f);
        private static readonly Color DisabledButtonColor = new Color(0.22f, 0.26f, 0.31f, 1f);

        private static readonly string[] ResourceNames = { "Oil", "Wood", "Food", "Steel" };
        private static readonly string[] ResourceImageFiles =
            { "oil.png", "wood.png", "food.png", "steel.png" };
        private static readonly char[] SpinnerFrames = { '|', '/', '-', '\\' };
        private static readonly Dictionary<string, Texture2D> ResourceTextureCache =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        private FarmSettings _boundSettings;
        private string[] _targetTexts = Array.Empty<string>();
        private string[] _troopCountTexts = Array.Empty<string>();
        private string _statusMessage;
        private bool _statusMessageIsError;
        private float _spinnerTime;
        private bool _showLiveNodes;
        private int _liveNodePage;
        private const int LiveNodePageSize = 16;

        public bool Visible { get; set; }
        public KeyCode ToggleKey { get; } = KeyCode.F8;

        internal static bool IsPointerInsidePanel(Vector2 guiPosition)
        {
            return new Rect(PanelX, PanelY, PanelWidth, PanelHeight).Contains(guiPosition);
        }

        internal bool IsScreenPointerInsidePanel()
        {
            Vector3 mousePosition = Input.mousePosition;
            return Visible && IsPointerInsidePanel(
                new Vector2(mousePosition.x, Screen.height - mousePosition.y));
        }

        public void Draw(
            WorldMapScanResult mapScan,
            IReadOnlyList<NearestResourceNodeInfo> liveResourceNodes,
            FarmSettings settings,
            Func<string> startFarm,
            Func<string> stopFarm,
            bool farmRunning,
            string farmStatus,
            Func<FarmSettings, bool> saveSettings,
            Func<int, bool> loadSquad,
            string squadCaptureStatus)
        {
            if (!Visible)
                return;

            _spinnerTime += Time.unscaledDeltaTime;
            BindSettings(settings);

            DrawPanelFrame();
            DrawHeader(liveResourceNodes == null ? 0 : liveResourceNodes.Count);
            DrawSettings(
                mapScan, liveResourceNodes, settings, startFarm, stopFarm,
                farmRunning, farmStatus, saveSettings, loadSquad, squadCaptureStatus);

            ConsumePointerEvent();
        }

        private void DrawPanelFrame()
        {
            // Fully opaque: draw a solid tinted texture so nothing behind the panel bleeds through.
            Color previousColor = GUI.color;
            GUI.color = BackgroundColor;
            GUI.DrawTexture(new Rect(PanelX, PanelY, PanelWidth, PanelHeight), SolidTexture);
            GUI.color = previousColor;
        }

        private static Texture2D _solidTexture;

        private static Texture2D SolidTexture
        {
            get
            {
                if (_solidTexture == null)
                {
                    Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    texture.SetPixel(0, 0, Color.white);
                    texture.Apply();
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    _solidTexture = texture;
                }
                return _solidTexture;
            }
        }

        // Draws a fully opaque colored rectangle (GUI.Box uses a semi-transparent
        // default skin texture, which lets the game bleed through behind the panel).
        private static void SolidRect(Rect rect, Color color)
        {
            Color previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, SolidTexture);
            GUI.color = previousColor;
        }

        // A compact button with a guaranteed solid fill. Because Il2Cpp GUIStyle has
        // no copy constructor (and an empty style draws no background), we paint the
        // fill ourselves and let GUI.Button only handle the label + hit test.
        private static bool AccentButton(Rect rect, string label, Color fill, bool enabled = true)
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = enabled;
            SolidRect(rect, enabled ? fill : DisabledButtonColor);
            bool clicked = GUI.Button(rect, label, SmallButton);
            GUI.enabled = previousEnabled;
            return clicked && enabled;
        }

        // Same idea but with custom content (e.g. a resource icon) instead of a label.
        private static bool AccentButtonContent(Rect rect, GUIContent content, Color fill)
        {
            SolidRect(rect, fill);
            return GUI.Button(rect, content, SmallButton);
        }

        private static GUIStyle _smallLabel;
        private static GUIStyle _smallHeaderLabel;
        private static GUIStyle _smallTextField;

        // Il2CppUnityEngine has no GUIStyle(GUIStyle) copy constructor, so each
        // compact style is built from an empty style and populated explicitly.
        // A real font is assigned (from the default skin) so CalcSize() returns
        // valid metrics; without a font CalcSize can report 0 and Fit() would
        // then fail to clamp long strings.
        private static GUIStyle SmallLabel
        {
            get
            {
                if (_smallLabel == null)
                {
                    _smallLabel = new GUIStyle();
                    _smallLabel.font = GUI.skin.font;
                    _smallLabel.fontSize = 11;
                    _smallLabel.alignment = TextAnchor.MiddleLeft;
                    _smallLabel.wordWrap = false;
                    _smallLabel.normal.textColor = Color.white;
                }
                return _smallLabel;
            }
        }

        private static GUIStyle SmallHeaderLabel
        {
            get
            {
                if (_smallHeaderLabel == null)
                {
                    _smallHeaderLabel = new GUIStyle();
                    _smallHeaderLabel.font = GUI.skin.font;
                    _smallHeaderLabel.fontSize = 15;
                    _smallHeaderLabel.fontStyle = FontStyle.Bold;
                    _smallHeaderLabel.alignment = TextAnchor.MiddleLeft;
                    _smallHeaderLabel.wordWrap = false;
                    _smallHeaderLabel.normal.textColor = Color.white;
                }
                return _smallHeaderLabel;
            }
        }

        // Buttons, toggles and the text field keep minimal empty styles because
        // Il2CppUnityEngine has no GUIStyle(GUIStyle) copy constructor; their fill
        // and border come from GUI.backgroundColor / GUI.color set at the call site.
        private static GUIStyle SmallTextField
        {
            get
            {
                if (_smallTextField == null)
                {
                    _smallTextField = new GUIStyle();
                    _smallTextField.font = GUI.skin.font;
                    _smallTextField.fontSize = 12;
                    _smallTextField.alignment = TextAnchor.MiddleLeft;
                    _smallTextField.normal.textColor = Color.white;
                }
                return _smallTextField;
            }
        }

        private static GUIStyle _smallButton;

        private static GUIStyle SmallButton
        {
            get
            {
                if (_smallButton == null)
                {
                    _smallButton = new GUIStyle();
                    _smallButton.font = GUI.skin.font;
                    _smallButton.fontSize = 11;
                    _smallButton.alignment = TextAnchor.MiddleCenter;
                    _smallButton.wordWrap = false;
                    _smallButton.normal.textColor = Color.white;
                }
                return _smallButton;
            }
        }

        private void DrawHeader(int liveNodeCount)
        {
            float headerX = PanelX;
            float headerY = PanelY;
            float headerW = PanelWidth;

            SolidRect(new Rect(headerX, headerY, headerW, 52f), new Color(0.055f, 0.098f, 0.145f, 1f));
            SolidRect(new Rect(headerX, headerY, 5f, 52f), AccentColor);

            GUI.color = AccentColor;
            GUI.Label(new Rect(headerX + 16f, headerY + 5f, 260f, 22f), ModName, SmallHeaderLabel);

            GUI.color = MutedTextColor;
            GUI.Label(new Rect(headerX + 16f, headerY + 28f, headerW - 130f, 18f),
                "Telegram " + TelegramHandle, SmallLabel);
            GUI.color = Color.white;

            if (AccentButton(
                new Rect(headerX + headerW - 104f, headerY + 27f, 96f, 18f),
                _showLiveNodes ? "Farm slots" : "Nodes " + liveNodeCount.ToString("N0", CultureInfo.InvariantCulture),
                _showLiveNodes ? AccentColor : AccentSoftColor))
            {
                _showLiveNodes = !_showLiveNodes;
            }

            float badgeW = 46f;
            float badgeH = 20f;
            float badgeX = headerX + headerW - badgeW - 12f;
            float badgeY = headerY + 6f;
            GUI.color = MutedTextColor;
            GUI.Label(new Rect(badgeX, badgeY + 1f, badgeW, badgeH), "v" + ModVersion, SmallLabel);
            GUI.color = Color.white;

            SolidRect(new Rect(headerX, headerY + 52f, headerW, 2f), AccentColor);
        }
        private void ConsumePointerEvent()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type == EventType.Used)
                return;

            switch (currentEvent.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                    if (IsPointerInsidePanel(currentEvent.mousePosition))
                    {
                        currentEvent.Use();
                    }
                    break;
            }
        }

        private void BindSettings(FarmSettings settings)
        {
            if (ReferenceEquals(_boundSettings, settings))
                return;

            _boundSettings = settings;
            _targetTexts = new string[settings.Slots.Count];
            _troopCountTexts = new string[settings.Slots.Count];
            for (int i = 0; i < settings.Slots.Count; i++)
            {
                _targetTexts[i] = settings.Slots[i].TargetAmount.ToString(CultureInfo.InvariantCulture);
                _troopCountTexts[i] = settings.Slots[i].TroopCount.ToString(CultureInfo.InvariantCulture);
            }
            _statusMessage = null;
        }

        private static string BuildHeroSummary(FarmSlotSettings slot)
        {
            string main = string.IsNullOrEmpty(slot.MainHeroName)
                ? "Not selected"
                : slot.MainHeroName;
            return string.IsNullOrEmpty(slot.SecondHeroName)
                ? main
                : main + " + " + slot.SecondHeroName;
        }

        private static string BuildTroopSummary(FarmSlotSettings slot)
        {
            if (slot.Troops == null || slot.Troops.Count == 0)
                return "Not selected";

            var parts = new List<string>(slot.Troops.Count);
            for (int i = 0; i < slot.Troops.Count; i++)
            {
                FarmTroopSelection troop = slot.Troops[i];
                if (troop == null)
                    continue;
                string troopName = string.IsNullOrEmpty(troop.Name)
                    ? "Troop " + troop.Id.ToString(CultureInfo.InvariantCulture)
                    : troop.Name;
                parts.Add(troopName + " x" + troop.Count.ToString("N0", CultureInfo.InvariantCulture));
            }

            return parts.Count == 0 ? "Not selected" : string.Join(", ", parts);
        }

        private static Texture2D GetResourceTexture(string fileName)
        {
            if (ResourceTextureCache.TryGetValue(fileName, out Texture2D cached))
                return cached;

            Texture2D texture = LoadTextureFromResource(fileName);
            ResourceTextureCache[fileName] = texture;
            return texture;
        }

        private static Texture2D LoadTextureFromResource(string fileName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = "FesticalAutoFarm.Pictures." + fileName;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return null;

                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }

                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(texture, data))
                    return texture;

                return null;
            }
        }

        private string GetSpinnerFrame()
        {
            int index = (int)(_spinnerTime / SpinnerInterval) % SpinnerFrames.Length;
            return SpinnerFrames[index].ToString();
        }

        // Truncates text so it never overflows the label width, without changing
        // the layout (Rect stays the same). Falls back to the original text when
        // measurement is unavailable so behavior stays safe.
        private static string Fit(string text, float maxWidth, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text) || style == null || maxWidth <= 0f)
                return text;

            float width = style.CalcSize(new GUIContent(text)).x;
            if (width <= maxWidth)
                return text;

            const string ellipsis = "...";
            float ellipsisWidth = style.CalcSize(new GUIContent(ellipsis)).x;
            int keep = text.Length - 1;
            while (keep > 0)
            {
                string candidate = text.Substring(0, keep) + ellipsis;
                if (style.CalcSize(new GUIContent(candidate)).x <= maxWidth)
                    return candidate;
                keep--;
            }

            return ellipsis;
        }

        private void DrawSettings(
            WorldMapScanResult mapScan,
            IReadOnlyList<NearestResourceNodeInfo> liveResourceNodes,
            FarmSettings settings,
            Func<string> startFarm,
            Func<string> stopFarm,
            bool farmRunning,
            string farmStatus,
            Func<FarmSettings, bool> saveSettings,
            Func<int, bool> loadSquad,
            string squadCaptureStatus)
        {
            int availableSlots = mapScan.HasMarchSlotInfo
                ? (int)Math.Min(mapScan.MaxMarchSlots, (uint)FarmSettingsStore.MaximumFarmSlots)
                : 0;

            float bodyX = PanelX + 8f;
            float bodyY = PanelY + 58f;
            float bodyW = PanelWidth - 16f;
            float bodyH = PanelHeight - 58f - 62f;

            SolidRect(new Rect(bodyX, bodyY, bodyW, bodyH), SurfaceColor);

            float stripY = bodyY + 6f;
            DrawStatusStrip(stripY, bodyX, bodyW, availableSlots, squadCaptureStatus);

            if (_showLiveNodes)
            {
                DrawLiveNodeList(
                    liveResourceNodes,
                    bodyX,
                    bodyY,
                    bodyW,
                    bodyH,
                    stripY);
            }
            else if (!mapScan.HasMarchSlotInfo)
            {
                GUI.color = MutedTextColor;
                GUI.Label(new Rect(bodyX + 14f, stripY + 22f, bodyW - 28f, 36f),
                    Fit("Open the world map to auto-load your march slots.", bodyW - 32f, SmallLabel), SmallLabel);
                GUI.color = Color.white;
            }
            else if (availableSlots == 0)
            {
                GUI.color = MutedTextColor;
                GUI.Label(new Rect(bodyX + 14f, stripY + 22f, bodyW - 28f, 36f),
                    Fit("No march slots available right now.", bodyW - 32f, SmallLabel), SmallLabel);
                GUI.color = Color.white;
            }
            else
            {
                List<SlotNodeAssignment> nodeAssignments = BuildSlotNodeAssignments(
                    mapScan, settings, availableSlots);
                float firstCardY = stripY + 20f;
                for (int i = 0; i < availableSlots; i++)
                {
                    DrawSlotCard(
                        mapScan, settings, i, firstCardY + i * SlotRowHeight, bodyX, bodyW,
                        nodeAssignments[i], loadSquad, farmRunning);
                }
            }

            DrawFooter(settings, startFarm, stopFarm, farmRunning, farmStatus, saveSettings, availableSlots);
        }

        private void DrawLiveNodeList(
            IReadOnlyList<NearestResourceNodeInfo> nodes,
            float bodyX,
            float bodyY,
            float bodyW,
            float bodyH,
            float stripY)
        {
            int count = nodes == null ? 0 : nodes.Count;
            GUI.color = MutedTextColor;
            GUI.Label(
                new Rect(bodyX + 12f, stripY + 2f, bodyW - 24f, 16f),
                count.ToString("N0", CultureInfo.InvariantCulture)
                    + " unique resource nodes discovered in loaded map areas, nearest first.",
                SmallLabel);

            if (count == 0)
            {
                GUI.Label(
                    new Rect(bodyX + 14f, stripY + 26f, bodyW - 28f, 32f),
                    "No resource nodes discovered yet. Move the camera over the world map.",
                    SmallLabel);
                GUI.color = Color.white;
                return;
            }

            int pageCount = (count + LiveNodePageSize - 1) / LiveNodePageSize;
            _liveNodePage = Math.Max(0, Math.Min(_liveNodePage, pageCount - 1));
            float rowsTop = stripY + 22f;
            for (int rowIndex = 0; rowIndex < LiveNodePageSize; rowIndex++)
            {
                int nodeIndex = _liveNodePage * LiveNodePageSize + rowIndex;
                if (nodeIndex >= count)
                    break;

                NearestResourceNodeInfo node = nodes[nodeIndex];
                string owner = node.OwnerId == 0 ? "free" : "owned";
                string row = (nodeIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + ". " + node.Resource
                    + " | " + node.Distance.ToString("F1", CultureInfo.InvariantCulture)
                    + " | " + node.Remaining.ToString("N0", CultureInfo.InvariantCulture)
                    + " | " + owner;
                GUI.Label(
                    new Rect(bodyX + 12f, rowsTop + rowIndex * 22f, bodyW - 24f, 20f),
                    Fit(row, bodyW - 30f, SmallLabel),
                    SmallLabel);
            }

            float controlsY = rowsTop + LiveNodePageSize * 22f + 2f;
            if (AccentButton(
                new Rect(bodyX + 12f, controlsY, 54f, 20f),
                "Previous",
                AccentSoftColor,
                _liveNodePage > 0))
            {
                _liveNodePage--;
            }

            GUI.color = MutedTextColor;
            GUI.Label(
                new Rect(bodyX + 70f, controlsY, bodyW - 140f, 20f),
                "Page " + (_liveNodePage + 1).ToString(CultureInfo.InvariantCulture)
                    + " / " + pageCount.ToString(CultureInfo.InvariantCulture),
                SmallLabel);
            GUI.color = Color.white;

            if (AccentButton(
                new Rect(bodyX + bodyW - 66f, controlsY, 54f, 20f),
                "Next",
                AccentSoftColor,
                _liveNodePage + 1 < pageCount))
            {
                _liveNodePage++;
            }

            GUI.color = Color.white;
        }

        private void DrawStatusStrip(
            float stripY,
            float bodyX,
            float bodyW,
            int availableSlots,
            string squadCaptureStatus)
        {
            GUI.color = MutedTextColor;
            GUI.Label(new Rect(bodyX + 14f, stripY, 140f, 18f),
                "Slots: " + (availableSlots == 0 ? "-" : availableSlots.ToString(CultureInfo.InvariantCulture))
                + " / " + FarmSettingsStore.MaximumFarmSlots, SmallLabel);
            GUI.color = Color.white;

            if (!string.IsNullOrEmpty(squadCaptureStatus))
            {
                GUI.color = AccentColor;
                float statusW = bodyW - 154f;
                GUI.Label(new Rect(bodyX + 140f, stripY, statusW, 18f),
                    Fit(squadCaptureStatus, statusW - 4f, SmallLabel), SmallLabel);
                GUI.color = Color.white;
            }
        }
        private void DrawSlotCard(
            WorldMapScanResult mapScan,
            FarmSettings settings,
            int index,
            float cardY,
            float bodyX,
            float bodyW,
            SlotNodeAssignment assignment,
            Func<int, bool> loadSquad,
            bool farmRunning)
        {
            FarmSlotSettings slot = settings.Slots[index];
            float cardX = bodyX + 6f;
            float cardW = bodyW - 12f;
            float cardH = SlotRowHeight - 6f;

            bool slotFarming = farmRunning && slot.Enabled;
            bool nodeMissing = slotFarming && (assignment == null || assignment.Node == null);

            SolidRect(new Rect(cardX, cardY, cardW, cardH), slot.Enabled ? CardSelectedColor : CardColor);
            SolidRect(new Rect(cardX, cardY, cardW, 2f), slotFarming ? FarmingColor : AccentSoftColor);

            // Row 1: slot label, toggle, farming/not-found badge, squad button
            GUI.color = TextColor;
            GUI.Label(new Rect(cardX + 6f, cardY + 4f, 30f, 14f), "Slot " + (index + 1), SmallLabel);
            GUI.color = Color.white;

            if (AccentButton(
                new Rect(cardX + 38f, cardY + 3f, 44f, 16f),
                slot.Enabled ? "ON" : "OFF",
                slot.Enabled ? FarmingColor : new Color(0.24f, 0.28f, 0.34f, 1f)))
            {
                slot.Enabled = !slot.Enabled;
            }

            if (slotFarming)
            {
                if (nodeMissing)
                {
                    GUI.color = DangerColor;
                    GUI.Label(new Rect(cardX + 88f, cardY + 4f, 120f, 14f),
                        Fit("resource not found!", 116f, SmallLabel), SmallLabel);
                }
                else
                {
                    GUI.color = FarmingColor;
                    GUI.Label(new Rect(cardX + 88f, cardY + 4f, 82f, 14f),
                        Fit("farming " + GetSpinnerFrame(), 78f, SmallLabel), SmallLabel);
                }
                GUI.color = Color.white;
            }

            if (AccentButton(
                new Rect(cardX + cardW - 86f, cardY + 2f, 80f, 18f), "Select Squad", AccentSoftColor))
            {
                if (loadSquad(index))
                    Visible = false;
            }

            // Row 2: resource icons
            for (int resourceIndex = 0; resourceIndex < ResourceImageFiles.Length; resourceIndex++)
            {
                bool selected = (int)slot.Resource == resourceIndex;
                Color iconFill = selected
                    ? AccentColor
                    : new Color(0.16f, 0.2f, 0.27f, 1f);
                Texture2D resourceTexture = GetResourceTexture(ResourceImageFiles[resourceIndex]);
                GUIContent resourceContent = resourceTexture != null
                    ? new GUIContent(string.Empty, resourceTexture, ResourceNames[resourceIndex])
                    : new GUIContent(ResourceNames[resourceIndex]);
                if (AccentButtonContent(
                    new Rect(cardX + 8f + resourceIndex * 32f, cardY + 21f, 28f, 20f),
                    resourceContent, iconFill))
                {
                    slot.Resource = (FarmResource)resourceIndex;
                }
            }

            // Row 3: node estimate
            GUI.color = MutedTextColor;
            float innerW = cardW - 12f;
            GUI.Label(new Rect(cardX + 6f, cardY + 43f, innerW, 11f),
                Fit(BuildNodeEstimate(mapScan, slot, assignment), innerW - 4f, SmallLabel), SmallLabel);

            // Row 4: hero/troop summary
            GUI.color = TextColor;
            string summary = "H: " + BuildHeroSummary(slot) + "  •  T: " + BuildTroopSummary(slot);
            GUI.Label(new Rect(cardX + 6f, cardY + 52f, innerW, 11f),
                Fit(summary, innerW - 4f, SmallLabel), SmallLabel);
            GUI.color = Color.white;
        }

        private static string BuildNodeEstimate(
            WorldMapScanResult mapScan,
            FarmSlotSettings slot,
            SlotNodeAssignment assignment)
        {
            if (!slot.Enabled)
                return "Slot disabled";

            if (mapScan == null || !mapScan.HasScanned || !mapScan.HasUsableNodeSnapshot)
                return "Open the world map to load nodes";

            string resourceName;
            if (!TryGetResourceName(slot, out resourceName))
                return "Invalid resource selection";

            if (assignment == null || assignment.Node == null)
                return "No unowned " + resourceName + " node available";

            NearestResourceNodeInfo node = assignment.Node;
            if (assignment.MeetsTarget)
                return "Target " + node.Distance.ToString("F1", CultureInfo.InvariantCulture)
                    + " away | " + node.Remaining.ToString("N0", CultureInfo.InvariantCulture) + " left";

            return "Low stock | " + node.Remaining.ToString("N0", CultureInfo.InvariantCulture)
                + " / " + slot.TargetAmount.ToString("N0", CultureInfo.InvariantCulture);
        }
        private void DrawFooter(
            FarmSettings settings,
            Func<string> startFarm,
            Func<string> stopFarm,
            bool farmRunning,
            string farmStatus,
            Func<FarmSettings, bool> saveSettings,
            int availableSlots)
        {
            float footerY = PanelY + PanelHeight - 56f;
            float buttonH = 24f;

            string statusLine = "Status: " + (string.IsNullOrEmpty(farmStatus) ? "Stopped" : farmStatus);
            GUI.color = farmRunning ? FarmingColor : MutedTextColor;
            GUI.Label(new Rect(PanelX + 12f, footerY, PanelWidth - 24f, 14f),
                Fit(statusLine, PanelWidth - 28f, SmallLabel), SmallLabel);
            GUI.color = Color.white;

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusMessageIsError ? DangerColor : AccentColor;
                GUI.Label(new Rect(PanelX + 12f, footerY + 14f, PanelWidth - 24f, 13f),
                    Fit(_statusMessage, PanelWidth - 28f, SmallLabel), SmallLabel);
                GUI.color = Color.white;
            }

            float x = PanelX + 12f;
            if (AccentButton(new Rect(x, footerY + 30f, 74f, buttonH), "Save",
                new Color(0.145f, 0.388f, 0.922f, 1f)))
                SaveSettings(settings, saveSettings, availableSlots);

            x += 79f;
            if (AccentButton(new Rect(x, footerY + 30f, 116f, buttonH), "Start Farming",
                new Color(0.086f, 0.639f, 0.29f, 1f), !farmRunning))
            {
                SaveSettings(settings, saveSettings, availableSlots);
                if (!_statusMessageIsError)
                    _statusMessage = startFarm();
            }

            x += 121f;
            if (AccentButton(new Rect(x, footerY + 30f, 74f, buttonH), "Stop",
                new Color(0.863f, 0.149f, 0.149f, 1f), farmRunning))
                _statusMessage = stopFarm();
        }

        private static List<SlotNodeAssignment> BuildSlotNodeAssignments(
            WorldMapScanResult mapScan,
            FarmSettings settings,
            int availableSlots)
        {
            var assignments = new List<SlotNodeAssignment>(availableSlots);
            var usedNodeIds = new HashSet<ulong>();
            for (int i = 0; i < availableSlots; i++)
                assignments.Add(null);

            if (mapScan == null
                || !mapScan.HasScanned
                || !mapScan.HasUsableNodeSnapshot)
            {
                return assignments;
            }

            for (int i = 0; i < availableSlots; i++)
            {
                FarmSlotSettings slot = settings.Slots[i];
                string resourceName;
                if (!slot.Enabled || !TryGetResourceName(slot, out resourceName))
                    continue;

                NearestResourceNodeInfo nearestSufficientNode = FindNearestAvailableNode(
                    mapScan.LoadedResourceNodes,
                    resourceName,
                    slot.TargetAmount,
                    usedNodeIds,
                    true);
                if (nearestSufficientNode != null)
                {
                    assignments[i] = new SlotNodeAssignment(nearestSufficientNode, true);
                    usedNodeIds.Add(nearestSufficientNode.EntityId);
                }
            }

            for (int i = 0; i < availableSlots; i++)
            {
                FarmSlotSettings slot = settings.Slots[i];
                if (!slot.Enabled || assignments[i] != null)
                    continue;

                string resourceName;
                if (!TryGetResourceName(slot, out resourceName))
                    continue;

                NearestResourceNodeInfo nearestAvailableNode = FindNearestAvailableNode(
                    mapScan.LoadedResourceNodes,
                    resourceName,
                    0,
                    usedNodeIds,
                    false);
                if (nearestAvailableNode != null)
                {
                    assignments[i] = new SlotNodeAssignment(nearestAvailableNode, false);
                    usedNodeIds.Add(nearestAvailableNode.EntityId);
                }
            }

            return assignments;
        }

        private static bool TryGetResourceName(FarmSlotSettings slot, out string resourceName)
        {
            int resourceIndex = slot == null ? -1 : (int)slot.Resource;
            if (resourceIndex < 0 || resourceIndex >= ResourceNames.Length)
            {
                resourceName = null;
                return false;
            }

            resourceName = ResourceNames[resourceIndex];
            return true;
        }

        private static NearestResourceNodeInfo FindNearestAvailableNode(
            List<NearestResourceNodeInfo> nodes,
            string resourceName,
            long targetAmount,
            HashSet<ulong> usedNodeIds,
            bool requireTarget)
        {
            NearestResourceNodeInfo nearest = null;
            for (int i = 0; i < nodes.Count; i++)
            {
                NearestResourceNodeInfo candidate = nodes[i];
                if (candidate.Resource != resourceName
                    || candidate.OwnerId != 0
                    || usedNodeIds.Contains(candidate.EntityId)
                    || (requireTarget && candidate.Remaining < targetAmount))
                {
                    continue;
                }

                if (nearest == null || candidate.DistanceSquared < nearest.DistanceSquared)
                    nearest = candidate;
            }

            return nearest;
        }

        private sealed class SlotNodeAssignment
        {
            public SlotNodeAssignment(NearestResourceNodeInfo node, bool meetsTarget)
            {
                Node = node;
                MeetsTarget = meetsTarget;
            }

            public NearestResourceNodeInfo Node { get; }
            public bool MeetsTarget { get; }
        }

        private void SaveSettings(
            FarmSettings settings,
            Func<FarmSettings, bool> saveSettings,
            int availableSlots)
        {
            for (int i = 0; i < availableSlots; i++)
            {
                int troopCount;
                if (!int.TryParse(_troopCountTexts[i], NumberStyles.None, CultureInfo.InvariantCulture, out troopCount)
                    || troopCount < 0
                    || troopCount > FarmSettingsStore.MaximumTroopCount)
                {
                    SetSettingsMessage("Slot " + (i + 1) + " troop count must be 0-"
                        + FarmSettingsStore.MaximumTroopCount.ToString(CultureInfo.InvariantCulture) + ".", true);
                    return;
                }
                settings.Slots[i].TroopCount = troopCount;
            }

            settings.Validate();
            if (saveSettings(settings))
                SetSettingsMessage("Settings saved.", false);
            else
                SetSettingsMessage("Could not save settings. See MelonLoader log.", true);
        }

        private void SetSettingsMessage(string message, bool isError)
        {
            _statusMessage = message;
            _statusMessageIsError = isError;
        }
    }
}

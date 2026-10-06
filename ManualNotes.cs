using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using Rewired;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        private const float AddMarkerHoldSeconds = 1.20f;
        private const float ManualMarkerBaseSize = 28.0f;

        private readonly Dictionary<string, ManualMapNote> _manualNotesById =
            new Dictionary<string, ManualMapNote>(StringComparer.Ordinal);

        private readonly Dictionary<string, Texture2D> _manualIconTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private readonly ManualMarkerMenuState[] _manualMarkerMenus =
        {
            new ManualMarkerMenuState(),
            new ManualMarkerMenuState()
        };

        private readonly ManualMarkerContextState[] _manualMarkerContexts =
        {
            new ManualMarkerContextState(),
            new ManualMarkerContextState()
        };

        private string _manualNotesSeed = String.Empty;
        private bool _manualNotesLoaded;
        private int _nextManualMarkerId = 1;

        private GUIStyle _manualMenuTitleStyle;
        private GUIStyle _manualMenuItemStyle;
        private GUIStyle _manualMenuSelectedStyle;
        private GUIStyle _manualMenuHelpStyle;
        private GUIStyle _manualLabelStyle;

        private ManualMarkerMenuNode _manualRootMenu;

        private void InitializeManualNotes()
        {
            BuildManualMenuDefinition();
            LoadManualMarkerAssets();
        }

        private void BuildManualMenuDefinition()
        {
            _manualRootMenu = MenuGroup(
                "Добавить отметку",
                MenuGroup(
                    "Материалы",
                    MarkerLeaf("Глина", "materials", "clay", "materials", "Глина"),
                    MarkerLeaf("Камни", "materials", "stones", "materials", "Камни"),
                    MarkerLeaf("Брёвна", "materials", "logs", "materials", "Брёвна"),
                    MarkerLeaf("Палки", "materials", "sticks", "materials", "Палки"),
                    MarkerLeaf("Доски", "materials", "planks", "materials", "Доски"),
                    MarkerLeaf("Металлолом", "materials", "scrap", "materials", "Металл"),
                    MarkerLeaf("Волокнистые листья", "materials", "fibrous", "materials", "Волокно"),
                    MarkerLeaf("Ткань / кожа", "materials", "cloth", "materials", "Ткань"),
                    MarkerLeaf("Прочее", "materials", "other", "materials", "Материалы")
                ),
                MenuGroup(
                    "Еда",
                    MarkerLeaf("Кокосы", "food", "coconuts", "dot", "Кокосы"),
                    MarkerLeaf("Фрукты", "food", "fruit", "dot", "Фрукты"),
                    MarkerLeaf("Рыба", "food", "fish", "dot", "Рыба"),
                    MarkerLeaf("Мясо", "food", "meat", "dot", "Мясо"),
                    MarkerLeaf("Провизия", "food", "supplies", "dot", "Еда")
                ),
                MenuGroup(
                    "Вода",
                    MarkerLeaf("Водосборник", "water", "still", "dot", "Вода"),
                    MarkerLeaf("Запас воды", "water", "reserve", "dot", "Вода")
                ),
                MenuGroup(
                    "Растения",
                    MarkerLeaf("Пипи", "plants", "pipi", "pipi", "Пипи"),
                    MarkerLeaf("Алоэ", "plants", "aloe", "aloe", "Алоэ"),
                    MarkerLeaf("Юкка", "plants", "yucca", "yucca", "Юкка"),
                    MarkerLeaf("Картофель", "plants", "potato", "potato", "Картофель"),
                    MarkerLeaf("Кура", "plants", "kura", "kura", "Кура"),
                    MarkerLeaf("Квувава", "plants", "quwawa", "quwawa", "Квувава"),
                    MarkerLeaf("Вавулавула", "plants", "wavulavula", "wavulavula", "Вавула")
                ),
                MenuGroup(
                    "Животные",
                    MarkerLeaf("Кабан", "animals", "boar", "boar", "Кабан"),
                    MarkerLeaf("Огромный краб", "animals", "giant_crab", "giant_crab", "Краб"),
                    MarkerLeaf("Змея", "animals", "snake", "snake", "Змея"),
                    MarkerLeaf("Акула", "animals", "shark", "shark", "Акула")
                ),
                MenuGroup(
                    "Снаряжение",
                    MarkerLeaf("Инструменты", "equipment", "tools", "dot", "Инструменты"),
                    MarkerLeaf("Оружие", "equipment", "weapons", "dot", "Оружие"),
                    MarkerLeaf("Топливо", "equipment", "fuel", "dot", "Топливо"),
                    MarkerLeaf("Навигация", "equipment", "nav", "dot", "Навигация"),
                    MarkerLeaf("Прочее", "equipment", "other", "dot", "Снаряжение")
                ),
                MenuGroup(
                    "Рельеф",
                    MarkerLeaf("Скала", "terrain", "cliff", "cliff", "Скала"),
                    MarkerLeaf("Гора", "terrain", "mountain", "mountain", "Гора"),
                    MarkerLeaf("Каменная арка", "terrain", "stone_arch", "stone_arch", "Арка"),
                    MarkerLeaf("Пещера", "terrain", "cave", "cave", "Пещера")
                ),
                MarkerLeaf("Укрытие", "basic", "shelter", "shelter", "Укрытие"),
                MarkerLeaf("Ящик", "basic", "crate", "crate", "Ящик"),
                MarkerLeaf("Костёр", "basic", "campfire", "campfire", "Костёр"),
                MenuGroup(
                    "Важное / прочее",
                    MarkerLeaf("Важно", "other", "important", "important", "Важно"),
                    MarkerLeaf("Точка", "other", "dot", "dot", "Точка")
                )
            );
        }

        private static ManualMarkerMenuNode MenuGroup(string title, params ManualMarkerMenuNode[] children)
        {
            ManualMarkerMenuNode node = new ManualMarkerMenuNode();
            node.Title = title;
            node.Children = new List<ManualMarkerMenuNode>(children ?? new ManualMarkerMenuNode[0]);
            return node;
        }

        private static ManualMarkerMenuNode MarkerLeaf(string title, string category, string subtype, string iconId, string shortLabel)
        {
            ManualMarkerMenuNode node = new ManualMarkerMenuNode();
            node.Title = title;
            node.Preset = new ManualMarkerPreset
            {
                Category = category,
                Subtype = subtype,
                IconId = iconId,
                Label = shortLabel
            };
            return node;
        }

        private void LoadManualMarkerAssets()
        {
            try
            {
                string pluginDirectory =
                    Path.GetDirectoryName(typeof(SplitMapPlugin).Assembly.Location);

                if (String.IsNullOrEmpty(pluginDirectory))
                {
                    Logger.LogWarning("Could not resolve plugin directory for manual note assets.");
                    return;
                }

                string sourceDirectory =
                    Path.Combine(pluginDirectory, "assets", "source-sheets");

                Texture2D basic = LoadTextureFromFile(Path.Combine(sourceDirectory, "map_icons_basic.png"), "Manual_Basic");
                Texture2D animals = LoadTextureFromFile(Path.Combine(sourceDirectory, "map_icons_animals.png"), "Manual_Animals");
                Texture2D plants = LoadTextureFromFile(Path.Combine(sourceDirectory, "map_icons_plants.png"), "Manual_Plants");
                Texture2D terrain = LoadTextureFromFile(Path.Combine(sourceDirectory, "map_icons_terrain.png"), "Manual_Terrain");

                if (basic != null)
                {
                    RegisterManualIcon("shelter", CropTopLeftRect(basic, 40, 127, 502, 506, "basic_shelter"));
                    RegisterManualIcon("crate", CropTopLeftRect(basic, 561, 192, 936, 497, "basic_crate"));
                    RegisterManualIcon("campfire", CropTopLeftRect(basic, 1008, 139, 1394, 531, "basic_campfire"));
                    RegisterManualIcon("materials", CropTopLeftRect(basic, 45, 619, 528, 920, "basic_materials"));
                    RegisterManualIcon("cross", CropTopLeftRect(basic, 595, 655, 848, 915, "basic_cross"));
                    RegisterManualIcon("important", CropTopLeftRect(basic, 981, 638, 1077, 909, "basic_important"));
                    RegisterManualIcon("dot", CropTopLeftRect(basic, 1213, 737, 1327, 840, "basic_dot"));
                }

                if (animals != null)
                {
                    RegisterManualIcon("boar", CropTopLeftRect(animals, 94, 112, 676, 498, "animal_boar"));
                    RegisterManualIcon("giant_crab", CropTopLeftRect(animals, 769, 95, 1361, 520, "animal_giant_crab"));
                    RegisterManualIcon("snake", CropTopLeftRect(animals, 115, 570, 564, 977, "animal_snake"));
                    RegisterManualIcon("shark", CropTopLeftRect(animals, 698, 607, 1368, 957, "animal_shark"));
                }

                if (plants != null)
                {
                    RegisterManualIcon("pipi", CropTopLeftRect(plants, 29, 146, 357, 511, "plant_pipi"));
                    RegisterManualIcon("aloe", CropTopLeftRect(plants, 389, 133, 733, 506, "plant_aloe"));
                    RegisterManualIcon("yucca", CropTopLeftRect(plants, 760, 63, 1070, 524, "plant_yucca"));
                    RegisterManualIcon("potato", CropTopLeftRect(plants, 1092, 168, 1407, 526, "plant_potato"));
                    RegisterManualIcon("kura", CropTopLeftRect(plants, 104, 578, 478, 987, "plant_kura"));
                    RegisterManualIcon("quwawa", CropTopLeftRect(plants, 547, 596, 967, 981, "plant_quwawa"));
                    RegisterManualIcon("wavulavula", CropTopLeftRect(plants, 1030, 640, 1353, 973, "plant_wavulavula"));
                }

                if (terrain != null)
                {
                    RegisterManualIcon("cliff", CropTopLeftRect(terrain, 60, 166, 681, 505, "terrain_cliff"));
                    RegisterManualIcon("mountain", CropTopLeftRect(terrain, 768, 62, 1407, 561, "terrain_mountain"));
                    RegisterManualIcon("stone_arch", CropTopLeftRect(terrain, 44, 548, 713, 1038, "terrain_stone_arch"));
                    RegisterManualIcon("cave", CropTopLeftRect(terrain, 754, 577, 1402, 1010, "terrain_cave"));
                }

                Logger.LogInfo("Manual note icon assets loaded: " + _manualIconTextures.Count);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to load manual note assets: " + ex.Message);
            }
        }

        private void RegisterManualIcon(string key, Texture2D texture)
        {
            if (String.IsNullOrEmpty(key) || texture == null)
                return;

            _manualIconTextures[key] = texture;
        }

        private Texture2D CropTopLeftRect(Texture2D source, int xMin, int yMin, int xMax, int yMax, string name)
        {
            if (source == null)
                return null;

            int width = Mathf.Max(1, xMax - xMin + 1);
            int height = Mathf.Max(1, yMax - yMin + 1);
            int bottomY = Mathf.Max(0, source.height - yMax - 1);

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(source.GetPixels(xMin, bottomY, width, height));
            texture.Apply();
            return texture;
        }

        private void EnsureManualNotesLoadedForCurrentWorld()
        {
            if (_world == null)
                return;

            string seed = ReadWorldSeed();
            if (_manualNotesLoaded && String.Equals(seed, _manualNotesSeed, StringComparison.Ordinal))
                return;

            _manualNotesLoaded = true;
            _manualNotesSeed = seed;
            _nextManualMarkerId = 1;
            _manualNotesById.Clear();

            try
            {
                string path = GetManualNotesPath(seed);
                if (!File.Exists(path))
                    return;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (String.IsNullOrEmpty(line))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 8)
                        continue;

                    ManualMapNote note = new ManualMapNote();
                    note.Id = parts[0];
                    note.IconId = parts[1];
                    note.Label = parts[2];
                    note.Category = parts[3];
                    note.Subtype = parts[4];
                    note.WorldPosition = new Vector3(
                        ParseInvariantFloat(parts[5]),
                        0.0f,
                        ParseInvariantFloat(parts[6]));
                    note.CrossedOut = parts[7] == "1";

                    if (!String.IsNullOrEmpty(note.Id))
                        _manualNotesById[note.Id] = note;

                    int parsedId;
                    if (!String.IsNullOrEmpty(note.Id) && note.Id.StartsWith("M", StringComparison.Ordinal) &&
                        Int32.TryParse(note.Id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId))
                    {
                        if (parsedId >= _nextManualMarkerId)
                            _nextManualMarkerId = parsedId + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to load manual notes: " + ex.Message);
            }
        }

        private void SaveManualNotes()
        {
            try
            {
                string path = GetManualNotesPath(ReadWorldSeed());
                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, ManualMapNote> pair in _manualNotesById)
                {
                    ManualMapNote note = pair.Value;
                    if (note == null)
                        continue;

                    sb.Append(note.Id ?? String.Empty).Append('\t')
                        .Append(note.IconId ?? String.Empty).Append('\t')
                        .Append((note.Label ?? String.Empty).Replace('\t', ' ')).Append('\t')
                        .Append(note.Category ?? String.Empty).Append('\t')
                        .Append(note.Subtype ?? String.Empty).Append('\t')
                        .Append(note.WorldPosition.x.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(note.WorldPosition.z.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(note.CrossedOut ? "1" : "0")
                        .AppendLine();
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to save manual notes: " + ex.Message);
            }
        }

        private string GetManualNotesPath(string seed)
        {
            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(Paths.ConfigPath, "StrandedDeepMap", "ManualMarkers", SafeFileName(seed) + ".txt");
        }

        private void HandleZoomOutAndAddMarkerButton(
            int playerIndex,
            Joystick joystick,
            RawGamepadProfile profile,
            MapViewState view)
        {
            if (joystick == null || profile == null || view == null)
                return;

            bool down = GetRawButton(joystick, profile.ZoomOut);

            if (!view.ZoomOutButtonHeld && down)
            {
                view.ZoomOutButtonHeld = true;
                view.ZoomOutButtonHoldStart = Time.unscaledTime;
                view.ZoomOutButtonLongPressTriggered = false;
            }

            if (!view.ZoomOutButtonHeld)
                return;

            if (down)
            {
                if (!view.ZoomOutButtonLongPressTriggered &&
                    Time.unscaledTime - view.ZoomOutButtonHoldStart >= AddMarkerHoldSeconds)
                {
                    if (OpenManualMarkerMenu(playerIndex))
                    {
                        view.HomeFeedbackText = MapL("ВЫБЕРИ ОТМЕТКУ", "CHOOSE A MARKER");
                    }
                    else
                    {
                        view.HomeFeedbackText = MapL("НЕ УДАЛОСЬ ОТКРЫТЬ", "COULD NOT OPEN");
                    }

                    view.HomeFeedbackUntil = Time.unscaledTime + 1.2f;
                    view.ZoomOutButtonLongPressTriggered = true;
                }
                return;
            }

            if (!view.ZoomOutButtonLongPressTriggered)
            {
                view.Zoom = NormalizeZoom(
                    Mathf.Clamp(
                        view.Zoom / ZoomFactor,
                        MinZoom,
                        MaxZoom));

                SaveZoomForView(playerIndex);
            }

            view.ZoomOutButtonHeld = false;
            view.ZoomOutButtonLongPressTriggered = false;
        }

        private bool OpenManualMarkerMenu(int viewIndex)
        {
            if (!AreManualMarkersEnabled())
                return false;
            if (viewIndex < 0 || viewIndex >= _manualMarkerMenus.Length)
                return false;

            EnsureManualNotesLoadedForCurrentWorld();

            Vector3 worldPosition;
            if (!TryGetMapCenterWorldPosition(viewIndex, out worldPosition))
                return false;

            CloseManualMarkerContext(viewIndex);

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];
            menu.Open = true;
            menu.TargetWorldPosition = worldPosition;
            menu.Stack.Clear();
            menu.Indices.Clear();
            menu.Stack.Add(_manualRootMenu);
            menu.Indices.Add(0);
            return true;
        }

        private bool HasOpenManualMarkerMenu(int viewIndex)
        {
            return viewIndex >= 0 && viewIndex < _manualMarkerMenus.Length && _manualMarkerMenus[viewIndex] != null && _manualMarkerMenus[viewIndex].Open;
        }

        private void CloseManualMarkerMenu(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _manualMarkerMenus.Length)
                return;

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];
            menu.Open = false;
            menu.Stack.Clear();
            menu.Indices.Clear();
        }

        private bool HandleManualMarkerMenuGamepadInput(int viewIndex, Joystick joystick, RawGamepadProfile profile)
        {
            if (!HasOpenManualMarkerMenu(viewIndex) || joystick == null || profile == null)
                return false;

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];
            ManualMarkerMenuNode current = GetCurrentMenuNode(menu);
            if (current == null)
                return true;

            List<ManualMarkerMenuNode> items = current.Children;
            if (items == null || items.Count == 0)
                return true;

            if (GetRawButtonDown(joystick, profile.ToggleMap))
            {
                CloseManualMarkerMenu(viewIndex);
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadUp))
                ChangeCurrentMenuIndex(menu, -1);

            if (GetRawButtonDown(joystick, profile.DPadDown))
                ChangeCurrentMenuIndex(menu, +1);

            if (GetRawButtonDown(joystick, profile.DPadLeft))
            {
                if (menu.Stack.Count > 1)
                {
                    menu.Stack.RemoveAt(menu.Stack.Count - 1);
                    menu.Indices.RemoveAt(menu.Indices.Count - 1);
                }
                else
                {
                    CloseManualMarkerMenu(viewIndex);
                }

                return true;
            }

            if (GetRawButtonDown(joystick, profile.Center) ||
                GetRawButtonDown(joystick, profile.DPadRight))
            {
                ActivateCurrentMenuSelection(viewIndex);
                return true;
            }

            return true;
        }

        private bool HandleManualMarkerMenuKeyboardInput(int viewIndex)
        {
            if (!HasOpenManualMarkerMenu(viewIndex))
                return false;

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];

            if (Input.GetKeyDown(KeyCode.UpArrow))
                ChangeCurrentMenuIndex(menu, -1);

            if (Input.GetKeyDown(KeyCode.DownArrow))
                ChangeCurrentMenuIndex(menu, +1);

            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                if (menu.Stack.Count > 1)
                {
                    menu.Stack.RemoveAt(menu.Stack.Count - 1);
                    menu.Indices.RemoveAt(menu.Indices.Count - 1);
                }
                else
                {
                    CloseManualMarkerMenu(viewIndex);
                }

                return true;
            }

            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                ActivateCurrentMenuSelection(viewIndex);
                return true;
            }

            return true;
        }

        private bool HasOpenManualMarkerContext(int viewIndex)
        {
            return
                viewIndex >= 0 &&
                viewIndex < _manualMarkerContexts.Length &&
                _manualMarkerContexts[viewIndex] != null &&
                _manualMarkerContexts[viewIndex].Open;
        }

        private void CloseManualMarkerContext(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _manualMarkerContexts.Length)
                return;

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];
            context.Open = false;
            context.NoteId = String.Empty;
            context.SelectedIndex = 0;
        }

        private bool TryOpenManualMarkerContextAtMapCenter(int viewIndex)
        {
            if (!AreManualMarkersEnabled())
                return false;

            if (viewIndex < 0 || viewIndex >= _manualMarkerContexts.Length)
                return false;

            MapViewState view = _views[viewIndex];
            if (view == null || view.Zoom < 16.0f)
                return false;

            EnsureManualNotesLoadedForCurrentWorld();

            ManualMapNote note = FindManualMarkerAtMapCenter(viewIndex);
            if (note == null || String.IsNullOrEmpty(note.Id))
                return false;

            CloseManualMarkerMenu(viewIndex);

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];
            context.Open = true;
            context.NoteId = note.Id;
            context.SelectedIndex = 0;
            return true;
        }

        private ManualMapNote FindManualMarkerAtMapCenter(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return null;

            Zone[] zones = GetZones();
            if (zones == null || zones.Length == 0)
                return null;

            WorldBounds bounds;
            if (!TryCalculateWorldBounds(zones, out bounds))
                return null;

            Rect viewport = GetViewportRect(viewIndex);
            if (viewport.width < 100.0f || viewport.height < 100.0f)
                return null;

            Rect localViewport = new Rect(0.0f, 0.0f, viewport.width, viewport.height);
            Rect basePaperRect = GetBasePaperRect(localViewport);
            Rect baseMapRect = GetBaseMapRect(basePaperRect);

            MapViewState view = _views[viewIndex];
            Vector2 centerNormalized = new Vector2(0.5f + view.Pan.x, 0.5f + view.Pan.y);

            float selectionRadiusPx = Mathf.Max(34.0f, GetManualMarkerSize(view.Zoom) * 0.78f);
            float bestDistanceSq = selectionRadiusPx * selectionRadiusPx;
            ManualMapNote best = null;

            foreach (KeyValuePair<string, ManualMapNote> pair in _manualNotesById)
            {
                ManualMapNote note = pair.Value;
                if (note == null || ShouldHideCrossedOutMarker(note.CrossedOut))
                    continue;

                Vector2 normalized = WorldToNormalized(note.WorldPosition, bounds);
                float dx = (normalized.x - centerNormalized.x) * baseMapRect.width * view.Zoom;
                float dy = (normalized.y - centerNormalized.y) * baseMapRect.height * view.Zoom;
                float distanceSq = dx * dx + dy * dy;

                if (distanceSq <= bestDistanceSq)
                {
                    best = note;
                    bestDistanceSq = distanceSq;
                }
            }

            return best;
        }

        private bool HandleManualMarkerContextGamepadInput(int viewIndex, Joystick joystick, RawGamepadProfile profile)
        {
            if (!HasOpenManualMarkerContext(viewIndex) || joystick == null || profile == null)
                return false;

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];

            if (GetRawButtonDown(joystick, profile.ToggleMap) ||
                GetRawButtonDown(joystick, profile.DPadLeft))
            {
                CloseManualMarkerContext(viewIndex);
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadUp))
            {
                context.SelectedIndex = (context.SelectedIndex + 2) % 3;
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadDown))
            {
                context.SelectedIndex = (context.SelectedIndex + 1) % 3;
                return true;
            }

            if (GetRawButtonDown(joystick, profile.Center) ||
                GetRawButtonDown(joystick, profile.DPadRight))
            {
                ActivateManualMarkerContextSelection(viewIndex);
                return true;
            }

            return true;
        }

        private bool HandleManualMarkerContextKeyboardInput(int viewIndex)
        {
            if (!HasOpenManualMarkerContext(viewIndex))
                return false;

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];

            if (Input.GetKeyDown(KeyCode.UpArrow))
                context.SelectedIndex = (context.SelectedIndex + 2) % 3;

            if (Input.GetKeyDown(KeyCode.DownArrow))
                context.SelectedIndex = (context.SelectedIndex + 1) % 3;

            if (Input.GetKeyDown(KeyCode.Escape) ||
                Input.GetKeyDown(KeyCode.Backspace) ||
                Input.GetKeyDown(KeyCode.LeftArrow))
            {
                CloseManualMarkerContext(viewIndex);
                return true;
            }

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter) ||
                Input.GetKeyDown(KeyCode.RightArrow))
            {
                ActivateManualMarkerContextSelection(viewIndex);
                return true;
            }

            return true;
        }

        private void ActivateManualMarkerContextSelection(int viewIndex)
        {
            if (!HasOpenManualMarkerContext(viewIndex))
                return;

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];
            ManualMapNote note;
            if (String.IsNullOrEmpty(context.NoteId) ||
                !_manualNotesById.TryGetValue(context.NoteId, out note) ||
                note == null)
            {
                CloseManualMarkerContext(viewIndex);
                return;
            }

            if (context.SelectedIndex == 0)
            {
                note.CrossedOut = !note.CrossedOut;
                SaveManualNotes();

                _views[viewIndex].HomeFeedbackText =
                    note.CrossedOut ? MapL("ОТМЕТКА ЗАЧЁРКНУТА", "MARKER CROSSED OUT") : MapL("ОТМЕТКА ВОССТАНОВЛЕНА", "MARKER RESTORED");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }
            else if (context.SelectedIndex == 1)
            {
                _manualNotesById.Remove(note.Id);
                SaveManualNotes();

                _views[viewIndex].HomeFeedbackText = MapL("ОТМЕТКА УДАЛЕНА", "MARKER REMOVED");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }

            CloseManualMarkerContext(viewIndex);
        }

        private void ChangeCurrentMenuIndex(ManualMarkerMenuState menu, int delta)
        {
            if (menu == null || menu.Indices.Count == 0)
                return;

            ManualMarkerMenuNode current = GetCurrentMenuNode(menu);
            if (current == null || current.Children == null || current.Children.Count == 0)
                return;

            int index = menu.Indices[menu.Indices.Count - 1];
            index = (index + delta) % current.Children.Count;
            if (index < 0)
                index += current.Children.Count;
            menu.Indices[menu.Indices.Count - 1] = index;
        }

        private ManualMarkerMenuNode GetCurrentMenuNode(ManualMarkerMenuState menu)
        {
            if (menu == null || menu.Stack.Count == 0)
                return null;

            return menu.Stack[menu.Stack.Count - 1];
        }

        private void ActivateCurrentMenuSelection(int viewIndex)
        {
            if (!HasOpenManualMarkerMenu(viewIndex))
                return;

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];
            ManualMarkerMenuNode current = GetCurrentMenuNode(menu);
            if (current == null || current.Children == null || current.Children.Count == 0)
                return;

            int index = menu.Indices[menu.Indices.Count - 1];
            index = Mathf.Clamp(index, 0, current.Children.Count - 1);
            ManualMarkerMenuNode selected = current.Children[index];
            if (selected == null)
                return;

            if (selected.Children != null && selected.Children.Count > 0)
            {
                menu.Stack.Add(selected);
                menu.Indices.Add(0);
                return;
            }

            if (selected.Preset == null)
                return;

            AddManualMarker(viewIndex, selected.Preset, menu.TargetWorldPosition);
            CloseManualMarkerMenu(viewIndex);
        }

        private void AddManualMarker(int viewIndex, ManualMarkerPreset preset, Vector3 worldPosition)
        {
            if (preset == null)
                return;

            EnsureManualNotesLoadedForCurrentWorld();

            ManualMapNote note = new ManualMapNote();
            note.Id = "M" + _nextManualMarkerId.ToString(CultureInfo.InvariantCulture);
            _nextManualMarkerId++;
            note.IconId = preset.IconId;
            note.Label = preset.Label;
            note.Category = preset.Category;
            note.Subtype = preset.Subtype;
            note.WorldPosition = worldPosition;
            note.CrossedOut = false;

            _manualNotesById[note.Id] = note;
            SaveManualNotes();

            if (viewIndex >= 0 && viewIndex < _views.Length)
            {
                _views[viewIndex].HomeFeedbackText = MapL("ОТМЕТКА ДОБАВЛЕНА", "MARKER ADDED");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }

            Logger.LogInfo(
                "P" + (viewIndex + 1) + " added manual marker " +
                note.Id + " [" + note.IconId + "] @ " +
                note.WorldPosition.x.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                note.WorldPosition.z.ToString("0.0", CultureInfo.InvariantCulture));
        }

        private bool TryGetMapCenterWorldPosition(int viewIndex, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return false;

            Zone[] zones = GetZones();
            if (zones == null || zones.Length == 0)
                return false;

            WorldBounds bounds;
            if (!TryCalculateWorldBounds(zones, out bounds))
                return false;

            MapViewState view = _views[viewIndex];
            float u = Mathf.Clamp01(0.5f + view.Pan.x);
            float v = Mathf.Clamp01(0.5f + view.Pan.y);

            float rotatedX = Mathf.Lerp(bounds.MinX, bounds.MaxX, u);
            float rotatedZ = Mathf.Lerp(bounds.MinZ, bounds.MaxZ, v);
            worldPosition = RotateNorthUpPointToWorld(new Vector3(rotatedX, 0.0f, rotatedZ));
            return true;
        }

        private Vector3 RotateNorthUpPointToWorld(Vector3 point)
        {
            if (!_northCalibrationValid)
                return point;

            float radians = _northYawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);
            float x = c * point.x + s * point.z;
            float z = -s * point.x + c * point.z;
            return new Vector3(x, point.y, z);
        }

        private float GetManualMarkerSize(float zoom)
        {
            float localZoom = Mathf.Max(16.0f, zoom) / 16.0f;
            float zoomSize = Mathf.Clamp(
                ManualMarkerBaseSize * Mathf.Pow(localZoom, 0.43f),
                ManualMarkerBaseSize,
                68.0f);

            return zoomSize * GetMarkerScale();
        }

        private void DrawManualMarkers(Rect mapRect, WorldBounds bounds, MapViewState view)
        {
            EnsureManualNotesLoadedForCurrentWorld();
            foreach (KeyValuePair<string, ManualMapNote> pair in _manualNotesById)
            {
                ManualMapNote note = pair.Value;
                if (note == null || ShouldHideCrossedOutMarker(note.CrossedOut))
                    continue;

                Vector2 normalized = WorldToNormalized(note.WorldPosition, bounds);
                Vector2 screenNormalized = ApplyView(normalized, view);
                if (!IsInsideMap(screenNormalized))
                    continue;

                Vector2 point = NormalizedToMap(screenNormalized, mapRect);
                Texture2D texture = null;
                _manualIconTextures.TryGetValue(note.IconId ?? String.Empty, out texture);

                float size = GetManualMarkerSize(view.Zoom);
                float opacity = GetCrossedOutMarkerOpacity(note.CrossedOut);
                if (texture != null)
                {
                    float maxDim = Mathf.Max(texture.width, texture.height);
                    float width = size;
                    float height = size;
                    if (maxDim > 0.001f)
                    {
                        width = size * (texture.width / maxDim);
                        height = size * (texture.height / maxDim);
                    }

                    GUI.color = new Color(1.0f, 1.0f, 1.0f, opacity);
                    GUI.DrawTexture(new Rect(point.x - width * 0.5f, point.y - height * 0.5f, width, height), texture);
                    GUI.color = Color.white;
                }
                else
                {
                    GUI.color = new Color(0.28f, 0.14f, 0.05f, 0.92f * opacity);
                    GUI.DrawTexture(new Rect(point.x - size * 0.22f, point.y - size * 0.22f, size * 0.44f, size * 0.44f), _circleTexture);
                    GUI.color = Color.white;
                }

                if (note.CrossedOut)
                {
                    DrawManualMarkerCross(point, size * 0.9f, opacity);
                }

                string localizedLabel = MapLocManualMarkerLabel(note.Category, note.Subtype, note.Label);
                if (ShouldDrawManualMarkerLabels(view.Zoom) && !String.IsNullOrEmpty(localizedLabel))
                {
                    DrawManualMarkerLabel(
                        point,
                        size,
                        localizedLabel,
                        opacity);
                }
            }
        }

        private void DrawManualMarkerLabel(
            Vector2 point,
            float iconSize,
            string text,
            float opacity)
        {
            if (String.IsNullOrEmpty(text) || _manualLabelStyle == null)
                return;

            GUIContent content = new GUIContent(text);
            Vector2 measured = _manualLabelStyle.CalcSize(content);

            float width = Mathf.Clamp(measured.x + 18.0f, 72.0f, 170.0f);
            float height = 25.0f;
            float y = point.y + iconSize * 0.48f + 3.0f;

            Rect background = new Rect(
                point.x - width * 0.5f,
                y,
                width,
                height);

            GUI.color = new Color(0.12f, 0.06f, 0.02f, 0.92f * opacity);
            GUI.DrawTexture(background, _pixelTexture);

            GUI.color = new Color(0.94f, 0.85f, 0.68f, 0.98f * opacity);
            GUI.DrawTexture(
                new Rect(
                    background.x + 2.0f,
                    background.y + 2.0f,
                    background.width - 4.0f,
                    background.height - 4.0f),
                _pixelTexture);

            GUI.color = new Color(1.0f, 1.0f, 1.0f, opacity);
            GUI.Label(background, text, _manualLabelStyle);
            GUI.color = Color.white;
        }

        private void DrawManualMarkerCross(Vector2 point, float size, float opacityMultiplier = 1.0f)
        {
            if (_pixelTexture == null)
                return;

            Color old = GUI.color;
            GUI.color = new Color(0.35f, 0.12f, 0.06f, 0.90f * opacityMultiplier);

            float thickness = 2.0f;
            float half = size * 0.5f;
            DrawLine(point.x - half, point.y - half, point.x + half, point.y + half, thickness);
            DrawLine(point.x - half, point.y + half, point.x + half, point.y - half, thickness);
            GUI.color = old;
        }

        private void DrawLine(float x0, float y0, float x1, float y1, float thickness)
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            float angle = Mathf.Atan2(y1 - y0, x1 - x0) * Mathf.Rad2Deg;
            float length = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            GUIUtility.RotateAroundPivot(angle, new Vector2(x0, y0));
            GUI.DrawTexture(new Rect(x0, y0 - thickness * 0.5f, length, thickness), _pixelTexture);
            GUI.matrix = oldMatrix;
        }

        private void DrawManualMarkerMenu(int viewIndex, Rect localViewport)
        {
            if (!HasOpenManualMarkerMenu(viewIndex))
                return;

            EnsureManualMenuStyles();

            ManualMarkerMenuState menu = _manualMarkerMenus[viewIndex];
            ManualMarkerMenuNode current = GetCurrentMenuNode(menu);
            if (current == null || current.Children == null)
                return;

            float rowHeight = 40.0f;
            float headerHeight = 92.0f;
            float footerHeight = 52.0f;
            float desiredHeight =
                headerHeight +
                current.Children.Count * rowHeight +
                footerHeight;

            float width = Mathf.Min(470.0f, localViewport.width * 0.90f);
            float height = Mathf.Min(desiredHeight, localViewport.height * 0.88f);

            Rect panel = new Rect(
                localViewport.center.x - width * 0.5f,
                localViewport.center.y - height * 0.5f,
                width,
                height);

            // Dim the map slightly so the menu remains readable from couch distance.
            GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.30f);
            GUI.DrawTexture(localViewport, _pixelTexture);

            // Dark frame + light parchment body: deliberately high contrast.
            GUI.color = new Color(0.11f, 0.055f, 0.018f, 0.98f);
            GUI.DrawTexture(panel, _pixelTexture);

            Rect inner = new Rect(
                panel.x + 4.0f,
                panel.y + 4.0f,
                panel.width - 8.0f,
                panel.height - 8.0f);

            GUI.color = new Color(0.91f, 0.80f, 0.61f, 1.0f);
            GUI.DrawTexture(inner, _pixelTexture);
            GUI.color = Color.white;

            GUI.Label(
                new Rect(panel.x + 18.0f, panel.y + 12.0f, panel.width - 36.0f, 36.0f),
                MapLocManualMenuTitle(current.Title),
                _manualMenuTitleStyle);

            string breadcrumb = menu.Stack.Count > 1
                ? MapLocManualMenuTitle(_manualRootMenu.Title) + "  >  " + MapLocManualMenuTitle(current.Title)
                : MapL("L1 / LB hold — добавить отметку", "L1 / LB hold — add marker");

            GUI.Label(
                new Rect(panel.x + 22.0f, panel.y + 52.0f, panel.width - 44.0f, 26.0f),
                breadcrumb,
                _manualMenuHelpStyle);

            float listY = panel.y + headerHeight;
            int selectedIndex = menu.Indices.Count > 0
                ? menu.Indices[menu.Indices.Count - 1]
                : 0;

            int maxVisibleRows = Mathf.Max(1, Mathf.FloorToInt((height - headerHeight - footerHeight) / rowHeight));
            int firstVisible = 0;
            if (current.Children.Count > maxVisibleRows)
            {
                firstVisible = Mathf.Clamp(
                    selectedIndex - maxVisibleRows / 2,
                    0,
                    current.Children.Count - maxVisibleRows);
            }

            int lastVisible = Mathf.Min(current.Children.Count, firstVisible + maxVisibleRows);
            int visualRow = 0;
            int i;
            for (i = firstVisible; i < lastVisible; i++)
            {
                ManualMarkerMenuNode item = current.Children[i];
                Rect row = new Rect(
                    panel.x + 18.0f,
                    listY + visualRow * rowHeight,
                    panel.width - 36.0f,
                    rowHeight - 4.0f);

                bool selected = i == selectedIndex;

                if (selected)
                {
                    GUI.color = new Color(0.24f, 0.105f, 0.025f, 1.0f);
                    GUI.DrawTexture(row, _pixelTexture);
                    GUI.color = Color.white;
                }

                string prefix =
                    item.Children != null && item.Children.Count > 0
                    ? "›  "
                    : "   ";

                GUI.Label(
                    row,
                    prefix + MapLocManualMenuTitle(item.Title),
                    selected
                        ? _manualMenuSelectedStyle
                        : _manualMenuItemStyle);

                visualRow++;
            }

            GUI.Label(
                new Rect(
                    panel.x + 18.0f,
                    panel.yMax - footerHeight + 10.0f,
                    panel.width - 36.0f,
                    34.0f),
                MapL("D-pad ↑/↓ — выбор     A / → — выбрать     View / ← — назад", "D-pad ↑/↓ — move     A / → — select     View / ← — back"),
                _manualMenuHelpStyle);

            GUI.color = Color.white;
        }

        private void DrawManualMarkerContextMenu(int viewIndex, Rect localViewport)
        {
            if (!HasOpenManualMarkerContext(viewIndex))
                return;

            EnsureManualMenuStyles();

            ManualMarkerContextState context = _manualMarkerContexts[viewIndex];
            ManualMapNote note;
            if (String.IsNullOrEmpty(context.NoteId) ||
                !_manualNotesById.TryGetValue(context.NoteId, out note) ||
                note == null)
            {
                CloseManualMarkerContext(viewIndex);
                return;
            }

            GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.28f);
            GUI.DrawTexture(localViewport, _pixelTexture);
            GUI.color = Color.white;

            float width = 390.0f;
            float height = 235.0f;
            Rect panel = new Rect(
                localViewport.center.x - width * 0.5f,
                localViewport.center.y - height * 0.5f,
                width,
                height);

            GUI.color = new Color(0.10f, 0.045f, 0.015f, 0.98f);
            GUI.DrawTexture(panel, _pixelTexture);
            GUI.color = new Color(0.91f, 0.80f, 0.61f, 0.99f);
            GUI.DrawTexture(new Rect(panel.x + 3.0f, panel.y + 3.0f, panel.width - 6.0f, panel.height - 6.0f), _pixelTexture);
            GUI.color = Color.white;

            string title = MapLocManualMarkerLabel(note.Category, note.Subtype, note.Label);
            if (String.IsNullOrEmpty(title))
                title = MapL("Отметка", "Marker");
            GUI.Label(new Rect(panel.x + 18.0f, panel.y + 12.0f, panel.width - 36.0f, 34.0f), title, _manualMenuTitleStyle);

            string[] options = new string[]
            {
                note.CrossedOut ? MapL("Восстановить", "Restore") : MapL("Зачеркнуть", "Cross Out"),
                MapL("Удалить", "Delete"),
                MapL("Отмена", "Cancel")
            };

            float rowY = panel.y + 62.0f;
            float rowHeight = 42.0f;
            int i;
            for (i = 0; i < options.Length; i++)
            {
                Rect row = new Rect(panel.x + 22.0f, rowY + i * rowHeight, panel.width - 44.0f, 36.0f);
                bool selected = i == context.SelectedIndex;

                if (selected)
                {
                    GUI.color = new Color(0.28f, 0.13f, 0.035f, 0.98f);
                    GUI.DrawTexture(row, _pixelTexture);
                    GUI.color = Color.white;
                }

                GUI.Label(row, options[i], selected ? _manualMenuSelectedStyle : _manualMenuItemStyle);
            }
        }

        private void EnsureManualMenuStyles()
        {
            if (_manualMenuTitleStyle == null)
            {
                _manualMenuTitleStyle = new GUIStyle(GUI.skin.label);
                _manualMenuTitleStyle.alignment = TextAnchor.MiddleCenter;
                _manualMenuTitleStyle.fontSize = 27;
                _manualMenuTitleStyle.fontStyle = FontStyle.Bold;
                _manualMenuTitleStyle.normal.textColor =
                    new Color(0.16f, 0.065f, 0.018f, 1.0f);
            }

            if (_manualMenuItemStyle == null)
            {
                _manualMenuItemStyle = new GUIStyle(GUI.skin.label);
                _manualMenuItemStyle.alignment = TextAnchor.MiddleLeft;
                _manualMenuItemStyle.padding = new RectOffset(14, 8, 2, 2);
                _manualMenuItemStyle.fontSize = 21;
                _manualMenuItemStyle.fontStyle = FontStyle.Normal;
                _manualMenuItemStyle.normal.textColor =
                    new Color(0.14f, 0.055f, 0.015f, 1.0f);
            }

            if (_manualMenuSelectedStyle == null)
            {
                _manualMenuSelectedStyle = new GUIStyle(_manualMenuItemStyle);
                _manualMenuSelectedStyle.normal.textColor =
                    new Color(1.0f, 0.94f, 0.80f, 1.0f);
                _manualMenuSelectedStyle.fontStyle = FontStyle.Bold;
            }

            if (_manualMenuHelpStyle == null)
            {
                _manualMenuHelpStyle = new GUIStyle(GUI.skin.label);
                _manualMenuHelpStyle.alignment = TextAnchor.MiddleCenter;
                _manualMenuHelpStyle.fontSize = 15;
                _manualMenuHelpStyle.fontStyle = FontStyle.Bold;
                _manualMenuHelpStyle.normal.textColor =
                    new Color(0.20f, 0.09f, 0.025f, 1.0f);
                _manualMenuHelpStyle.wordWrap = true;
            }

            if (_manualLabelStyle == null)
            {
                _manualLabelStyle = new GUIStyle(GUI.skin.label);
                _manualLabelStyle.alignment = TextAnchor.MiddleCenter;
                _manualLabelStyle.fontSize = 16;
                _manualLabelStyle.fontStyle = FontStyle.Bold;
                _manualLabelStyle.normal.textColor =
                    new Color(0.14f, 0.055f, 0.015f, 1.0f);
                _manualLabelStyle.clipping = TextClipping.Clip;
            }
        }

        private sealed class ManualMapNote
        {
            public string Id;
            public string IconId;
            public string Label;
            public string Category;
            public string Subtype;
            public Vector3 WorldPosition;
            public bool CrossedOut;
        }

        private sealed class ManualMarkerPreset
        {
            public string Category;
            public string Subtype;
            public string IconId;
            public string Label;
        }

        private sealed class ManualMarkerMenuNode
        {
            public string Title;
            public ManualMarkerPreset Preset;
            public List<ManualMarkerMenuNode> Children;
        }

        private sealed class ManualMarkerMenuState
        {
            public bool Open;
            public Vector3 TargetWorldPosition;
            public readonly List<ManualMarkerMenuNode> Stack = new List<ManualMarkerMenuNode>();
            public readonly List<int> Indices = new List<int>();
        }

        private sealed class ManualMarkerContextState
        {
            public bool Open;
            public string NoteId = String.Empty;
            public int SelectedIndex;
        }
    }
}

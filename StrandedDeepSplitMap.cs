using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using Rewired;

namespace StrandedDeepSplitMap
{
    [BepInPlugin("local.strandeddeep.splitmap", "Stranded Deep Split Map", "0.13.2")]
    [BepInDependency("com.bamex.strandeddeep.modsettings", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class SplitMapPlugin : BaseUnityPlugin
    {
        private const float MinZoom = 1.0f;
        private const float MaxZoom = 128.0f;
        private const float ZoomFactor = 2.0f;
        private const float PanStep = 0.04f;

        private const float GamepadPanSpeed = 0.48f;
        private const float MarkerContextHoldSeconds = 1.20f;
        private const float HomeIslandTripleClickGapSeconds = 0.34f;
        private const float NavigationHoldSeconds = 1.20f;
        private const float NavigationArrivalRadius = 120.0f;

        private const float ExplorationRadiusWorld = 22.0f;
        private const float ExplorationSampleInterval = 0.40f;
        private const float ExplorationSaveDelay = 2.0f;

        // Distance is measured to the Zone center. A typical island terrain is about
        // 250 m wide, so 450 m means the island can be discovered roughly a few
        // hundred meters before reaching its shore.
        private const float DiscoverySampleInterval = 0.25f;

        private readonly MapViewState[] _views = new MapViewState[]
        {
            new MapViewState(),
            new MapViewState()
        };

        private readonly Dictionary<string, ZoneContourData> _zoneContourCache =
            new Dictionary<string, ZoneContourData>();

        private readonly HashSet<string> _discoveredZones =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, Texture2D> _discoveryBlobCache =
            new Dictionary<string, Texture2D>();

        private Beam.Player[] _players = new Beam.Player[0];
        private StrandedWorld _world;
        private float _nextRefreshTime;
        private int _focusedView = 0;

        private Texture2D _pixelTexture;
        private Texture2D _circleTexture;
        private Texture2D _arrowTexture;
        private Texture2D _navigationArrowTexture;

        private Texture2D _parchmentTexture;
        private Texture2D _compassRoseTexture;

        private ConfigEntry<float> _player1LastZoomConfig;
        private ConfigEntry<float> _player2LastZoomConfig;


        private GUIStyle _titleStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _compassStyle;

        private MethodInfo _createShoreMaskMethod;

        private bool _northCalibrationValid;
        private float _northYawDegrees;
        private string _northCalibrationSeed;

        private float _nextExplorationSampleTime;
        private float _nextExplorationSaveTime;
        private float _nextDiscoverySampleTime;
        private bool _discoveryLoaded;
        private string _discoverySeed;
        private bool _discoveryDirty;

        private string _homeIslandZoneKey = String.Empty;
        private string _homeIslandSeed = String.Empty;
        private bool _homeIslandLoaded;

        private readonly string[] _navigationTargetZoneKeys =
        {
            String.Empty,
            String.Empty
        };

        private string _navigationSeed = String.Empty;
        private bool _navigationLoaded;

        private void Awake()
        {
            CreateTextures();
            LoadVisualAssets();
            InitializeWreckNotes();
            InitializeManualNotes();
            InitializePersistentViewSettings();
            InitializeMapUserSettings();
            RegisterMapModSettings();
            InitializeWorldToolsReflection();

            Logger.LogInfo("Stranded Deep Split Map v0.13b loaded.");
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextRefreshTime)
            {
                RefreshRuntimeReferences();
                _nextRefreshTime = Time.unscaledTime + 1.0f;
            }

            if (Time.unscaledTime >= _nextDiscoverySampleTime)
            {
                UpdateDiscoveryFromPlayers();
                _nextDiscoverySampleTime = Time.unscaledTime + DiscoverySampleInterval;
            }

            if (Time.unscaledTime >= _nextExplorationSampleTime)
            {
                UpdateExplorationFromPlayers();
                _nextExplorationSampleTime = Time.unscaledTime + ExplorationSampleInterval;
            }

            if (Time.unscaledTime >= _nextExplorationSaveTime)
            {
                SaveDirtyExplorationMasks();
                SaveDiscoveredZonesIfDirty();
                _nextExplorationSaveTime = Time.unscaledTime + ExplorationSaveDelay;
            }

            HandleGamepadMapControls();

            if (Input.GetKeyDown(KeyCode.K))
                CalibrateNorthFromLeftPlayer();

            if (Input.GetKeyDown(KeyCode.M))
                ToggleView(0);

            if (Input.GetKeyDown(KeyCode.N))
                ToggleView(1);

            HandleFocusedMapControls();
        }

        private void OnGUI()
        {
            EnsureStyles();

            bool anyMapOpen =
                _views[0].Open ||
                _views[1].Open;

            if (_world == null ||
                _players == null ||
                _players.Length == 0)
            {
                if (anyMapOpen)
                    DrawUnavailableOverlay();

                return;
            }

            if (_views[0].Open)
                DrawPlayerMap(0);

            if (_views[1].Open)
                DrawPlayerMap(1);

            DrawNavigationIndicator(0);
            DrawNavigationIndicator(1);
        }

        private void ToggleView(int index)
        {
            if (index < 0 || index >= _views.Length)
                return;

            MapViewState view = _views[index];
            bool opening = !view.Open;

            view.Open = !view.Open;

            if (view.Open)
            {
                _focusedView = index;
                view.CenterOnOpen = ShouldCenterOnOpen();

                RefreshRuntimeReferences();

                if (opening)
                    PrepareWreckPromptBeforeOpeningMap(index);

                SuppressReservedMapButtons(index);

                if (view.CenterOnOpen && CenterViewOnOwningPlayer(index))
                    view.CenterOnOpen = false;
            }
            else
            {
                ClearPendingWreck(index);
                RestoreReservedMapButtons(index);

                view.CenterButtonHeld = false;
                view.CenterButtonLongPressTriggered = false;
                view.CenterButtonClickCount = 0;
                view.CenterButtonFirstClickAt = 0.0f;
                view.CenterButtonLastClickAt = 0.0f;

                view.ZoomInButtonHeld = false;
                view.ZoomInButtonLongPressTriggered = false;

                view.ZoomOutButtonHeld = false;
                view.ZoomOutButtonLongPressTriggered = false;

                CloseManualMarkerMenu(index);
                CloseManualMarkerContext(index);

                SaveZoomForView(index);

                int other = index == 0 ? 1 : 0;
                if (_views[other].Open)
                    _focusedView = other;
            }
        }

        private void SuppressReservedMapButtons(int viewIndex)
        {
            if (viewIndex < 0 ||
                viewIndex >= _views.Length ||
                _players == null ||
                viewIndex >= _players.Length ||
                _players[viewIndex] == null ||
                _players[viewIndex].Input == null ||
                _players[viewIndex].Input.controllers == null)
            {
                return;
            }

            MapViewState view = _views[viewIndex];

            if (view.ReservedButtonsSuppressed)
                return;

            IList<Joystick> joysticks =
                _players[viewIndex].Input.controllers.Joysticks;

            Joystick joystick =
                GetPrimaryAssignedJoystick(joysticks);

            if (joystick == null)
                return;

            try
            {
                view.SuppressedReservedButtonMaps.Clear();

                IEnumerable<ControllerMap> maps =
                    _players[viewIndex].Input.controllers.maps.GetAllMaps<ControllerMap>();

                foreach (ControllerMap map in maps)
                {
                    if (map == null ||
                        map.controllerType != ControllerType.Joystick ||
                        map.controllerId != joystick.id)
                    {
                        continue;
                    }

                    IList<ActionElementMap> buttonMaps =
                        map.ButtonMaps;

                    if (buttonMaps == null)
                        continue;

                    int i;
                    for (i = 0; i < buttonMaps.Count; i++)
                    {
                        ActionElementMap actionMap = buttonMaps[i];

                        if (actionMap == null ||
                            (actionMap.elementIndex != 5 &&
                             actionMap.elementIndex != 4 &&
                             actionMap.elementIndex != 0))
                        {
                            continue;
                        }

                        ActionElementMapState state =
                            new ActionElementMapState();

                        state.Map = actionMap;
                        state.WasEnabled = actionMap.enabled;

                        view.SuppressedReservedButtonMaps.Add(state);

                        if (actionMap.enabled)
                            actionMap.enabled = false;

                    }
                }

                view.ReservedButtonsSuppressed = true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to suppress reserved map bindings for P" +
                    (viewIndex + 1) +
                    ": " +
                    ex.Message);

                view.SuppressedReservedButtonMaps.Clear();
                view.ReservedButtonsSuppressed = false;
            }
        }

        private void RestoreReservedMapButtons(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return;

            MapViewState view = _views[viewIndex];

            if (!view.ReservedButtonsSuppressed)
                return;

            try
            {
                int i;
                for (i = 0; i < view.SuppressedReservedButtonMaps.Count; i++)
                {
                    ActionElementMapState state =
                        view.SuppressedReservedButtonMaps[i];

                    if (state == null || state.Map == null)
                        continue;

                    state.Map.enabled = state.WasEnabled;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to restore reserved map bindings for P" +
                    (viewIndex + 1) +
                    ": " +
                    ex.Message);
            }
            finally
            {
                view.SuppressedReservedButtonMaps.Clear();
                view.ReservedButtonsSuppressed = false;
            }
        }

        private void HandleGamepadMapControls()
        {
            if (_players == null || _players.Length == 0)
                return;

            int count = Math.Min(_players.Length, 2);
            int playerIndex;

            for (playerIndex = 0; playerIndex < count; playerIndex++)
            {
                Beam.Player beamPlayer = _players[playerIndex];

                if (beamPlayer == null ||
                    beamPlayer.Input == null ||
                    beamPlayer.Input.controllers == null)
                {
                    continue;
                }

                IList<Joystick> joysticks =
                    beamPlayer.Input.controllers.Joysticks;

                if (joysticks == null || joysticks.Count == 0)
                    continue;

                Joystick joystick = GetPrimaryAssignedJoystick(joysticks);

                if (joystick == null)
                    continue;

                RawGamepadProfile profile =
                    GetRawGamepadProfile(joystick);

                if (profile == null)
                    continue;

                MapViewState view = _views[playerIndex];

                if (view.Open &&
                    HasPendingWreckPrompt(playerIndex) &&
                    HandlePendingWreckGamepadInput(
                        playerIndex,
                        joystick,
                        profile))
                {
                    continue;
                }

                if (view.Open &&
                    HasOpenManualMarkerMenu(playerIndex) &&
                    HandleManualMarkerMenuGamepadInput(
                        playerIndex,
                        joystick,
                        profile))
                {
                    continue;
                }

                if (view.Open &&
                    HasOpenManualMarkerContext(playerIndex) &&
                    HandleManualMarkerContextGamepadInput(
                        playerIndex,
                        joystick,
                        profile))
                {
                    continue;
                }

                if (view.Open &&
                    HasOpenWreckMarkerContext(playerIndex) &&
                    HandleWreckMarkerContextGamepadInput(
                        playerIndex,
                        joystick,
                        profile))
                {
                    continue;
                }

                if (GetRawButtonDown(joystick, profile.ToggleMap))
                {
                    ToggleView(playerIndex);
                    continue;
                }

                if (!view.Open)
                    continue;

                float panX = 0.0f;
                float panY = 0.0f;

                if (GetRawButton(joystick, profile.DPadLeft))
                    panX -= 1.0f;

                if (GetRawButton(joystick, profile.DPadRight))
                    panX += 1.0f;

                if (GetRawButton(joystick, profile.DPadDown))
                    panY -= 1.0f;

                if (GetRawButton(joystick, profile.DPadUp))
                    panY += 1.0f;

                if (Mathf.Abs(panX) > 0.01f ||
                    Mathf.Abs(panY) > 0.01f)
                {
                    float speed =
                        GamepadPanSpeed *
                        Time.unscaledDeltaTime /
                        Mathf.Max(1.0f, view.Zoom);

                    view.Pan.x += panX * speed;
                    view.Pan.y += panY * speed;

                    ClampPan(view);
                }

                HandleZoomInAndNavigationButton(
                    playerIndex,
                    joystick,
                    profile,
                    view);

                HandleZoomOutAndAddMarkerButton(
                    playerIndex,
                    joystick,
                    profile,
                    view);

                HandleCenterAndHomeButton(
                    playerIndex,
                    joystick,
                    profile,
                    view);
            }
        }

        private void HandleZoomInAndNavigationButton(
            int playerIndex,
            Joystick joystick,
            RawGamepadProfile profile,
            MapViewState view)
        {
            if (joystick == null || profile == null || view == null)
                return;

            bool down =
                GetRawButton(
                    joystick,
                    profile.ZoomIn);

            // Start from the current raw physical state rather than relying only
            // on the one-frame button-down edge. This makes long press robust
            // even if Rewired/game UI consumes or misses that edge.
            if (!view.ZoomInButtonHeld && down)
            {
                view.ZoomInButtonHeld = true;
                view.ZoomInButtonHoldStart = Time.unscaledTime;
                view.ZoomInButtonLongPressTriggered = false;
            }

            if (!view.ZoomInButtonHeld)
                return;

            if (down)
            {
                if (!view.ZoomInButtonLongPressTriggered &&
                    Time.unscaledTime - view.ZoomInButtonHoldStart >=
                    NavigationHoldSeconds)
                {
                    NavigationSelectionResult result =
                        ToggleNavigationTargetAtMapCenter(playerIndex);

                    view.ZoomInButtonLongPressTriggered = true;

                    if (result == NavigationSelectionResult.Set)
                        view.HomeFeedbackText = "NAVIGATION SET";
                    else if (result == NavigationSelectionResult.Cleared)
                        view.HomeFeedbackText = "NAVIGATION OFF";
                    else
                        view.HomeFeedbackText = "AIM CROSSHAIR AT ISLAND";

                    view.HomeFeedbackUntil =
                        Time.unscaledTime + 1.6f;
                }

                return;
            }

            // Short press keeps the existing RB/R1 zoom-in behavior.
            if (!view.ZoomInButtonLongPressTriggered)
            {
                view.Zoom = NormalizeZoom(
                    Mathf.Clamp(
                        view.Zoom * ZoomFactor,
                        MinZoom,
                        MaxZoom));

                SaveZoomForView(playerIndex);
            }

            view.ZoomInButtonHeld = false;
            view.ZoomInButtonLongPressTriggered = false;
        }

        private NavigationSelectionResult ToggleNavigationTargetAtMapCenter(
            int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return NavigationSelectionResult.NoIsland;

            EnsureNavigationLoadedForCurrentWorld();

            Zone target =
                FindDiscoveredZoneAtMapCenter(viewIndex);

            if (target == null)
                return NavigationSelectionResult.NoIsland;

            string key = GetZoneKey(target);

            if (String.Equals(
                    _navigationTargetZoneKeys[viewIndex],
                    key,
                    StringComparison.Ordinal))
            {
                _navigationTargetZoneKeys[viewIndex] =
                    String.Empty;

                SaveNavigationTargets();

                Logger.LogInfo(
                    "P" +
                    (viewIndex + 1) +
                    " navigation target cleared.");

                return NavigationSelectionResult.Cleared;
            }

            _navigationTargetZoneKeys[viewIndex] = key;
            SaveNavigationTargets();

            Logger.LogInfo(
                "P" +
                (viewIndex + 1) +
                " navigation target = " +
                key +
                " (" +
                target.ZoneName +
                ")");

            return NavigationSelectionResult.Set;
        }

        private Zone FindDiscoveredZoneAtMapCenter(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return null;

            Zone[] zones = GetZones();

            if (zones == null || zones.Length == 0)
                return null;

            WorldBounds bounds;
            if (!TryCalculateWorldBounds(zones, out bounds))
                return null;

            MapViewState view = _views[viewIndex];

            Vector2 mapCenterNormalized =
                new Vector2(
                    0.5f + view.Pan.x,
                    0.5f + view.Pan.y);

            float boundsWidth =
                Mathf.Max(
                    0.001f,
                    bounds.MaxX - bounds.MinX);

            float boundsHeight =
                Mathf.Max(
                    0.001f,
                    bounds.MaxZ - bounds.MinZ);

            Zone best = null;
            float bestDistanceSq = Single.MaxValue;

            int i;
            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];

                if (zone == null ||
                    IsHiddenBiome(zone) ||
                    !IsZoneDiscovered(zone))
                {
                    continue;
                }

                Vector2 normalized =
                    WorldToNormalized(
                        zone.transform.position,
                        bounds);

                float dx =
                    (normalized.x - mapCenterNormalized.x) *
                    boundsWidth;

                float dz =
                    (normalized.y - mapCenterNormalized.y) *
                    boundsHeight;

                float distanceSq =
                    dx * dx +
                    dz * dz;

                float selectionRadius =
                    GetDiscoveryBlobWorldSize(zone) *
                    1.05f;

                if (distanceSq >
                    selectionRadius * selectionRadius)
                {
                    continue;
                }

                if (distanceSq < bestDistanceSq)
                {
                    best = zone;
                    bestDistanceSq = distanceSq;
                }
            }

            return best;
        }

        private bool IsNavigationTarget(
            Zone zone,
            int viewIndex)
        {
            if (zone == null ||
                viewIndex < 0 ||
                viewIndex >= _navigationTargetZoneKeys.Length)
            {
                return false;
            }

            string key =
                _navigationTargetZoneKeys[viewIndex];

            if (String.IsNullOrEmpty(key))
                return false;

            return String.Equals(
                GetZoneKey(zone),
                key,
                StringComparison.Ordinal);
        }

        private Zone FindZoneByKey(string key)
        {
            if (String.IsNullOrEmpty(key))
                return null;

            Zone[] zones = GetZones();
            if (zones == null)
                return null;

            int i;
            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];

                if (zone == null)
                    continue;

                if (String.Equals(
                        GetZoneKey(zone),
                        key,
                        StringComparison.Ordinal))
                {
                    return zone;
                }
            }

            return null;
        }

        private void EnsureNavigationLoadedForCurrentWorld()
        {
            if (_world == null)
                return;

            string seed = ReadWorldSeed();

            if (_navigationLoaded &&
                String.Equals(
                    seed,
                    _navigationSeed,
                    StringComparison.Ordinal))
            {
                return;
            }

            _navigationLoaded = true;
            _navigationSeed = seed;

            _navigationTargetZoneKeys[0] = String.Empty;
            _navigationTargetZoneKeys[1] = String.Empty;

            try
            {
                string path =
                    GetNavigationPath(seed);

                if (!File.Exists(path))
                    return;

                string[] lines =
                    File.ReadAllLines(path);

                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    if (String.IsNullOrEmpty(line))
                        continue;

                    if (line.StartsWith(
                            "P1=",
                            StringComparison.Ordinal))
                    {
                        _navigationTargetZoneKeys[0] =
                            line.Substring(3).Trim();
                    }
                    else if (line.StartsWith(
                            "P2=",
                            StringComparison.Ordinal))
                    {
                        _navigationTargetZoneKeys[1] =
                            line.Substring(3).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to load navigation targets: " +
                    ex.Message);
            }
        }

        private void SaveNavigationTargets()
        {
            try
            {
                string seed = ReadWorldSeed();
                string path =
                    GetNavigationPath(seed);

                string directory =
                    Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string contents =
                    "P1=" +
                    (_navigationTargetZoneKeys[0] ?? String.Empty) +
                    Environment.NewLine +
                    "P2=" +
                    (_navigationTargetZoneKeys[1] ?? String.Empty) +
                    Environment.NewLine;

                File.WriteAllText(
                    path,
                    contents,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to save navigation targets: " +
                    ex.Message);
            }
        }

        private string GetNavigationPath(string seed)
        {
            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "Navigation",
                SafeFileName(seed) + ".txt");
        }

        private void HandleCenterAndHomeButton(
            int playerIndex,
            Joystick joystick,
            RawGamepadProfile profile,
            MapViewState view)
        {
            if (joystick == null || profile == null || view == null)
                return;

            float now = Time.unscaledTime;
            bool down =
                GetRawButton(
                    joystick,
                    profile.Center);

            // Resolve a pending one/double-tap only after the triple-tap window has
            // expired. This prevents the first tap from recentering the map before
            // the player can complete a Home Island triple-tap.
            if (!down &&
                !view.CenterButtonHeld &&
                view.CenterButtonClickCount > 0 &&
                now - view.CenterButtonLastClickAt > HomeIslandTripleClickGapSeconds)
            {
                CenterViewOnOwningPlayer(playerIndex);
                view.CenterButtonClickCount = 0;
                view.CenterButtonFirstClickAt = 0.0f;
                view.CenterButtonLastClickAt = 0.0f;
            }

            if (!view.CenterButtonHeld && down)
            {
                view.CenterButtonHeld = true;
                view.CenterButtonHoldStart = now;
                view.CenterButtonLongPressTriggered = false;
            }

            if (!view.CenterButtonHeld)
                return;

            if (down)
            {
                if (!view.CenterButtonLongPressTriggered &&
                    now - view.CenterButtonHoldStart >=
                    MarkerContextHoldSeconds)
                {
                    // Holding A/Cross is now marker-only. Home Island is deliberately
                    // NOT touched here, so an island hit-area can never steal the action.
                    bool opened =
                        TryOpenManualMarkerContextAtMapCenter(playerIndex);

                    if (!opened)
                        opened = TryOpenWreckMarkerContextAtMapCenter(playerIndex);

                    view.CenterButtonLongPressTriggered = true;

                    // A hold cancels any partial tap sequence.
                    view.CenterButtonClickCount = 0;
                    view.CenterButtonFirstClickAt = 0.0f;
                    view.CenterButtonLastClickAt = 0.0f;

                    if (!opened)
                    {
                        view.HomeFeedbackText = "CENTER A MARKER OR WRECK";
                        view.HomeFeedbackUntil = now + 1.4f;
                    }

                    return;
                }

                return;
            }

            if (!view.CenterButtonLongPressTriggered)
            {
                // Register a tap. Three quick taps set Home Island. One or two taps
                // fall back to the normal center-on-player action when the short
                // multi-click window expires.
                if (view.CenterButtonClickCount <= 0 ||
                    now - view.CenterButtonLastClickAt > HomeIslandTripleClickGapSeconds)
                {
                    view.CenterButtonClickCount = 1;
                    view.CenterButtonFirstClickAt = now;
                }
                else
                {
                    view.CenterButtonClickCount++;
                }

                view.CenterButtonLastClickAt = now;

                if (view.CenterButtonClickCount >= 3)
                {
                    bool success =
                        TrySetHomeIslandAtMapCenter(playerIndex);

                    view.HomeFeedbackText =
                        success
                        ? "HOME ISLAND SET"
                        : "CENTER AN ISLAND";

                    view.HomeFeedbackUntil = now + 1.6f;

                    view.CenterButtonClickCount = 0;
                    view.CenterButtonFirstClickAt = 0.0f;
                    view.CenterButtonLastClickAt = 0.0f;
                }
            }

            view.CenterButtonHeld = false;
            view.CenterButtonLongPressTriggered = false;
        }

        private bool TrySetHomeIslandAtMapCenter(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return false;

            EnsureHomeIslandLoadedForCurrentWorld();

            Zone best =
                FindDiscoveredZoneAtMapCenter(viewIndex);

            if (best == null)
                return false;

            _homeIslandZoneKey = GetZoneKey(best);
            _homeIslandSeed = ReadWorldSeed();
            _homeIslandLoaded = true;

            SaveHomeIsland();

            Logger.LogInfo(
                "Home Island set to " +
                _homeIslandZoneKey +
                " (" +
                best.ZoneName +
                ")");

            return true;
        }

        private bool IsHomeIsland(Zone zone)
        {
            if (zone == null ||
                String.IsNullOrEmpty(_homeIslandZoneKey))
            {
                return false;
            }

            return String.Equals(
                GetZoneKey(zone),
                _homeIslandZoneKey,
                StringComparison.Ordinal);
        }

        private void EnsureHomeIslandLoadedForCurrentWorld()
        {
            if (_world == null)
                return;

            string seed = ReadWorldSeed();

            if (_homeIslandLoaded &&
                String.Equals(
                    seed,
                    _homeIslandSeed,
                    StringComparison.Ordinal))
            {
                return;
            }

            _homeIslandLoaded = true;
            _homeIslandSeed = seed;
            _homeIslandZoneKey = String.Empty;

            try
            {
                string path =
                    GetHomeIslandPath(seed);

                if (!File.Exists(path))
                    return;

                _homeIslandZoneKey =
                    File.ReadAllText(path).Trim();

                if (!String.IsNullOrEmpty(_homeIslandZoneKey))
                {
                    Logger.LogInfo(
                        "Loaded Home Island: " +
                        _homeIslandZoneKey +
                        " for seed " +
                        seed);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to load Home Island: " +
                    ex.Message);
            }
        }

        private void SaveHomeIsland()
        {
            if (String.IsNullOrEmpty(_homeIslandZoneKey))
                return;

            try
            {
                string seed = ReadWorldSeed();
                string path =
                    GetHomeIslandPath(seed);

                string directory =
                    Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(
                    path,
                    _homeIslandZoneKey,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to save Home Island: " +
                    ex.Message);
            }
        }

        private string GetHomeIslandPath(string seed)
        {
            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "HomeIsland",
                SafeFileName(seed) + ".txt");
        }

        private Joystick GetPrimaryAssignedJoystick(
            IList<Joystick> joysticks)
        {
            if (joysticks == null)
                return null;

            int i;

            for (i = 0; i < joysticks.Count; i++)
            {
                Joystick joystick = joysticks[i];

                if (joystick != null && joystick.isConnected)
                    return joystick;
            }

            return null;
        }

        private RawGamepadProfile GetRawGamepadProfile(
            Joystick joystick)
        {
            if (joystick == null)
                return null;

            string name =
                joystick.name != null
                ? joystick.name
                : String.Empty;

            // Observed directly on the user's local split-screen setup:
            //
            // Sony DualShock 4:
            // View/Select 6, D-pad U/R/D/L 12/13/14/15,
            // L1 4, R1 5, Cross 0.
            if (name.IndexOf(
                    "DualShock 4",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                (joystick.buttonCount == 16 &&
                 name.IndexOf(
                    "Sony",
                    StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return RawGamepadProfile.DualShock4;
            }

            // Observed directly on the user's XInput controller:
            // View/Select 6, D-pad U/R/D/L 11/12/13/14,
            // LB 4, RB 5, A 0.
            if (name.IndexOf(
                    "XInput",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                joystick.buttonCount == 15)
            {
                return RawGamepadProfile.XInput;
            }

            return null;
        }

        private static bool GetRawButton(
            Joystick joystick,
            int buttonIndex)
        {
            if (joystick == null ||
                buttonIndex < 0 ||
                buttonIndex >= joystick.buttonCount)
            {
                return false;
            }

            try
            {
                return joystick.GetButton(buttonIndex);
            }
            catch
            {
                return false;
            }
        }

        private static bool GetRawButtonDown(
            Joystick joystick,
            int buttonIndex)
        {
            if (joystick == null ||
                buttonIndex < 0 ||
                buttonIndex >= joystick.buttonCount)
            {
                return false;
            }

            try
            {
                return joystick.GetButtonDown(buttonIndex);
            }
            catch
            {
                return false;
            }
        }

        private void HandleFocusedMapControls()
        {
            if (_focusedView < 0 || _focusedView >= _views.Length)
                return;

            MapViewState view = _views[_focusedView];
            if (!view.Open)
                return;

            if (HandlePendingWreckKeyboardInput(_focusedView))
                return;

            if (HandleManualMarkerMenuKeyboardInput(_focusedView))
                return;

            if (HandleManualMarkerContextKeyboardInput(_focusedView))
                return;

            if (HandleWreckMarkerContextKeyboardInput(_focusedView))
                return;

            if (Input.GetKeyDown(KeyCode.L))
            {
                OpenManualMarkerMenu(_focusedView);
                return;
            }

            bool changed = false;

            if (Input.GetKeyDown(KeyCode.Equals) ||
                Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                view.Zoom = NormalizeZoom(
                    Mathf.Clamp(view.Zoom * ZoomFactor, MinZoom, MaxZoom));

                SaveZoomForView(_focusedView);
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.Minus) ||
                Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                view.Zoom = NormalizeZoom(
                    Mathf.Clamp(view.Zoom / ZoomFactor, MinZoom, MaxZoom));

                SaveZoomForView(_focusedView);
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                view.Pan.x -= PanStep / view.Zoom;
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                view.Pan.x += PanStep / view.Zoom;
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                view.Pan.y += PanStep / view.Zoom;
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                view.Pan.y -= PanStep / view.Zoom;
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.C))
            {
                CenterViewOnOwningPlayer(_focusedView);
                changed = true;
            }

            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                view.Zoom = 1.0f;
                SaveZoomForView(_focusedView);
                CenterViewOnOwningPlayer(_focusedView);
                changed = true;
            }

            if (changed)
                ClampPan(view);
        }

        private void InitializePersistentViewSettings()
        {
            _player1LastZoomConfig = Config.Bind<float>(
                "MapView",
                "Player1LastZoom",
                1.0f,
                "Last zoom level used by the left split-screen player.");

            _player2LastZoomConfig = Config.Bind<float>(
                "MapView",
                "Player2LastZoom",
                1.0f,
                "Last zoom level used by the right split-screen player.");

            _views[0].Zoom = NormalizeZoom(_player1LastZoomConfig.Value);
            _views[1].Zoom = NormalizeZoom(_player2LastZoomConfig.Value);
        }

        private float NormalizeZoom(float value)
        {
            if (value < 1.5f) return 1.0f;
            if (value < 3.0f) return 2.0f;
            if (value < 6.0f) return 4.0f;
            if (value < 12.0f) return 8.0f;
            if (value < 24.0f) return 16.0f;
            if (value < 48.0f) return 32.0f;
            if (value < 96.0f) return 64.0f;
            return 128.0f;
        }

        private void SaveZoomForView(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _views.Length)
                return;

            float zoom = NormalizeZoom(_views[viewIndex].Zoom);
            _views[viewIndex].Zoom = zoom;

            if (viewIndex == 0 && _player1LastZoomConfig != null)
                _player1LastZoomConfig.Value = zoom;

            if (viewIndex == 1 && _player2LastZoomConfig != null)
                _player2LastZoomConfig.Value = zoom;

            try
            {
                Config.Save();
            }
            catch
            {
            }
        }

        private void InitializeWorldToolsReflection()
        {
            try
            {
                Assembly assembly = typeof(Zone).Assembly;
                Type worldToolsType = assembly.GetType("WorldTools");

                if (worldToolsType == null)
                {
                    Logger.LogWarning("WorldTools type not found.");
                    return;
                }

                _createShoreMaskMethod = worldToolsType.GetMethod(
                    "CreateShoreMaskTexture",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(TerrainData) },
                    null);

                if (_createShoreMaskMethod == null)
                {
                    Logger.LogWarning("WorldTools.CreateShoreMaskTexture(TerrainData) not found.");
                    return;
                }

                Logger.LogInfo("WorldTools.CreateShoreMaskTexture(TerrainData) is available.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("WorldTools reflection init failed: " + ex.Message);
            }
        }

        private void RefreshRuntimeReferences()
        {
            try
            {
                _world = UnityEngine.Object.FindObjectOfType<StrandedWorld>();
                TryLoadNorthCalibrationForCurrentWorld();
                EnsureDiscoveryLoadedForCurrentWorld();
                EnsureHomeIslandLoadedForCurrentWorld();
                EnsureNavigationLoadedForCurrentWorld();
                EnsureWreckNotesLoadedForCurrentWorld();

                Beam.Player[] found = UnityEngine.Object.FindObjectsOfType<Beam.Player>();
                List<Beam.Player> usable = new List<Beam.Player>();

                int i;
                for (i = 0; i < found.Length; i++)
                {
                    Beam.Player player = found[i];
                    if (player == null)
                        continue;

                    if (!player.gameObject.activeInHierarchy)
                        continue;

                    if (player.PlayerCamera == null)
                        continue;

                    if (player.PlayerCamera.Camera == null)
                        continue;

                    usable.Add(player);
                }

                usable.Sort(delegate(Beam.Player a, Beam.Player b)
                {
                    float ax = GetCameraRectX(a);
                    float bx = GetCameraRectX(b);

                    int byCamera = ax.CompareTo(bx);
                    if (byCamera != 0)
                        return byCamera;

                    return a.Id.CompareTo(b.Id);
                });

                _players = usable.ToArray();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Runtime reference refresh failed: " + ex.Message);
                _players = new Beam.Player[0];
            }
        }

        private static float GetCameraRectX(Beam.Player player)
        {
            try
            {
                if (player != null &&
                    player.PlayerCamera != null &&
                    player.PlayerCamera.Camera != null)
                {
                    return player.PlayerCamera.Camera.rect.x;
                }
            }
            catch
            {
            }

            return 999.0f;
        }

        private void DrawNavigationIndicator(int viewIndex)
        {
            if (!IsNavigationHudEnabled())
                return;
            if (viewIndex < 0 ||
                viewIndex >= _views.Length ||
                _views[viewIndex].Open ||
                _players == null ||
                viewIndex >= _players.Length)
            {
                return;
            }

            EnsureNavigationLoadedForCurrentWorld();

            string targetKey =
                _navigationTargetZoneKeys[viewIndex];

            if (String.IsNullOrEmpty(targetKey))
                return;

            Beam.Player player =
                _players[viewIndex];

            if (player == null)
                return;

            Zone target =
                FindZoneByKey(targetKey);

            if (target == null ||
                IsHiddenBiome(target) ||
                !IsZoneDiscovered(target))
            {
                return;
            }

            Vector3 direction =
                target.transform.position -
                player.transform.position;

            direction.y = 0.0f;

            if (direction.sqrMagnitude <
                NavigationArrivalRadius *
                NavigationArrivalRadius)
            {
                return;
            }

            Vector3 forward =
                GetPlayerViewForward(player);

            forward.y = 0.0f;

            if (forward.sqrMagnitude < 0.0001f ||
                direction.sqrMagnitude < 0.0001f)
            {
                return;
            }

            forward.Normalize();
            direction.Normalize();

            float forwardYaw =
                Mathf.Atan2(
                    forward.x,
                    forward.z) *
                Mathf.Rad2Deg;

            float targetYaw =
                Mathf.Atan2(
                    direction.x,
                    direction.z) *
                Mathf.Rad2Deg;

            float relativeAngle =
                Mathf.DeltaAngle(
                    forwardYaw,
                    targetYaw);

            Rect viewport =
                GetViewportRect(viewIndex);

            if (viewport.width < 100.0f ||
                viewport.height < 100.0f)
            {
                return;
            }

            GUI.BeginGroup(viewport);

            // Small sat-nav style pointer in the bottom-left of THIS player's
            // viewport. It rotates in place and never crosses into the partner's UI.
            Vector2 pivot =
                new Vector2(
                    44.0f,
                    viewport.height - 46.0f);

            float arrowSize = 30.0f;

            Matrix4x4 oldMatrix =
                GUI.matrix;

            // The generated navigation texture points visually upward.
            GUIUtility.RotateAroundPivot(
                relativeAngle,
                pivot);

            GUI.color =
                new Color(
                    0.16f,
                    0.48f,
                    1.0f,
                    0.72f);

            GUI.DrawTexture(
                new Rect(
                    pivot.x - arrowSize * 0.5f,
                    pivot.y - arrowSize * 0.5f,
                    arrowSize,
                    arrowSize),
                _navigationArrowTexture != null
                    ? _navigationArrowTexture
                    : _arrowTexture);

            GUI.matrix = oldMatrix;
            GUI.color = Color.white;

            GUI.EndGroup();
        }

        private void DrawPlayerMap(int viewIndex)
        {
            Rect viewport = GetViewportRect(viewIndex);
            if (viewport.width < 100.0f || viewport.height < 100.0f)
                return;

            GUI.BeginGroup(viewport);

            Rect localViewport = new Rect(0.0f, 0.0f, viewport.width, viewport.height);

            // The world remains alive behind the map. Only the local player's half
            // is darkened; the parchment floats above it.
            GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.68f);
            GUI.DrawTexture(localViewport, _pixelTexture);
            GUI.color = Color.white;

            Rect basePaperRect = GetBasePaperRect(localViewport);
            Rect baseMapRect = GetBaseMapRect(basePaperRect);
            Rect baseCompassRect = GetBaseCompassRect(basePaperRect);

            MapViewState view = _views[viewIndex];

            if (view.CenterOnOpen)
            {
                if (CenterViewOnOwningPlayer(viewIndex))
                    view.CenterOnOpen = false;
            }

            Vector2 focusPoint = GetPaperFocusPoint(baseMapRect, view);
            Vector2 screenCenter = localViewport.center;

            Rect paperRect = TransformPaperRect(basePaperRect, focusPoint, screenCenter, view.Zoom);
            Rect mapRect = TransformPaperRect(baseMapRect, focusPoint, screenCenter, view.Zoom);
            Rect compassRect = TransformPaperRect(baseCompassRect, focusPoint, screenCenter, view.Zoom);

            // v0.10c: all zoomed map artwork is rendered inside an
            // explicit second clip whose size is exactly THIS player's viewport.
            // This is intentionally redundant with the outer viewport group: older
            // 64x experiments showed GUI-matrix-rotated island textures escaping into the other
            // split-screen half. Keeping the transform-heavy layer in its own clip
            // makes the split boundary a hard rendering boundary before we expose 64x.
            Matrix4x4 mapLayerMatrix = GUI.matrix;
            GUI.BeginGroup(localViewport);

            DrawParchment(paperRect, view.Zoom);

            Zone[] zones = GetZones();
            if (zones != null && zones.Length > 0)
            {
                WorldBounds bounds;
                if (TryCalculateWorldBounds(zones, out bounds))
                {
                    DrawKnownIslands(mapRect, zones, bounds, view, viewIndex);
                    if (AreWreckMarkersEnabled())
                        DrawWreckMarkers(mapRect, bounds, view, viewIndex);

                    // v0.11f: manual/local notes are intentionally hidden on the
                    // archipelago overview. They appear only once the player has
                    // zoomed in far enough for island-level detail. Wrecks remain
                    // visible at every zoom because they are navigation landmarks.
                    if (AreManualMarkersEnabled() && view.Zoom >= 16.0f)
                        DrawManualMarkers(mapRect, bounds, view);

                    DrawPlayers(mapRect, bounds, view, viewIndex);
                }
            }

            DrawCompassRose(compassRect);

            // Defensive reset in case any rotated island draw ever exits without
            // restoring GUI.matrix. Do this before ending the nested clip so no
            // transformed state can affect the other player's UI.
            GUI.matrix = mapLayerMatrix;
            GUI.color = Color.white;
            GUI.EndGroup();
            DrawMapCenterCrosshair(view, localViewport);
            DrawHomeControlFeedback(view, localViewport);
            DrawWreckPrompt(viewIndex, localViewport);
            DrawManualMarkerMenu(viewIndex, localViewport);
            DrawManualMarkerContextMenu(viewIndex, localViewport);
            DrawWreckMarkerContextMenu(viewIndex, localViewport);

            GUI.EndGroup();
        }

        private Rect GetViewportRect(int viewIndex)
        {
            if (_players != null && viewIndex < _players.Length)
            {
                Beam.Player player = _players[viewIndex];

                try
                {
                    Camera camera = player.PlayerCamera.Camera;
                    if (camera != null)
                    {
                        Rect r = camera.rect;

                        float x = r.x * Screen.width;
                        float width = r.width * Screen.width;

                        float height = r.height * Screen.height;
                        float y = Screen.height - ((r.y + r.height) * Screen.height);

                        if (width > 0.0f && height > 0.0f)
                            return new Rect(x, y, width, height);
                    }
                }
                catch
                {
                }
            }

            float fallbackWidth = Screen.width * 0.5f;

            if (viewIndex == 0)
                return new Rect(0.0f, 0.0f, fallbackWidth, Screen.height);

            return new Rect(fallbackWidth, 0.0f, fallbackWidth, Screen.height);
        }

        private Zone[] GetZones()
        {
            if (_world == null)
                return null;

            try
            {
                return _world.Zones;
            }
            catch
            {
                return null;
            }
        }

        private bool TryCalculateWorldBounds(Zone[] zones, out WorldBounds bounds)
        {
            bounds = new WorldBounds();

            if (zones == null || zones.Length == 0)
                return false;

            bool found = false;
            int i;

            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];
                if (zone == null)
                    continue;

                Vector3 p = RotateWorldPointToNorthUp(zone.transform.position);

                if (!found)
                {
                    bounds.MinX = p.x;
                    bounds.MaxX = p.x;
                    bounds.MinZ = p.z;
                    bounds.MaxZ = p.z;
                    found = true;
                }
                else
                {
                    if (p.x < bounds.MinX) bounds.MinX = p.x;
                    if (p.x > bounds.MaxX) bounds.MaxX = p.x;
                    if (p.z < bounds.MinZ) bounds.MinZ = p.z;
                    if (p.z > bounds.MaxZ) bounds.MaxZ = p.z;
                }
            }

            if (!found)
                return false;

            float width = bounds.MaxX - bounds.MinX;
            float height = bounds.MaxZ - bounds.MinZ;

            if (width < 0.001f)
                width = 1.0f;

            if (height < 0.001f)
                height = 1.0f;

            float padX = width * 0.06f;
            float padZ = height * 0.06f;

            bounds.MinX -= padX;
            bounds.MaxX += padX;
            bounds.MinZ -= padZ;
            bounds.MaxZ += padZ;

            return true;
        }

        private Rect GetBasePaperRect(Rect viewport)
        {
            float aspect = 4.0f / 3.0f;

            if (_parchmentTexture != null && _parchmentTexture.height > 0)
                aspect = (float)_parchmentTexture.width / (float)_parchmentTexture.height;

            float maxWidth = viewport.width * 0.94f;
            float maxHeight = viewport.height * 0.86f;

            float width = maxWidth;
            float height = width / aspect;

            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }

            return new Rect(
                viewport.center.x - width * 0.5f,
                viewport.center.y - height * 0.5f,
                width,
                height);
        }

        private Rect GetBaseMapRect(Rect paperRect)
        {
            // Safe content area inside the torn parchment.
            // Keep X/Z isotropic, but use more of the usable paper than v0.5a.
            float size = Mathf.Min(
                paperRect.width * 0.72f,
                paperRect.height * 0.82f);

            float centerX = paperRect.center.x - paperRect.width * 0.025f;
            float centerY = paperRect.center.y - paperRect.height * 0.005f;

            return new Rect(
                centerX - size * 0.5f,
                centerY - size * 0.5f,
                size,
                size);
        }

        private Rect GetBaseCompassRect(Rect paperRect)
        {
            // The generated compass contains letters outside the circular rose,
            // so give it a much safer inset than v0.5a.
            float size = Mathf.Min(
                paperRect.width * 0.125f,
                paperRect.height * 0.195f);

            float marginX = paperRect.width * 0.115f;
            float marginY = paperRect.height * 0.125f;

            return new Rect(
                paperRect.xMax - marginX - size,
                paperRect.yMax - marginY - size,
                size,
                size);
        }

        private Vector2 GetPaperFocusPoint(Rect baseMapRect, MapViewState view)
        {
            // view.Pan is stored in normalized world-map coordinates:
            // +X = east, +Y = north. GUI Y runs downward.
            float focusU = 0.5f + view.Pan.x;
            float focusVScreen = 0.5f - view.Pan.y;

            return new Vector2(
                baseMapRect.x + focusU * baseMapRect.width,
                baseMapRect.y + focusVScreen * baseMapRect.height);
        }

        private static Rect TransformPaperRect(
            Rect baseRect,
            Vector2 focusPoint,
            Vector2 screenCenter,
            float zoom)
        {
            float xMin = screenCenter.x + (baseRect.xMin - focusPoint.x) * zoom;
            float yMin = screenCenter.y + (baseRect.yMin - focusPoint.y) * zoom;

            return new Rect(
                xMin,
                yMin,
                baseRect.width * zoom,
                baseRect.height * zoom);
        }

        private void DrawParchment(Rect paperRect, float zoom)
        {
            if (_parchmentTexture != null)
            {
                float shadowOffset = Mathf.Clamp(5.0f * Mathf.Sqrt(zoom), 5.0f, 18.0f);

                GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.34f);
                GUI.DrawTexture(
                    new Rect(
                        paperRect.x + shadowOffset,
                        paperRect.y + shadowOffset,
                        paperRect.width,
                        paperRect.height),
                    _parchmentTexture);

                GUI.color = Color.white;
                GUI.DrawTexture(paperRect, _parchmentTexture);
            }
            else
            {
                GUI.color = new Color(0.78f, 0.68f, 0.50f, 1.0f);
                GUI.DrawTexture(paperRect, _pixelTexture);
                GUI.color = Color.white;
            }
        }

        private void DrawMapCenterCrosshair(
            MapViewState view,
            Rect localViewport)
        {
            if (view == null || _pixelTexture == null)
                return;

            Vector2 center =
                localViewport.center;

            // Default: parchment/ink brown.
            Color crosshairColor =
                new Color(
                    0.24f,
                    0.12f,
                    0.045f,
                    0.78f);

            // While holding a map action the same crosshair becomes a subtle
            // confirmation of the pending marker/navigation action.
            if (view.CenterButtonHeld &&
                !view.CenterButtonLongPressTriggered)
            {
                crosshairColor =
                    new Color(
                        0.50f,
                        0.18f,
                        0.06f,
                        0.92f);
            }
            else if (view.ZoomOutButtonHeld &&
                     !view.ZoomOutButtonLongPressTriggered)
            {
                crosshairColor =
                    new Color(
                        0.46f,
                        0.26f,
                        0.08f,
                        0.92f);
            }
            else if (view.ZoomInButtonHeld &&
                     !view.ZoomInButtonLongPressTriggered)
            {
                crosshairColor =
                    new Color(
                        0.08f,
                        0.32f,
                        0.72f,
                        0.92f);
            }

            GUI.color = crosshairColor;

            const float arm = 10.0f;
            const float gap = 3.0f;
            const float thickness = 1.0f;

            // Horizontal arms.
            GUI.DrawTexture(
                new Rect(
                    center.x - gap - arm,
                    center.y - thickness * 0.5f,
                    arm,
                    thickness),
                _pixelTexture);

            GUI.DrawTexture(
                new Rect(
                    center.x + gap,
                    center.y - thickness * 0.5f,
                    arm,
                    thickness),
                _pixelTexture);

            // Vertical arms.
            GUI.DrawTexture(
                new Rect(
                    center.x - thickness * 0.5f,
                    center.y - gap - arm,
                    thickness,
                    arm),
                _pixelTexture);

            GUI.DrawTexture(
                new Rect(
                    center.x - thickness * 0.5f,
                    center.y + gap,
                    thickness,
                    arm),
                _pixelTexture);

            GUI.color = Color.white;
        }

        private void DrawHomeControlFeedback(
            MapViewState view,
            Rect localViewport)
        {
            if (view == null)
                return;

            if (view.ZoomInButtonHeld &&
                !view.ZoomInButtonLongPressTriggered)
            {
                float heldFor =
                    Time.unscaledTime -
                    view.ZoomInButtonHoldStart;

                if (heldFor >= 0.25f)
                {
                    float progress =
                        Mathf.Clamp01(
                            heldFor /
                            NavigationHoldSeconds);

                    float width = 150.0f;
                    float height = 8.0f;

                    Rect background = new Rect(
                        localViewport.center.x - width * 0.5f,
                        localViewport.center.y + 90.0f,
                        width,
                        height);

                    GUI.color =
                        new Color(
                            0.035f,
                            0.07f,
                            0.13f,
                            0.76f);

                    GUI.DrawTexture(
                        background,
                        _pixelTexture);

                    GUI.color =
                        new Color(
                            0.10f,
                            0.38f,
                            0.80f,
                            0.95f);

                    GUI.DrawTexture(
                        new Rect(
                            background.x,
                            background.y,
                            background.width * progress,
                            background.height),
                        _pixelTexture);

                    GUI.color = Color.white;

                    GUI.Label(
                        new Rect(
                            localViewport.center.x - 105.0f,
                            localViewport.center.y + 60.0f,
                            210.0f,
                            26.0f),
                        "HOLD: NAV TARGET",
                        _smallStyle);
                }
            }

            if (view.ZoomOutButtonHeld &&
                !view.ZoomOutButtonLongPressTriggered)
            {
                float heldFor =
                    Time.unscaledTime -
                    view.ZoomOutButtonHoldStart;

                if (heldFor >= 0.25f)
                {
                    float progress =
                        Mathf.Clamp01(heldFor / AddMarkerHoldSeconds);

                    float width = 150.0f;
                    float height = 8.0f;

                    Rect background = new Rect(
                        localViewport.center.x - width * 0.5f,
                        localViewport.center.y + 132.0f,
                        width,
                        height);

                    GUI.color =
                        new Color(0.10f, 0.055f, 0.025f, 0.75f);

                    GUI.DrawTexture(background, _pixelTexture);

                    GUI.color =
                        new Color(0.46f, 0.26f, 0.08f, 0.95f);

                    GUI.DrawTexture(
                        new Rect(
                            background.x,
                            background.y,
                            background.width * progress,
                            background.height),
                        _pixelTexture);

                    GUI.color = Color.white;

                    GUI.Label(
                        new Rect(
                            localViewport.center.x - 100.0f,
                            localViewport.center.y + 102.0f,
                            200.0f,
                            26.0f),
                        "HOLD: ADD MARKER",
                        _smallStyle);
                }
            }

            if (view.CenterButtonHeld &&
                !view.CenterButtonLongPressTriggered)
            {
                float progress =
                    Mathf.Clamp01(
                        (Time.unscaledTime -
                         view.CenterButtonHoldStart) /
                        MarkerContextHoldSeconds);

                float width = 150.0f;
                float height = 8.0f;

                Rect background = new Rect(
                    localViewport.center.x - width * 0.5f,
                    localViewport.center.y + 48.0f,
                    width,
                    height);

                GUI.color =
                    new Color(
                        0.10f,
                        0.055f,
                        0.025f,
                        0.75f);

                GUI.DrawTexture(
                    background,
                    _pixelTexture);

                GUI.color =
                    new Color(
                        0.50f,
                        0.18f,
                        0.06f,
                        0.95f);

                GUI.DrawTexture(
                    new Rect(
                        background.x,
                        background.y,
                        background.width * progress,
                        background.height),
                    _pixelTexture);

                GUI.color = Color.white;

                GUI.Label(
                    new Rect(
                        localViewport.center.x - 100.0f,
                        localViewport.center.y + 18.0f,
                        200.0f,
                        26.0f),
                    "HOLD: EDIT MARKER",
                    _smallStyle);
            }

            if (!String.IsNullOrEmpty(view.HomeFeedbackText) &&
                Time.unscaledTime < view.HomeFeedbackUntil)
            {
                GUI.Label(
                    new Rect(
                        localViewport.center.x - 120.0f,
                        localViewport.center.y + 64.0f,
                        240.0f,
                        30.0f),
                    view.HomeFeedbackText,
                    _smallStyle);
            }
        }

        private void DrawCompassRose(Rect compassRect)
        {
            if (_compassRoseTexture != null)
            {
                GUI.color = new Color(0.44f, 0.27f, 0.13f, 0.88f);
                GUI.DrawTexture(compassRect, _compassRoseTexture);
                GUI.color = Color.white;
                return;
            }

            // Fallback if the external asset is missing.
            GUI.Label(
                new Rect(compassRect.center.x - 20.0f, compassRect.y, 40.0f, 24.0f),
                "N",
                _compassStyle);
        }

        private void DrawKnownIslands(
            Rect mapRect,
            Zone[] zones,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex)
        {
            int i;

            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];
                if (zone == null)
                    continue;

                if (IsHiddenBiome(zone))
                    continue;

                bool discovered = IsZoneDiscovered(zone);

                if (IsFogOfWarEnabled())
                {
                    if (!discovered)
                        continue;

                    DrawDiscoveryBlob(zone, mapRect, bounds, view, viewIndex);

                    // With fog of war enabled, exact coastline still comes only from
                    // the persistent exploration mask.
                    ZoneContourData exploredContour;
                    if (TryGetZoneContour(zone, out exploredContour))
                        DrawZoneContour(zone, exploredContour, mapRect, bounds, view, viewIndex, false);
                }
                else
                {
                    // Display-only reveal. Discovery and Exploration are NOT changed.
                    // If a full contour cannot be obtained for an unloaded island,
                    // at least show its rough island blob.
                    ZoneContourData fullContour;
                    if (TryGetZoneContour(zone, out fullContour))
                        DrawZoneContour(zone, fullContour, mapRect, bounds, view, viewIndex, true);
                    else
                        DrawDiscoveryBlob(zone, mapRect, bounds, view, viewIndex);
                }
            }
        }

        private void DrawDiscoveryBlob(
            Zone zone,
            Rect mapRect,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex)
        {
            Texture2D blob = GetOrCreateDiscoveryBlob(zone);
            if (blob == null)
                return;

            float sizeWorld = GetDiscoveryBlobWorldSize(zone);

            Vector3 centerWorld = zone.transform.position;
            Vector2 centerNormalized = WorldToNormalized(centerWorld, bounds);
            Vector2 screenNormalized = ApplyView(centerNormalized, view);
            Vector2 centerPoint = NormalizedToMap(screenNormalized, mapRect);

            float boundsWidth = Mathf.Max(0.001f, bounds.MaxX - bounds.MinX);
            float boundsHeight = Mathf.Max(0.001f, bounds.MaxZ - bounds.MinZ);

            float pixelWidth = Mathf.Max(
                11.0f,
                (sizeWorld / boundsWidth) * mapRect.width);

            float pixelHeight = Mathf.Max(
                11.0f,
                (sizeWorld / boundsHeight) * mapRect.height);

            Rect rect = new Rect(
                centerPoint.x - pixelWidth * 0.5f,
                centerPoint.y - pixelHeight * 0.5f,
                pixelWidth,
                pixelHeight);

            if (!RectIntersects(mapRect, rect))
                return;

            if (IsNavigationTarget(zone, viewIndex))
            {
                GUI.color =
                    new Color(
                        0.10f,
                        0.29f,
                        0.58f,
                        0.62f);
            }
            else if (IsHomeIsland(zone))
            {
                GUI.color =
                    new Color(
                        0.20f,
                        0.39f,
                        0.14f,
                        0.58f);
            }
            else
            {
                GUI.color = zone.IsStartingIsland
                    ? new Color(0.34f, 0.17f, 0.055f, 0.43f)
                    : new Color(0.28f, 0.14f, 0.05f, 0.36f);
            }

            GUI.DrawTexture(rect, blob);
            GUI.color = Color.white;
        }

        private Texture2D GetOrCreateDiscoveryBlob(Zone zone)
        {
            if (zone == null)
                return null;

            string key = GetZoneKey(zone);
            Texture2D existing;

            if (_discoveryBlobCache.TryGetValue(key, out existing) && existing != null)
                return existing;

            int seed = StableHash(ReadWorldSeed() + "|" + key + "|" + zone.Biome.ToString());
            Texture2D blob = CreateProceduralDiscoveryBlob(seed, 64);

            _discoveryBlobCache[key] = blob;
            return blob;
        }

        private Texture2D CreateProceduralDiscoveryBlob(int seed, int size)
        {
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            System.Random random = new System.Random(seed);

            const int segments = 12;
            float[] radii = new float[segments];

            int i;
            for (i = 0; i < segments; i++)
            {
                // Deliberately coarse. It communicates "land exists here" without
                // leaking bays, coves, narrow spits, or the true shoreline.
                radii[i] = 0.68f + (float)random.NextDouble() * 0.27f;
            }

            Color32 visible = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(255, 255, 255, 0);
            Color32[] pixels = new Color32[size * size];

            float center = (size - 1) * 0.5f;
            float baseRadius = size * 0.42f;

            int y;
            int x;

            for (y = 0; y < size; y++)
            {
                for (x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float angle = Mathf.Atan2(dy, dx);
                    if (angle < 0.0f)
                        angle += Mathf.PI * 2.0f;

                    float segmentPosition =
                        angle / (Mathf.PI * 2.0f) * segments;

                    int a = Mathf.FloorToInt(segmentPosition) % segments;
                    int b = (a + 1) % segments;
                    float t = segmentPosition - Mathf.Floor(segmentPosition);

                    // Smooth interpolation between the deliberately sparse control radii.
                    t = t * t * (3.0f - 2.0f * t);

                    float localRadius =
                        Mathf.Lerp(radii[a], radii[b], t) *
                        baseRadius;

                    pixels[y * size + x] =
                        distance <= localRadius
                        ? visible
                        : clear;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private float GetDiscoveryBlobWorldSize(Zone zone)
        {
            string biome = zone.Biome.ToString();

            if (String.Equals(biome, "ISLAND_SMALL", StringComparison.OrdinalIgnoreCase))
                return 145.0f;

            return 190.0f;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;

                if (value == null)
                    return hash;

                int i;
                for (i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];

                return hash;
            }
        }

        private bool IsHiddenBiome(Zone zone)
        {
            if (zone == null)
                return true;

            string biome = zone.Biome.ToString();

            return
                String.Equals(biome, "MISSION", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(biome, "CARRIER", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsZoneDiscovered(Zone zone)
        {
            if (zone == null || IsHiddenBiome(zone))
                return false;

            return _discoveredZones.Contains(GetZoneKey(zone));
        }

        private void DrawZoneContour(
            Zone zone,
            ZoneContourData contour,
            Rect mapRect,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex,
            bool revealFull)
        {
            if (contour == null || contour.Texture == null)
            {
                DrawFallbackIslandMarker(zone, mapRect, bounds, view, viewIndex);
                return;
            }

            Rect islandRect = WorldAreaToMapRect(
                contour.WorldXMin,
                contour.WorldXMax,
                contour.WorldZMin,
                contour.WorldZMax,
                bounds,
                view,
                mapRect);

            if (islandRect.width <= 1.0f || islandRect.height <= 1.0f)
                return;

            if (!RectIntersects(mapRect, islandRect))
                return;

            if (!revealFull)
            {
                EnsureExplorationData(zone, contour);
                if (contour.ExploredTexture == null)
                    return;
            }

            // Do NOT rotate large island rectangles through GUI.matrix here.
            // At very high zoom Unity IMGUI can let a rotated DrawTexture escape
            // the current BeginGroup clip and paint into the other split-screen half.
            // v0.10c bakes the north rotation into a cached display texture instead,
            // so the actual GUI draw stays axis-aligned and is clipped reliably.
            Texture2D exploredDisplayTexture;
            Texture2D outlineDisplayTexture;
            float displayWidthScale;
            float displayHeightScale;

            if (revealFull)
            {
                GetNorthUpFullContourDisplayTextures(
                    contour,
                    out exploredDisplayTexture,
                    out outlineDisplayTexture,
                    out displayWidthScale,
                    out displayHeightScale);
            }
            else
            {
                GetNorthUpContourDisplayTextures(
                    contour,
                    out exploredDisplayTexture,
                    out outlineDisplayTexture,
                    out displayWidthScale,
                    out displayHeightScale);
            }

            if (exploredDisplayTexture == null)
                return;

            Rect displayRect = new Rect(
                islandRect.center.x - islandRect.width * displayWidthScale * 0.5f,
                islandRect.center.y - islandRect.height * displayHeightScale * 0.5f,
                islandRect.width * displayWidthScale,
                islandRect.height * displayHeightScale);

            if (IsNavigationTarget(zone, viewIndex))
            {
                GUI.color =
                    new Color(
                        0.09f,
                        0.30f,
                        0.62f,
                        0.44f);
            }
            else if (IsHomeIsland(zone))
            {
                GUI.color =
                    new Color(
                        0.18f,
                        0.40f,
                        0.12f,
                        0.42f);
            }
            else
            {
                GUI.color = zone.IsStartingIsland
                    ? new Color(0.34f, 0.18f, 0.065f, 0.34f)
                    : new Color(0.29f, 0.15f, 0.055f, 0.30f);
            }

            GUI.DrawTexture(displayRect, exploredDisplayTexture);

            if (outlineDisplayTexture != null)
            {
                if (IsNavigationTarget(zone, viewIndex))
                {
                    GUI.color =
                        new Color(
                            0.045f,
                            0.20f,
                            0.58f,
                            0.99f);
                }
                else if (IsHomeIsland(zone))
                {
                    GUI.color =
                        new Color(
                            0.10f,
                            0.30f,
                            0.065f,
                            0.98f);
                }
                else
                {
                    GUI.color = zone.IsStartingIsland
                        ? new Color(0.25f, 0.105f, 0.025f, 0.96f)
                        : new Color(0.22f, 0.085f, 0.02f, 0.94f);
                }

                GUI.DrawTexture(displayRect, outlineDisplayTexture);
            }

            GUI.color = Color.white;
        }

        private void GetNorthUpContourDisplayTextures(
            ZoneContourData contour,
            out Texture2D exploredTexture,
            out Texture2D outlineTexture,
            out float widthScale,
            out float heightScale)
        {
            exploredTexture = contour != null ? contour.ExploredTexture : null;
            outlineTexture = contour != null ? contour.ExploredOutlineTexture : null;
            widthScale = 1.0f;
            heightScale = 1.0f;

            if (contour == null || contour.ExploredTexture == null)
                return;

            if (!_northCalibrationValid || Mathf.Abs(_northYawDegrees) <= 0.001f)
                return;

            bool cacheValid =
                contour.NorthUpExploredTexture != null &&
                contour.NorthUpOutlineTexture != null &&
                contour.NorthUpRevision == contour.ExplorationRevision &&
                Mathf.Abs(Mathf.DeltaAngle(contour.NorthUpYawDegrees, _northYawDegrees)) < 0.01f;

            if (!cacheValid)
            {
                DestroyTexture(contour.NorthUpExploredTexture);
                DestroyTexture(contour.NorthUpOutlineTexture);
                contour.NorthUpExploredTexture = null;
                contour.NorthUpOutlineTexture = null;

                Texture2D rotatedExplored;
                Texture2D rotatedOutline;
                float rotatedWidthScale;
                float rotatedHeightScale;

                if (TryRotateTexturePairForNorthUp(
                        contour.ExploredTexture,
                        contour.ExploredOutlineTexture,
                        _northYawDegrees,
                        out rotatedExplored,
                        out rotatedOutline,
                        out rotatedWidthScale,
                        out rotatedHeightScale))
                {
                    contour.NorthUpExploredTexture = rotatedExplored;
                    contour.NorthUpOutlineTexture = rotatedOutline;
                    contour.NorthUpWidthScale = rotatedWidthScale;
                    contour.NorthUpHeightScale = rotatedHeightScale;
                    contour.NorthUpRevision = contour.ExplorationRevision;
                    contour.NorthUpYawDegrees = _northYawDegrees;
                }
            }

            if (contour.NorthUpExploredTexture != null)
            {
                exploredTexture = contour.NorthUpExploredTexture;
                outlineTexture = contour.NorthUpOutlineTexture;
                widthScale = Mathf.Max(0.001f, contour.NorthUpWidthScale);
                heightScale = Mathf.Max(0.001f, contour.NorthUpHeightScale);
            }
        }

        private bool TryRotateTexturePairForNorthUp(
            Texture2D sourceExplored,
            Texture2D sourceOutline,
            float northYawDegrees,
            out Texture2D rotatedExplored,
            out Texture2D rotatedOutline,
            out float widthScale,
            out float heightScale)
        {
            rotatedExplored = null;
            rotatedOutline = null;
            widthScale = 1.0f;
            heightScale = 1.0f;

            if (sourceExplored == null)
                return false;

            try
            {
                int sourceWidth = sourceExplored.width;
                int sourceHeight = sourceExplored.height;

                if (sourceWidth <= 0 || sourceHeight <= 0)
                    return false;

                // GUI rotated the old island rectangle by -northYawDegrees in
                // screen coordinates. Texture2D pixel Y points upward, so baking
                // the equivalent visual rotation requires +northYawDegrees here.
                float radians = northYawDegrees * Mathf.Deg2Rad;
                float c = Mathf.Cos(radians);
                float s = Mathf.Sin(radians);
                float ac = Mathf.Abs(c);
                float ass = Mathf.Abs(s);

                int outputWidth = Mathf.Max(1, Mathf.CeilToInt(sourceWidth * ac + sourceHeight * ass));
                int outputHeight = Mathf.Max(1, Mathf.CeilToInt(sourceWidth * ass + sourceHeight * ac));

                Color32[] sourceExploredPixels = sourceExplored.GetPixels32();
                Color32[] sourceOutlinePixels = sourceOutline != null
                    ? sourceOutline.GetPixels32()
                    : null;

                Color32 transparent = new Color32(255, 255, 255, 0);
                Color32[] outputExploredPixels = new Color32[outputWidth * outputHeight];
                Color32[] outputOutlinePixels = sourceOutlinePixels != null
                    ? new Color32[outputWidth * outputHeight]
                    : null;

                int clearIndex;
                for (clearIndex = 0; clearIndex < outputExploredPixels.Length; clearIndex++)
                {
                    outputExploredPixels[clearIndex] = transparent;
                    if (outputOutlinePixels != null)
                        outputOutlinePixels[clearIndex] = transparent;
                }

                float sourceCx = (sourceWidth - 1) * 0.5f;
                float sourceCy = (sourceHeight - 1) * 0.5f;
                float outputCx = (outputWidth - 1) * 0.5f;
                float outputCy = (outputHeight - 1) * 0.5f;

                int y;
                int x;
                for (y = 0; y < outputHeight; y++)
                {
                    float dy = y - outputCy;

                    for (x = 0; x < outputWidth; x++)
                    {
                        float dx = x - outputCx;

                        // Inverse rotation: destination -> source.
                        float sourceX = c * dx + s * dy + sourceCx;
                        float sourceY = -s * dx + c * dy + sourceCy;

                        int sx = Mathf.RoundToInt(sourceX);
                        int sy = Mathf.RoundToInt(sourceY);

                        if (sx < 0 || sx >= sourceWidth || sy < 0 || sy >= sourceHeight)
                            continue;

                        int sourceIndex = sy * sourceWidth + sx;
                        int outputIndex = y * outputWidth + x;
                        outputExploredPixels[outputIndex] = sourceExploredPixels[sourceIndex];

                        if (outputOutlinePixels != null)
                            outputOutlinePixels[outputIndex] = sourceOutlinePixels[sourceIndex];
                    }
                }

                rotatedExplored = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false);
                rotatedExplored.wrapMode = TextureWrapMode.Clamp;
                rotatedExplored.filterMode = FilterMode.Bilinear;
                rotatedExplored.SetPixels32(outputExploredPixels);
                rotatedExplored.Apply();

                if (outputOutlinePixels != null)
                {
                    rotatedOutline = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false);
                    rotatedOutline.wrapMode = TextureWrapMode.Clamp;
                    rotatedOutline.filterMode = FilterMode.Bilinear;
                    rotatedOutline.SetPixels32(outputOutlinePixels);
                    rotatedOutline.Apply();
                }

                widthScale = (float)outputWidth / (float)sourceWidth;
                heightScale = (float)outputHeight / (float)sourceHeight;
                return true;
            }
            catch (Exception ex)
            {
                DestroyTexture(rotatedExplored);
                DestroyTexture(rotatedOutline);
                rotatedExplored = null;
                rotatedOutline = null;

                Logger.LogWarning(
                    "North-up contour bake failed: " +
                    ex.Message);

                return false;
            }
        }

        private void DrawFallbackIslandMarker(
            Zone zone,
            Rect mapRect,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex)
        {
            Vector2 normalized = WorldToNormalized(zone.transform.position, bounds);
            Vector2 screenNormalized = ApplyView(normalized, view);

            if (!IsInsideMap(screenNormalized))
                return;

            Vector2 point = NormalizedToMap(screenNormalized, mapRect);

            float markerSize = 11.0f;

            GUI.color = IsNavigationTarget(zone, viewIndex)
                ? new Color(0.06f, 0.25f, 0.68f, 0.96f)
                : IsHomeIsland(zone)
                    ? new Color(0.12f, 0.34f, 0.08f, 0.92f)
                    : new Color(0.32f, 0.18f, 0.08f, 0.85f);

            GUI.DrawTexture(
                new Rect(
                    point.x - markerSize * 0.5f,
                    point.y - markerSize * 0.5f,
                    markerSize,
                    markerSize),
                _circleTexture);

            GUI.color = Color.white;
        }

        private void DrawPlayers(
            Rect mapRect,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex)
        {
            if (_players == null)
                return;

            int count = Math.Min(_players.Length, 2);
            int i;

            for (i = 0; i < count; i++)
            {
                Beam.Player player = _players[i];
                if (player == null)
                    continue;

                Vector2 normalized = WorldToNormalized(player.transform.position, bounds);
                Vector2 screenNormalized = ApplyView(normalized, view);

                if (!IsInsideMap(screenNormalized))
                    continue;

                Vector2 point = NormalizedToMap(screenNormalized, mapRect);
                Vector3 forward = GetPlayerViewForward(player);
                forward = RotateWorldDirectionToNorthUp(forward);

                // CreateArrowTexture() was authored in texture pixel coordinates,
                // where y=0 is the bottom edge. Its visual arrow therefore points
                // opposite to the screen-space "up" direction used by our heading math.
                // Add 180 degrees so a calibrated North vector renders toward map N.
                float angle =
                    Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg +
                    180.0f;

                Color markerColor = i == 0
                    ? new Color(0.35f, 0.78f, 1.0f, 1.0f)
                    : new Color(1.0f, 0.48f, 0.34f, 1.0f);

                bool isOwningPlayer = i == viewIndex;

                if (!isOwningPlayer && !IsPartnerMarkerEnabled())
                    continue;

                float ownerScale = isOwningPlayer ? GetOwnPlayerArrowScale() : 1.0f;

                float arrowWidth =
                    (isOwningPlayer ? 34.0f : 24.0f) * ownerScale;

                float arrowHeight =
                    (isOwningPlayer ? 46.0f : 32.0f) * ownerScale;

                Matrix4x4 oldMatrix = GUI.matrix;

                GUIUtility.RotateAroundPivot(angle, point);
                GUI.color = markerColor;
                GUI.DrawTexture(
                    new Rect(
                        point.x - arrowWidth * 0.5f,
                        point.y - arrowHeight * 0.5f,
                        arrowWidth,
                        arrowHeight),
                    _arrowTexture);

                GUI.matrix = oldMatrix;
                GUI.color = Color.white;

            }
        }

        private void CalibrateNorthFromLeftPlayer()
        {
            RefreshRuntimeReferences();

            if (_players == null || _players.Length == 0 || _players[0] == null)
            {
                Logger.LogWarning("North calibration failed: left player was not found.");
                return;
            }

            Vector3 forward = GetPlayerViewForward(_players[0]);
            forward.y = 0.0f;

            if (forward.sqrMagnitude < 0.0001f)
            {
                Logger.LogWarning("North calibration failed: player camera forward vector is invalid.");
                return;
            }

            forward.Normalize();

            _northYawDegrees =
                Mathf.Atan2(forward.x, forward.z) *
                Mathf.Rad2Deg;

            _northCalibrationValid = true;
            _northCalibrationSeed = ReadWorldSeed();

            SaveNorthCalibration();

            Logger.LogInfo(
                "North calibrated. World north yaw = " +
                _northYawDegrees.ToString("0.000", CultureInfo.InvariantCulture) +
                " degrees. Seed = " +
                _northCalibrationSeed);
        }

        private void TryLoadNorthCalibrationForCurrentWorld()
        {
            if (_world == null)
                return;

            string seed = ReadWorldSeed();

            if (String.Equals(seed, _northCalibrationSeed, StringComparison.Ordinal))
                return;

            _northCalibrationSeed = seed;
            _northCalibrationValid = false;
            _northYawDegrees = 0.0f;

            try
            {
                string path = GetNorthCalibrationPath(seed);

                if (!File.Exists(path))
                    return;

                string raw = File.ReadAllText(path).Trim();
                float yaw = ParseInvariantFloat(raw);

                _northYawDegrees = yaw;
                _northCalibrationValid = true;

                Logger.LogInfo(
                    "Loaded north calibration: " +
                    yaw.ToString("0.000", CultureInfo.InvariantCulture) +
                    " degrees for seed " +
                    seed);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to load north calibration: " + ex.Message);
            }
        }

        private void SaveNorthCalibration()
        {
            if (!_northCalibrationValid)
                return;

            try
            {
                string seed = ReadWorldSeed();
                string path = GetNorthCalibrationPath(seed);
                string directory = Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(
                    path,
                    _northYawDegrees.ToString("R", CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to save north calibration: " + ex.Message);
            }
        }

        private string GetNorthCalibrationPath(string seed)
        {
            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "NorthCalibration",
                SafeFileName(seed) + ".txt");
        }

        private Vector3 RotateWorldPointToNorthUp(Vector3 point)
        {
            if (!_northCalibrationValid)
                return point;

            float radians = _northYawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);

            float x = c * point.x - s * point.z;
            float z = s * point.x + c * point.z;

            return new Vector3(x, point.y, z);
        }

        private Vector3 RotateWorldDirectionToNorthUp(Vector3 direction)
        {
            if (!_northCalibrationValid)
                return direction;

            float radians = _northYawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);

            float x = c * direction.x - s * direction.z;
            float z = s * direction.x + c * direction.z;

            return new Vector3(x, direction.y, z);
        }

        private Vector3 GetPlayerViewForward(Beam.Player player)
        {
            Vector3 forward = Vector3.zero;

            try
            {
                if (player != null &&
                    player.PlayerCamera != null &&
                    player.PlayerCamera.Camera != null)
                {
                    forward = player.PlayerCamera.Camera.transform.forward;
                }
            }
            catch
            {
            }

            forward.y = 0.0f;

            if (forward.sqrMagnitude < 0.0001f && player != null)
            {
                forward = player.transform.forward;
                forward.y = 0.0f;
            }

            if (forward.sqrMagnitude < 0.0001f)
                return Vector3.forward;

            return forward.normalized;
        }

        private bool CenterViewOnOwningPlayer(int viewIndex)
        {
            if (viewIndex < 0 ||
                _players == null ||
                viewIndex >= _players.Length ||
                _players[viewIndex] == null)
            {
                return false;
            }

            Zone[] zones = GetZones();
            if (zones == null || zones.Length == 0)
                return false;

            WorldBounds bounds;
            if (!TryCalculateWorldBounds(zones, out bounds))
                return false;

            Vector2 normalized = WorldToNormalized(
                _players[viewIndex].transform.position,
                bounds);

            MapViewState view = _views[viewIndex];

            view.Pan = new Vector2(
                normalized.x - 0.5f,
                normalized.y - 0.5f);

            ClampPan(view);
            return true;
        }

        private void DrawCompass(Rect mapRect)
        {
            GUI.Label(
                new Rect(mapRect.center.x - 20.0f, mapRect.y - 31.0f, 40.0f, 26.0f),
                "N",
                _compassStyle);

            GUI.Label(
                new Rect(mapRect.center.x - 20.0f, mapRect.yMax + 4.0f, 40.0f, 26.0f),
                "S",
                _compassStyle);

            GUI.Label(
                new Rect(mapRect.x - 31.0f, mapRect.center.y - 13.0f, 28.0f, 26.0f),
                "W",
                _compassStyle);

            GUI.Label(
                new Rect(mapRect.xMax + 3.0f, mapRect.center.y - 13.0f, 28.0f, 26.0f),
                "E",
                _compassStyle);
        }


        private bool TryGetZoneContour(Zone zone, out ZoneContourData data)
        {
            data = null;

            if (zone == null)
                return false;

            string key = GetZoneKey(zone);

            if (_zoneContourCache.TryGetValue(key, out data) && data != null && data.Texture != null)
            {
                EnsureExplorationData(zone, data);
                return true;
            }

            if (TryLoadContourFromDisk(zone, out data))
            {
                _zoneContourCache[key] = data;
                EnsureExplorationData(zone, data);
                return true;
            }

            if (TryGenerateContourFromLoadedTerrain(zone, out data))
            {
                _zoneContourCache[key] = data;
                TrySaveContourToDisk(zone, data);
                EnsureExplorationData(zone, data);
                return true;
            }

            return false;
        }

        private bool TryGenerateContourFromLoadedTerrain(Zone zone, out ZoneContourData data)
        {
            data = null;

            if (_createShoreMaskMethod == null)
                return false;

            try
            {
                UnityEngine.Terrain terrain = zone.Terrain;
                if (terrain == null || terrain.terrainData == null)
                    return false;

                object raw = _createShoreMaskMethod.Invoke(
                    null,
                    new object[] { terrain.terrainData });

                Texture2D sourceTexture = raw as Texture2D;
                if (sourceTexture == null)
                    return false;

                Texture2D cleanedTexture = null;
                try
                {
                    cleanedTexture = CleanupShoreMask(sourceTexture);
                }
                finally
                {
                    try
                    {
                        UnityEngine.Object.Destroy(sourceTexture);
                    }
                    catch
                    {
                    }
                }

                if (cleanedTexture == null)
                    return false;

                Vector3 p = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;

                data = new ZoneContourData();
                data.Texture = cleanedTexture;
                data.WorldXMin = p.x;
                data.WorldXMax = p.x + size.x;
                data.WorldZMin = p.z;
                data.WorldZMax = p.z + size.z;

                Logger.LogInfo("Generated contour for zone: " + zone.Id);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Contour generation failed for zone " + zone.Id + ": " + ex.Message);
                return false;
            }
        }

        private Texture2D CleanupShoreMask(Texture2D sourceTexture)
        {
            if (sourceTexture == null)
                return null;

            int width = sourceTexture.width;
            int height = sourceTexture.height;

            Color32[] pixels;
            try
            {
                pixels = sourceTexture.GetPixels32();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("GetPixels32 failed for shore mask: " + ex.Message);
                return null;
            }

            int total = width * height;
            bool[] land = new bool[total];
            int i;

            for (i = 0; i < total; i++)
            {
                Color32 c = pixels[i];
                land[i] = c.a > 10 && (c.r + c.g + c.b) > 30;
            }

            bool[] keep = FilterConnectedComponents(land, width, height, 8);

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.wrapMode = TextureWrapMode.Clamp;
            result.filterMode = FilterMode.Bilinear;

            Color32 landColor = new Color32(255, 255, 255, 255);
            Color32 seaColor = new Color32(255, 255, 255, 0);
            Color32[] outPixels = new Color32[total];

            for (i = 0; i < total; i++)
                outPixels[i] = keep[i] ? landColor : seaColor;

            result.SetPixels32(outPixels);
            result.Apply();

            return result;
        }

        private bool[] FilterConnectedComponents(bool[] land, int width, int height, int minPixels)
        {
            int total = width * height;
            bool[] visited = new bool[total];
            bool[] keep = new bool[total];
            int[] queue = new int[total];
            List<int> component = new List<int>(256);

            int largestCount = 0;
            int[] largestIndices = null;

            int i;
            for (i = 0; i < total; i++)
            {
                if (!land[i] || visited[i])
                    continue;

                component.Clear();

                int head = 0;
                int tail = 0;

                visited[i] = true;
                queue[tail++] = i;

                while (head < tail)
                {
                    int current = queue[head++];
                    component.Add(current);

                    int x = current % width;
                    int y = current / width;

                    AddNeighbor(x - 1, y, width, height, land, visited, queue, ref tail);
                    AddNeighbor(x + 1, y, width, height, land, visited, queue, ref tail);
                    AddNeighbor(x, y - 1, width, height, land, visited, queue, ref tail);
                    AddNeighbor(x, y + 1, width, height, land, visited, queue, ref tail);
                }

                if (component.Count >= minPixels)
                {
                    int c;
                    for (c = 0; c < component.Count; c++)
                        keep[component[c]] = true;
                }

                if (component.Count > largestCount)
                {
                    largestCount = component.Count;
                    largestIndices = component.ToArray();
                }
            }

            if (largestCount > 0)
            {
                int j;
                for (j = 0; j < largestIndices.Length; j++)
                    keep[largestIndices[j]] = true;
            }

            return keep;
        }

        private void AddNeighbor(
            int x,
            int y,
            int width,
            int height,
            bool[] land,
            bool[] visited,
            int[] queue,
            ref int tail)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;

            int index = y * width + x;

            if (!land[index] || visited[index])
                return;

            visited[index] = true;
            queue[tail++] = index;
        }

        private void EnsureDiscoveryLoadedForCurrentWorld()
        {
            if (_world == null)
                return;

            string seed = ReadWorldSeed();

            if (_discoveryLoaded &&
                String.Equals(seed, _discoverySeed, StringComparison.Ordinal))
            {
                return;
            }

            _discoveredZones.Clear();
            _discoveryLoaded = true;
            _discoverySeed = seed;
            _discoveryDirty = false;

            try
            {
                string path = GetDiscoveryPath();

                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    int i;

                    for (i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();

                        if (!String.IsNullOrEmpty(line))
                            _discoveredZones.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to load discovered zones: " + ex.Message);
            }

            // Migration: islands already visited before v0.4b are obviously known.
            Zone[] zones = GetZones();
            if (zones != null)
            {
                int i;

                for (i = 0; i < zones.Length; i++)
                {
                    Zone zone = zones[i];

                    if (zone == null || IsHiddenBiome(zone))
                        continue;

                    if (zone.HasVisited)
                        MarkZoneDiscovered(zone);
                }
            }

            SaveDiscoveredZonesIfDirty();
        }

        private void UpdateDiscoveryFromPlayers()
        {
            UpdateVisualDiscoveryFromPlayers();
        }

        private void MarkZoneDiscovered(Zone zone)
        {
            if (zone == null || IsHiddenBiome(zone))
                return;

            string key = GetZoneKey(zone);

            if (_discoveredZones.Add(key))
            {
                _discoveryDirty = true;

                Logger.LogInfo(
                    "Island discovered: " +
                    key +
                    " (" +
                    zone.Biome +
                    ")");
            }
        }

        private void SaveDiscoveredZonesIfDirty()
        {
            if (!_discoveryDirty)
                return;

            try
            {
                string path = GetDiscoveryPath();
                string directory = Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                List<string> lines = new List<string>(_discoveredZones);
                lines.Sort(StringComparer.Ordinal);

                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                _discoveryDirty = false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to save discovered zones: " + ex.Message);
            }
        }

        private string GetDiscoveryPath()
        {
            string seed = ReadWorldSeed();

            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "Discovery",
                SafeFileName(seed) + ".txt");
        }

        private void UpdateExplorationFromPlayers()
        {
            if (_world == null || _players == null || _players.Length == 0)
                return;

            Zone[] zones = GetZones();
            if (zones == null || zones.Length == 0)
                return;

            int playerCount = Math.Min(_players.Length, 2);
            int p;

            for (p = 0; p < playerCount; p++)
            {
                Beam.Player player = _players[p];
                if (player == null)
                    continue;

                Zone zone;
                ZoneContourData contour;

                if (!TryFindZoneForWorldPosition(
                        player.transform.position,
                        zones,
                        out zone,
                        out contour))
                {
                    continue;
                }

                EnsureExplorationData(zone, contour);
                MarkExplored(contour, player.transform.position, ExplorationRadiusWorld);
            }
        }

        private bool TryFindZoneForWorldPosition(
            Vector3 worldPosition,
            Zone[] zones,
            out Zone foundZone,
            out ZoneContourData foundContour)
        {
            foundZone = null;
            foundContour = null;

            float bestDistanceSq = Single.MaxValue;
            int i;

            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];
                if (zone == null || IsHiddenBiome(zone))
                    continue;

                if (!IsZoneDiscovered(zone))
                    continue;

                ZoneContourData contour;
                if (!TryGetZoneContour(zone, out contour))
                    continue;

                const float margin = 18.0f;

                if (worldPosition.x < contour.WorldXMin - margin ||
                    worldPosition.x > contour.WorldXMax + margin ||
                    worldPosition.z < contour.WorldZMin - margin ||
                    worldPosition.z > contour.WorldZMax + margin)
                {
                    continue;
                }

                float cx = (contour.WorldXMin + contour.WorldXMax) * 0.5f;
                float cz = (contour.WorldZMin + contour.WorldZMax) * 0.5f;
                float dx = worldPosition.x - cx;
                float dz = worldPosition.z - cz;
                float distanceSq = dx * dx + dz * dz;

                if (foundZone == null || distanceSq < bestDistanceSq)
                {
                    foundZone = zone;
                    foundContour = contour;
                    bestDistanceSq = distanceSq;
                }
            }

            return foundZone != null && foundContour != null;
        }

        private void EnsureExplorationData(Zone zone, ZoneContourData contour)
        {
            if (zone == null || contour == null || contour.Texture == null)
                return;

            int width = contour.Texture.width;
            int height = contour.Texture.height;
            int total = width * height;

            if (contour.ExplorationMask != null &&
                contour.ExplorationMask.Length == total &&
                contour.ExploredTexture != null &&
                contour.ExploredOutlineTexture != null)
            {
                return;
            }

            contour.ExplorationMask = new byte[total];

            string path = GetExplorationMaskPath(zone);

            if (File.Exists(path))
            {
                Texture2D loaded = null;

                try
                {
                    byte[] bytes = File.ReadAllBytes(path);

                    loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                    if (loaded.LoadImage(bytes) &&
                        loaded.width == width &&
                        loaded.height == height)
                    {
                        Color32[] pixels = loaded.GetPixels32();
                        int i;

                        for (i = 0; i < total; i++)
                            contour.ExplorationMask[i] = pixels[i].a > 16 ? (byte)255 : (byte)0;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        "Failed to load exploration mask for " +
                        zone.Id +
                        ": " +
                        ex.Message);
                }
                finally
                {
                    if (loaded != null)
                        DestroyTexture(loaded);
                }
            }

            RebuildExploredTexture(contour);
        }

        private void MarkExplored(
            ZoneContourData contour,
            Vector3 worldPosition,
            float radiusWorld)
        {
            if (contour == null ||
                contour.Texture == null ||
                contour.ExplorationMask == null)
            {
                return;
            }

            int width = contour.Texture.width;
            int height = contour.Texture.height;

            float worldWidth = Mathf.Max(0.001f, contour.WorldXMax - contour.WorldXMin);
            float worldDepth = Mathf.Max(0.001f, contour.WorldZMax - contour.WorldZMin);

            float u = (worldPosition.x - contour.WorldXMin) / worldWidth;
            float v = (worldPosition.z - contour.WorldZMin) / worldDepth;

            if (u < -0.1f || u > 1.1f || v < -0.1f || v > 1.1f)
                return;

            int centerX = Mathf.RoundToInt(Mathf.Clamp01(u) * (width - 1));
            int centerY = Mathf.RoundToInt(Mathf.Clamp01(v) * (height - 1));

            float radiusX = Mathf.Max(1.0f, radiusWorld / worldWidth * (width - 1));
            float radiusY = Mathf.Max(1.0f, radiusWorld / worldDepth * (height - 1));

            int minX = Mathf.Max(0, Mathf.FloorToInt(centerX - radiusX));
            int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(centerX + radiusX));
            int minY = Mathf.Max(0, Mathf.FloorToInt(centerY - radiusY));
            int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(centerY + radiusY));

            Color32[] landPixels = contour.Texture.GetPixels32();
            bool changed = false;

            int y;
            int x;

            for (y = minY; y <= maxY; y++)
            {
                float ny = (y - centerY) / radiusY;

                for (x = minX; x <= maxX; x++)
                {
                    float nx = (x - centerX) / radiusX;

                    if (nx * nx + ny * ny > 1.0f)
                        continue;

                    int index = y * width + x;

                    if (landPixels[index].a <= 16)
                        continue;

                    if (contour.ExplorationMask[index] != 255)
                    {
                        contour.ExplorationMask[index] = 255;
                        changed = true;
                    }
                }
            }

            if (!changed)
                return;

            contour.ExplorationDirty = true;
            RebuildExploredTexture(contour);
        }

        private void RebuildExploredTexture(ZoneContourData contour)
        {
            if (contour == null ||
                contour.Texture == null ||
                contour.ExplorationMask == null)
            {
                return;
            }

            int width = contour.Texture.width;
            int height = contour.Texture.height;
            int total = width * height;

            Color32[] landPixels = contour.Texture.GetPixels32();
            Color32[] outPixels = new Color32[total];
            Color32[] outlinePixels = new Color32[total];

            Color32 transparent = new Color32(255, 255, 255, 0);
            Color32 visible = new Color32(255, 255, 255, 255);

            int i;
            for (i = 0; i < total; i++)
            {
                bool exploredLand =
                    landPixels[i].a > 16 &&
                    contour.ExplorationMask[i] > 16;

                outPixels[i] = exploredLand ? visible : transparent;
                outlinePixels[i] = transparent;
            }

            // Draw only the TRUE shoreline that has actually been explored.
            // Do not outline the artificial circular edge of the exploration radius.
            int y;
            int x;

            for (y = 0; y < height; y++)
            {
                for (x = 0; x < width; x++)
                {
                    int index = y * width + x;

                    if (landPixels[index].a <= 16 ||
                        contour.ExplorationMask[index] <= 16)
                    {
                        continue;
                    }

                    bool shoreline = false;
                    int oy;
                    int ox;

                    for (oy = -2; oy <= 2 && !shoreline; oy++)
                    {
                        for (ox = -2; ox <= 2; ox++)
                        {
                            if (ox == 0 && oy == 0)
                                continue;

                            int nx = x + ox;
                            int ny = y + oy;

                            if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                            {
                                shoreline = true;
                                break;
                            }

                            int neighbor = ny * width + nx;

                            if (landPixels[neighbor].a <= 16)
                            {
                                shoreline = true;
                                break;
                            }
                        }
                    }

                    if (shoreline)
                        outlinePixels[index] = visible;
                }
            }

            if (contour.ExploredTexture == null ||
                contour.ExploredTexture.width != width ||
                contour.ExploredTexture.height != height)
            {
                DestroyTexture(contour.ExploredTexture);

                contour.ExploredTexture = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    false);

                contour.ExploredTexture.wrapMode = TextureWrapMode.Clamp;
                contour.ExploredTexture.filterMode = FilterMode.Bilinear;
            }

            if (contour.ExploredOutlineTexture == null ||
                contour.ExploredOutlineTexture.width != width ||
                contour.ExploredOutlineTexture.height != height)
            {
                DestroyTexture(contour.ExploredOutlineTexture);

                contour.ExploredOutlineTexture = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    false);

                contour.ExploredOutlineTexture.wrapMode = TextureWrapMode.Clamp;
                contour.ExploredOutlineTexture.filterMode = FilterMode.Bilinear;
            }

            contour.ExploredTexture.SetPixels32(outPixels);
            contour.ExploredTexture.Apply();

            contour.ExploredOutlineTexture.SetPixels32(outlinePixels);
            contour.ExploredOutlineTexture.Apply();

            contour.ExplorationRevision++;
            DestroyTexture(contour.NorthUpExploredTexture);
            DestroyTexture(contour.NorthUpOutlineTexture);
            contour.NorthUpExploredTexture = null;
            contour.NorthUpOutlineTexture = null;
            contour.NorthUpRevision = -1;
        }

        private void SaveDirtyExplorationMasks()
        {
            foreach (KeyValuePair<string, ZoneContourData> pair in _zoneContourCache)
            {
                ZoneContourData contour = pair.Value;

                if (contour == null ||
                    !contour.ExplorationDirty ||
                    contour.ExploredTexture == null)
                {
                    continue;
                }

                try
                {
                    string directory = GetExplorationCacheDirectory();
                    Directory.CreateDirectory(directory);

                    string path = Path.Combine(
                        directory,
                        SafeFileName(pair.Key) + ".png");

                    File.WriteAllBytes(
                        path,
                        contour.ExploredTexture.EncodeToPNG());

                    contour.ExplorationDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        "Failed to save exploration mask for " +
                        pair.Key +
                        ": " +
                        ex.Message);
                }
            }
        }

        private string GetExplorationCacheDirectory()
        {
            string seed = ReadWorldSeed();

            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "ExplorationCache",
                SafeFileName(seed));
        }

        private string GetExplorationMaskPath(Zone zone)
        {
            return Path.Combine(
                GetExplorationCacheDirectory(),
                SafeFileName(GetZoneKey(zone)) + ".png");
        }

        private bool TrySaveContourToDisk(Zone zone, ZoneContourData data)
        {
            try
            {
                string dir = GetContourCacheDirectory();
                Directory.CreateDirectory(dir);

                string filePath = Path.Combine(dir, SafeFileName(GetZoneKey(zone)) + ".png");
                string metaPath = Path.Combine(dir, SafeFileName(GetZoneKey(zone)) + ".txt");

                byte[] bytes = data.Texture.EncodeToPNG();
                File.WriteAllBytes(filePath, bytes);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(data.WorldXMin.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine(data.WorldXMax.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine(data.WorldZMin.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine(data.WorldZMax.ToString("R", CultureInfo.InvariantCulture));

                File.WriteAllText(metaPath, sb.ToString(), new UTF8Encoding(false));

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to save contour cache for zone " + zone.Id + ": " + ex.Message);
                return false;
            }
        }

        private bool TryLoadContourFromDisk(Zone zone, out ZoneContourData data)
        {
            data = null;

            try
            {
                string dir = GetContourCacheDirectory();
                string key = SafeFileName(GetZoneKey(zone));
                string filePath = Path.Combine(dir, key + ".png");
                string metaPath = Path.Combine(dir, key + ".txt");

                if (!File.Exists(filePath) || !File.Exists(metaPath))
                    return false;

                string[] lines = File.ReadAllLines(metaPath);
                if (lines == null || lines.Length < 4)
                    return false;

                float worldXMin = ParseInvariantFloat(lines[0]);
                float worldXMax = ParseInvariantFloat(lines[1]);
                float worldZMin = ParseInvariantFloat(lines[2]);
                float worldZMax = ParseInvariantFloat(lines[3]);

                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes == null || bytes.Length == 0)
                    return false;

                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    return false;
                }

                data = new ZoneContourData();
                data.Texture = texture;
                data.WorldXMin = worldXMin;
                data.WorldXMax = worldXMax;
                data.WorldZMin = worldZMin;
                data.WorldZMax = worldZMax;

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to load contour cache for zone " + zone.Id + ": " + ex.Message);
                return false;
            }
        }

        private string GetContourCacheDirectory()
        {
            string seed = ReadWorldSeed();
            if (String.IsNullOrEmpty(seed))
                seed = "unknown-world";

            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "ContourCache",
                SafeFileName(seed));
        }

        private string ReadWorldSeed()
        {
            try
            {
                FieldInfo field = typeof(StrandedWorld).GetField(
                    "WORLD_SEED",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (field != null)
                {
                    object value = field.GetValue(null);
                    if (value != null)
                        return value.ToString();
                }
            }
            catch
            {
            }

            return "unknown-world";
        }

        private string GetZoneKey(Zone zone)
        {
            if (zone == null)
                return "null-zone";

            if (!String.IsNullOrEmpty(zone.Id))
                return zone.Id;

            return "instance-" + zone.GetInstanceID();
        }

        private Rect WorldAreaToMapRect(
            float worldXMin,
            float worldXMax,
            float worldZMin,
            float worldZMax,
            WorldBounds bounds,
            MapViewState view,
            Rect mapRect)
        {
            float centerX = (worldXMin + worldXMax) * 0.5f;
            float centerZ = (worldZMin + worldZMax) * 0.5f;
            float worldWidth = Mathf.Abs(worldXMax - worldXMin);
            float worldDepth = Mathf.Abs(worldZMax - worldZMin);

            Vector2 centerNormalized = WorldToNormalized(
                new Vector3(centerX, 0.0f, centerZ),
                bounds);

            Vector2 screenNormalized = ApplyView(centerNormalized, view);
            Vector2 centerPoint = NormalizedToMap(screenNormalized, mapRect);

            float boundsWidth = Mathf.Max(0.001f, bounds.MaxX - bounds.MinX);
            float boundsHeight = Mathf.Max(0.001f, bounds.MaxZ - bounds.MinZ);

            float pixelWidth =
                (worldWidth / boundsWidth) *
                mapRect.width;

            float pixelHeight =
                (worldDepth / boundsHeight) *
                mapRect.height;

            return new Rect(
                centerPoint.x - pixelWidth * 0.5f,
                centerPoint.y - pixelHeight * 0.5f,
                pixelWidth,
                pixelHeight);
        }

        private static bool RectIntersects(Rect a, Rect b)
        {
            return
                a.xMin < b.xMax &&
                a.xMax > b.xMin &&
                a.yMin < b.yMax &&
                a.yMax > b.yMin;
        }

        private Vector2 WorldToNormalized(Vector3 world, WorldBounds bounds)
        {
            Vector3 rotated = RotateWorldPointToNorthUp(world);

            float width = bounds.MaxX - bounds.MinX;
            float height = bounds.MaxZ - bounds.MinZ;

            float u = (rotated.x - bounds.MinX) / width;
            float v = (rotated.z - bounds.MinZ) / height;

            return new Vector2(u, v);
        }

        private static Vector2 ApplyView(Vector2 worldNormalized, MapViewState view)
        {
            // v0.5+: zoom and pan transform the physical parchment as one unit.
            // Map content itself remains in stable paper-local coordinates.
            return new Vector2(
                worldNormalized.x,
                1.0f - worldNormalized.y);
        }

        private static bool IsInsideMap(Vector2 normalized)
        {
            return
                normalized.x >= 0.0f &&
                normalized.x <= 1.0f &&
                normalized.y >= 0.0f &&
                normalized.y <= 1.0f;
        }

        private static Vector2 NormalizedToMap(Vector2 normalized, Rect mapRect)
        {
            return new Vector2(
                mapRect.x + normalized.x * mapRect.width,
                mapRect.y + normalized.y * mapRect.height);
        }

        private static void ClampPan(MapViewState view)
        {
            // A map opening must be able to center its player even at 1x.
            // This intentionally allows parts of the parchment to move off-screen.
            view.Pan.x = Mathf.Clamp(view.Pan.x, -0.5f, 0.5f);
            view.Pan.y = Mathf.Clamp(view.Pan.y, -0.5f, 0.5f);
        }

        private void DrawUnavailableOverlay()
        {
            float half = Screen.width * 0.5f;

            if (_views[0].Open)
            {
                GUI.color = new Color(0.03f, 0.035f, 0.04f, 0.93f);
                GUI.DrawTexture(new Rect(0.0f, 0.0f, half, Screen.height), _pixelTexture);
                GUI.color = Color.white;

                GUI.Label(
                    new Rect(20.0f, 20.0f, half - 40.0f, 60.0f),
                    "Map data is not available yet.\nLoad a world with local players.",
                    _titleStyle);
            }

            if (_views[1].Open)
            {
                GUI.color = new Color(0.03f, 0.035f, 0.04f, 0.93f);
                GUI.DrawTexture(new Rect(half, 0.0f, half, Screen.height), _pixelTexture);
                GUI.color = Color.white;

                GUI.Label(
                    new Rect(half + 20.0f, 20.0f, half - 40.0f, 60.0f),
                    "Map data is not available yet.\nLoad a world with local players.",
                    _titleStyle);
            }
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null)
                return;

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.fontSize = 18;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = new Color(0.93f, 0.94f, 0.90f, 1.0f);

            _smallStyle = new GUIStyle(GUI.skin.label);
            _smallStyle.alignment = TextAnchor.MiddleCenter;
            _smallStyle.fontSize = 13;
            _smallStyle.normal.textColor = new Color(0.78f, 0.80f, 0.77f, 1.0f);

            _compassStyle = new GUIStyle(GUI.skin.label);
            _compassStyle.alignment = TextAnchor.MiddleCenter;
            _compassStyle.fontSize = 17;
            _compassStyle.fontStyle = FontStyle.Bold;
            _compassStyle.normal.textColor = new Color(0.93f, 0.94f, 0.90f, 1.0f);

        }

        private void LoadVisualAssets()
        {
            try
            {
                string pluginDirectory =
                    Path.GetDirectoryName(typeof(SplitMapPlugin).Assembly.Location);

                if (String.IsNullOrEmpty(pluginDirectory))
                {
                    Logger.LogWarning("Could not resolve plugin directory for visual assets.");
                    return;
                }

                string assetsDirectory = Path.Combine(pluginDirectory, "assets");

                _parchmentTexture = LoadTextureFromFile(
                    Path.Combine(assetsDirectory, "parchment.png"),
                    "SplitMap_Parchment");

                _compassRoseTexture = LoadTextureFromFile(
                    Path.Combine(assetsDirectory, "compass_rose.png"),
                    "SplitMap_CompassRose");

                if (_parchmentTexture == null)
                    Logger.LogWarning("assets/parchment.png was not loaded.");

                if (_compassRoseTexture == null)
                    Logger.LogWarning("assets/compass_rose.png was not loaded.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Visual asset loading failed: " + ex.Message);
            }
        }

        private Texture2D LoadTextureFromFile(string path, string textureName)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                Texture2D texture = new Texture2D(
                    2,
                    2,
                    TextureFormat.RGBA32,
                    false);

                texture.name = textureName;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                if (!texture.LoadImage(bytes))
                {
                    DestroyTexture(texture);
                    return null;
                }

                return texture;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to load texture " +
                    path +
                    ": " +
                    ex.Message);

                return null;
            }
        }

        private void CreateTextures()
        {
            _pixelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _pixelTexture.name = "SplitMap_Pixel";
            _pixelTexture.SetPixel(0, 0, Color.white);
            _pixelTexture.Apply();

            _circleTexture = CreateCircleTexture(32);
            _circleTexture.name = "SplitMap_IslandMarker";

            _arrowTexture = CreateArrowTexture(32, 40);
            _arrowTexture.name = "SplitMap_PlayerArrow";

            _navigationArrowTexture = CreateNavigationArrowTexture(64);
            _navigationArrowTexture.name = "SplitMap_NavigationArrow";

        }

        private static Texture2D CreateCircleTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color clear = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            Color solid = Color.white;

            float center = (size - 1) * 0.5f;
            float radius = size * 0.46f;
            float feather = 1.5f;

            int y;
            int x;

            for (y = 0; y < size; y++)
            {
                for (x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    if (distance <= radius - feather)
                    {
                        texture.SetPixel(x, y, solid);
                    }
                    else if (distance <= radius)
                    {
                        float alpha = 1.0f - ((distance - (radius - feather)) / feather);
                        texture.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, alpha));
                    }
                    else
                    {
                        texture.SetPixel(x, y, clear);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        private static Texture2D CreateNavigationArrowTexture(int size)
        {
            Texture2D texture =
                new Texture2D(
                    size,
                    size,
                    TextureFormat.RGBA32,
                    false);

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color clear =
                new Color(
                    1.0f,
                    1.0f,
                    1.0f,
                    0.0f);

            Color solid = Color.white;

            int x;
            int y;

            for (y = 0; y < size; y++)
            {
                for (x = 0; x < size; x++)
                    texture.SetPixel(x, y, clear);
            }

            // A small navigation-pointer silhouette inspired by a sat-nav cursor:
            // sharp forward tip, broad shoulders, and a concave rear notch.
            // Texture2D coordinates use bottom-left origin.
            Vector2[] polygon =
            {
                new Vector2(size * 0.50f, size * 0.96f), // forward tip
                new Vector2(size * 0.91f, size * 0.13f), // lower-right tail
                new Vector2(size * 0.56f, size * 0.30f), // inner notch
                new Vector2(size * 0.09f, size * 0.13f)  // lower-left tail
            };

            for (y = 0; y < size; y++)
            {
                for (x = 0; x < size; x++)
                {
                    Vector2 point =
                        new Vector2(
                            x + 0.5f,
                            y + 0.5f);

                    if (PointInPolygon(point, polygon))
                        texture.SetPixel(x, y, solid);
                }
            }

            texture.Apply();
            return texture;
        }

        private static bool PointInPolygon(
            Vector2 point,
            Vector2[] polygon)
        {
            bool inside = false;

            int i;
            int j = polygon.Length - 1;

            for (i = 0; i < polygon.Length; i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];

                bool crosses =
                    ((pi.y > point.y) !=
                     (pj.y > point.y)) &&
                    (point.x <
                     (pj.x - pi.x) *
                     (point.y - pi.y) /
                     ((pj.y - pi.y) + 0.00001f) +
                     pi.x);

                if (crosses)
                    inside = !inside;

                j = i;
            }

            return inside;
        }

        private static Texture2D CreateArrowTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color clear = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            Color solid = Color.white;

            int y;
            int x;

            for (y = 0; y < height; y++)
            {
                for (x = 0; x < width; x++)
                    texture.SetPixel(x, y, clear);
            }

            float centerX = (width - 1) * 0.5f;
            float tipY = 1.0f;
            float shoulderY = height * 0.58f;
            float halfShoulder = width * 0.42f;
            float shaftHalf = width * 0.12f;

            for (y = 1; y < height - 1; y++)
            {
                if (y <= shoulderY)
                {
                    float t = (y - tipY) / Mathf.Max(1.0f, shoulderY - tipY);
                    float halfWidth = Mathf.Lerp(1.0f, halfShoulder, t);

                    int minX = Mathf.RoundToInt(centerX - halfWidth);
                    int maxX = Mathf.RoundToInt(centerX + halfWidth);

                    for (x = minX; x <= maxX; x++)
                    {
                        if (x >= 0 && x < width)
                            texture.SetPixel(x, y, solid);
                    }
                }
                else
                {
                    int minX = Mathf.RoundToInt(centerX - shaftHalf);
                    int maxX = Mathf.RoundToInt(centerX + shaftHalf);

                    for (x = minX; x <= maxX; x++)
                    {
                        if (x >= 0 && x < width)
                            texture.SetPixel(x, y, solid);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        private void OnDestroy()
        {
            RestoreReservedMapButtons(0);
            RestoreReservedMapButtons(1);

            SaveZoomForView(0);
            SaveZoomForView(1);
            SaveNavigationTargets();

            SaveDirtyExplorationMasks();
            SaveDiscoveredZonesIfDirty();
            SaveWreckNotes();

            DestroyWreckNotesResources();
            DestroyTexture(_pixelTexture);
            DestroyTexture(_circleTexture);
            DestroyTexture(_arrowTexture);
            DestroyTexture(_navigationArrowTexture);
            DestroyTexture(_parchmentTexture);
            DestroyTexture(_compassRoseTexture);

            foreach (KeyValuePair<string, ZoneContourData> pair in _zoneContourCache)
            {
                if (pair.Value != null)
                {
                    DestroyTexture(pair.Value.Texture);
                    DestroyTexture(pair.Value.ExploredTexture);
                    DestroyTexture(pair.Value.ExploredOutlineTexture);
                    DestroyTexture(pair.Value.NorthUpExploredTexture);
                    DestroyTexture(pair.Value.NorthUpOutlineTexture);
                }
            }

            foreach (KeyValuePair<string, Texture2D> pair in _discoveryBlobCache)
                DestroyTexture(pair.Value);

            _zoneContourCache.Clear();
            _discoveryBlobCache.Clear();
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture == null)
                return;

            try
            {
                UnityEngine.Object.Destroy(texture);
            }
            catch
            {
            }
        }

        private static float ParseInvariantFloat(string value)
        {
            return Single.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string SafeFileName(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "unnamed";

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(value.Length);

            int i;
            for (i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool bad = false;
                int j;

                for (j = 0; j < invalid.Length; j++)
                {
                    if (c == invalid[j])
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.ToString();
        }

        private enum NavigationSelectionResult
        {
            NoIsland = 0,
            Set = 1,
            Cleared = 2
        }

        private sealed class RawGamepadProfile
        {
            public string Name;

            public int ToggleMap;
            public int DPadUp;
            public int DPadRight;
            public int DPadDown;
            public int DPadLeft;
            public int ZoomOut;
            public int ZoomIn;
            public int Center;

            public static readonly RawGamepadProfile DualShock4 =
                new RawGamepadProfile
                {
                    Name = "DualShock 4 RAW",
                    ToggleMap = 6,
                    DPadUp = 12,
                    DPadRight = 13,
                    DPadDown = 14,
                    DPadLeft = 15,
                    ZoomOut = 4,
                    ZoomIn = 5,
                    Center = 0
                };

            public static readonly RawGamepadProfile XInput =
                new RawGamepadProfile
                {
                    Name = "XInput RAW",
                    ToggleMap = 6,
                    DPadUp = 11,
                    DPadRight = 12,
                    DPadDown = 13,
                    DPadLeft = 14,
                    ZoomOut = 4,
                    ZoomIn = 5,
                    Center = 0
                };
        }

        private sealed class MapViewState
        {
            public bool Open;
            public bool CenterOnOpen;
            public bool ReservedButtonsSuppressed;

            public bool CenterButtonHeld;
            public bool CenterButtonLongPressTriggered;
            public float CenterButtonHoldStart;
            public int CenterButtonClickCount;
            public float CenterButtonFirstClickAt;
            public float CenterButtonLastClickAt;

            public bool ZoomInButtonHeld;
            public bool ZoomInButtonLongPressTriggered;
            public float ZoomInButtonHoldStart;

            public bool ZoomOutButtonHeld;
            public bool ZoomOutButtonLongPressTriggered;
            public float ZoomOutButtonHoldStart;

            public string HomeFeedbackText = String.Empty;
            public float HomeFeedbackUntil;

            public float Zoom = 1.0f;
            public Vector2 Pan = Vector2.zero;

            public readonly List<ActionElementMapState> SuppressedReservedButtonMaps =
                new List<ActionElementMapState>();
        }

        private sealed class ActionElementMapState
        {
            public ActionElementMap Map;
            public bool WasEnabled;
        }

        private sealed class ZoneContourData
        {
            public Texture2D Texture;
            public Texture2D ExploredTexture;
            public Texture2D ExploredOutlineTexture;
            public Texture2D NorthUpExploredTexture;
            public Texture2D NorthUpOutlineTexture;
            public byte[] ExplorationMask;
            public bool ExplorationDirty;
            public int ExplorationRevision;
            public int NorthUpRevision = -1;
            public float NorthUpYawDegrees;
            public float NorthUpWidthScale = 1.0f;
            public float NorthUpHeightScale = 1.0f;

            public float WorldXMin;
            public float WorldXMax;
            public float WorldZMin;
            public float WorldZMax;
        }

        private struct WorldBounds
        {
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
        }
    }
}

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Bamex.StrandedDeep.ModSettings;
using UnityEngine;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        private const string ModSettingsModId = "splitmap";

        private ConfigEntry<bool> _navigationHudEnabledConfig;
        private ConfigEntry<float> _ownPlayerArrowScaleConfig;
        private ConfigEntry<bool> _manualMarkersEnabledConfig;
        private ConfigEntry<float> _markerScaleConfig;
        private ConfigEntry<int> _markerLabelModeConfig;
        private ConfigEntry<int> _crossedOutMarkerModeConfig;
        private ConfigEntry<bool> _wreckMarkersEnabledConfig;
        private ConfigEntry<bool> _partnerMarkerEnabledConfig;
        private ConfigEntry<bool> _centerOnOpenConfig;
        private ConfigEntry<bool> _fogOfWarEnabledConfig;

        private readonly Dictionary<ZoneContourData, FullContourDisplayCache>
            _fullContourDisplayCaches =
                new Dictionary<ZoneContourData, FullContourDisplayCache>();

        private void InitializeMapUserSettings()
        {
            _navigationHudEnabledConfig = Config.Bind<bool>(
                "Display",
                "NavigationHudEnabled",
                true,
                "Show the bearing-only navigation arrow while the map is closed.");

            _ownPlayerArrowScaleConfig = Config.Bind<float>(
                "Display",
                "OwnPlayerArrowScale",
                1.0f,
                "Scale of the owning player's arrow on their own map only.");

            _manualMarkersEnabledConfig = Config.Bind<bool>(
                "Markers",
                "ManualMarkersEnabled",
                true,
                "Show and interact with shared manual markers.");

            _markerScaleConfig = Config.Bind<float>(
                "Markers",
                "MarkerScale",
                1.0f,
                "Scale multiplier for manual and wreck markers; zoom-based scaling is preserved.");

            _markerLabelModeConfig = Config.Bind<int>(
                "Markers",
                "MarkerLabelMode",
                1,
                "0=Off, 1=labels from 64x, 2=labels from 32x.");

            _crossedOutMarkerModeConfig = Config.Bind<int>(
                "Markers",
                "CrossedOutMarkerMode",
                0,
                "0=Show, 1=Dim, 2=Hide crossed-out manual markers.");

            _wreckMarkersEnabledConfig = Config.Bind<bool>(
                "Markers",
                "WreckMarkersEnabled",
                true,
                "Show and interact with shared wreck markers.");

            _partnerMarkerEnabledConfig = Config.Bind<bool>(
                "Display",
                "PartnerMarkerEnabled",
                true,
                "Show the other player's arrow on your map.");

            _centerOnOpenConfig = Config.Bind<bool>(
                "MapView",
                "CenterOnOpen",
                true,
                "Center each player's map on that player when the map opens.");

            _fogOfWarEnabledConfig = Config.Bind<bool>(
                "MapView",
                "FogOfWarEnabled",
                true,
                "When disabled, reveal the whole ordinary archipelago visually without changing Discovery or Exploration data.");

            NormalizeUserSettings();
        }

        private void NormalizeUserSettings()
        {
            if (_ownPlayerArrowScaleConfig != null)
                _ownPlayerArrowScaleConfig.Value = Mathf.Clamp(_ownPlayerArrowScaleConfig.Value, 0.70f, 1.60f);

            if (_markerScaleConfig != null)
                _markerScaleConfig.Value = Mathf.Clamp(_markerScaleConfig.Value, 0.70f, 1.60f);

            if (_markerLabelModeConfig != null)
                _markerLabelModeConfig.Value = Mathf.Clamp(_markerLabelModeConfig.Value, 0, 2);

            if (_crossedOutMarkerModeConfig != null)
                _crossedOutMarkerModeConfig.Value = Mathf.Clamp(_crossedOutMarkerModeConfig.Value, 0, 2);
        }

        private void RegisterMapModSettings()
        {
            if (!ModSettingsClient.IsAvailable())
            {
                Logger.LogInfo("Mod Settings host not installed; Split Map will use its BepInEx config only.");
                return;
            }

            ModSettingsClient.RemoveMod(ModSettingsModId);

            if (!ModSettingsClient.RegisterModLocalized(ModSettingsModId, "КАРТА", "MAP", 200))
            {
                Logger.LogWarning("Failed to register Split Map in Mod Settings.");
                return;
            }

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "navigationHud",
                "Навигационная стрелка",
                "Navigation Arrow",
                100,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return IsNavigationHudEnabled(); },
                delegate(bool value) { _navigationHudEnabledConfig.Value = value; });

            ModSettingsClient.AddSliderLocalized(
                ModSettingsModId,
                "ownPlayerArrowScale",
                "Размер своей стрелки",
                "Player Arrow Size",
                200,
                0.70f,
                1.60f,
                0.10f,
                100.0f,
                "%",
                "%",
                0,
                delegate { return GetOwnPlayerArrowScale(); },
                delegate(float value) { _ownPlayerArrowScaleConfig.Value = Mathf.Clamp(value, 0.70f, 1.60f); });

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "manualMarkers",
                "Ручные отметки",
                "Manual Markers",
                300,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return AreManualMarkersEnabled(); },
                delegate(bool value) { _manualMarkersEnabledConfig.Value = value; });

            ModSettingsClient.AddSliderLocalized(
                ModSettingsModId,
                "markerScale",
                "Размер отметок",
                "Marker Size",
                400,
                0.70f,
                1.60f,
                0.10f,
                100.0f,
                "%",
                "%",
                0,
                delegate { return GetMarkerScale(); },
                delegate(float value) { _markerScaleConfig.Value = Mathf.Clamp(value, 0.70f, 1.60f); });

            ModSettingsClient.AddChoiceLocalized(
                ModSettingsModId,
                "markerLabels",
                "Подписи отметок",
                "Marker Labels",
                500,
                new string[] { "Выкл.", "С 64x", "С 32x" },
                new string[] { "Off", "From 64x", "From 32x" },
                delegate { return GetMarkerLabelMode(); },
                delegate(int value) { _markerLabelModeConfig.Value = Mathf.Clamp(value, 0, 2); });

            ModSettingsClient.AddChoiceLocalized(
                ModSettingsModId,
                "crossedOutMarkers",
                "Зачёркнутые отметки",
                "Crossed-Out Markers",
                600,
                new string[] { "Показывать", "Приглушать", "Скрывать" },
                new string[] { "Show", "Dim", "Hide" },
                delegate { return GetCrossedOutMarkerMode(); },
                delegate(int value) { _crossedOutMarkerModeConfig.Value = Mathf.Clamp(value, 0, 2); });

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "wreckMarkers",
                "Затонувшие корабли",
                "Wreck Markers",
                700,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return AreWreckMarkersEnabled(); },
                delegate(bool value) { _wreckMarkersEnabledConfig.Value = value; });

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "partnerMarker",
                "Показывать партнёра",
                "Show Partner",
                800,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return IsPartnerMarkerEnabled(); },
                delegate(bool value) { _partnerMarkerEnabledConfig.Value = value; });

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "centerOnOpen",
                "Центрировать при открытии",
                "Center on Open",
                900,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return ShouldCenterOnOpen(); },
                delegate(bool value) { _centerOnOpenConfig.Value = value; });

            ModSettingsClient.AddToggleLocalized(
                ModSettingsModId,
                "fogOfWar",
                "Туман войны",
                "Fog of War",
                1000,
                "Вкл.",
                "On",
                "Выкл.",
                "Off",
                delegate { return IsFogOfWarEnabled(); },
                delegate(bool value) { _fogOfWarEnabledConfig.Value = value; });

            ModSettingsClient.AddButtonLocalized(
                ModSettingsModId,
                "calibrateNorth",
                "Север: P1 смотрит точно на N по компасу",
                "North: P1 Faces N on the Compass",
                1100,
                "КАЛИБРОВАТЬ",
                "CALIBRATE",
                CalibrateNorthFromSettings);

            Logger.LogInfo("Split Map registered 11 controls in Mod Settings.");
        }

        private bool IsNavigationHudEnabled()
        {
            return _navigationHudEnabledConfig == null || _navigationHudEnabledConfig.Value;
        }

        private float GetOwnPlayerArrowScale()
        {
            return _ownPlayerArrowScaleConfig == null
                ? 1.0f
                : Mathf.Clamp(_ownPlayerArrowScaleConfig.Value, 0.70f, 1.60f);
        }

        private bool AreManualMarkersEnabled()
        {
            return _manualMarkersEnabledConfig == null || _manualMarkersEnabledConfig.Value;
        }

        private float GetMarkerScale()
        {
            return _markerScaleConfig == null
                ? 1.0f
                : Mathf.Clamp(_markerScaleConfig.Value, 0.70f, 1.60f);
        }

        private int GetMarkerLabelMode()
        {
            return _markerLabelModeConfig == null
                ? 1
                : Mathf.Clamp(_markerLabelModeConfig.Value, 0, 2);
        }

        private bool ShouldDrawManualMarkerLabels(float zoom)
        {
            int mode = GetMarkerLabelMode();
            if (mode <= 0)
                return false;
            return zoom >= (mode == 2 ? 32.0f : 64.0f);
        }

        private int GetCrossedOutMarkerMode()
        {
            return _crossedOutMarkerModeConfig == null
                ? 0
                : Mathf.Clamp(_crossedOutMarkerModeConfig.Value, 0, 2);
        }

        private bool ShouldHideCrossedOutMarker(bool crossedOut)
        {
            return crossedOut && GetCrossedOutMarkerMode() == 2;
        }

        private float GetCrossedOutMarkerOpacity(bool crossedOut)
        {
            return crossedOut && GetCrossedOutMarkerMode() == 1 ? 0.34f : 1.0f;
        }

        private bool AreWreckMarkersEnabled()
        {
            return _wreckMarkersEnabledConfig == null || _wreckMarkersEnabledConfig.Value;
        }

        private bool IsPartnerMarkerEnabled()
        {
            return _partnerMarkerEnabledConfig == null || _partnerMarkerEnabledConfig.Value;
        }

        private bool ShouldCenterOnOpen()
        {
            return _centerOnOpenConfig == null || _centerOnOpenConfig.Value;
        }

        private bool IsFogOfWarEnabled()
        {
            return _fogOfWarEnabledConfig == null || _fogOfWarEnabledConfig.Value;
        }

        private void CalibrateNorthFromSettings()
        {
            CalibrateNorthFromLeftPlayer();
        }

        private void GetNorthUpFullContourDisplayTextures(
            ZoneContourData contour,
            out Texture2D fillTexture,
            out Texture2D outlineTexture,
            out float widthScale,
            out float heightScale)
        {
            fillTexture = contour != null ? contour.Texture : null;
            outlineTexture = null;
            widthScale = 1.0f;
            heightScale = 1.0f;

            if (contour == null || contour.Texture == null)
                return;

            FullContourDisplayCache cache;
            if (!_fullContourDisplayCaches.TryGetValue(contour, out cache) || cache == null)
            {
                cache = new FullContourDisplayCache();
                cache.OutlineTexture = BuildFullContourOutline(contour.Texture);
                _fullContourDisplayCaches[contour] = cache;
            }

            outlineTexture = cache.OutlineTexture;

            if (!_northCalibrationValid || Mathf.Abs(_northYawDegrees) <= 0.001f)
                return;

            bool rotatedValid =
                cache.RotatedFillTexture != null &&
                cache.RotatedOutlineTexture != null &&
                Mathf.Abs(Mathf.DeltaAngle(cache.NorthYawDegrees, _northYawDegrees)) < 0.01f;

            if (!rotatedValid)
            {
                DestroyTexture(cache.RotatedFillTexture);
                DestroyTexture(cache.RotatedOutlineTexture);
                cache.RotatedFillTexture = null;
                cache.RotatedOutlineTexture = null;

                Texture2D rotatedFill;
                Texture2D rotatedOutline;
                float rotatedWidthScale;
                float rotatedHeightScale;

                if (TryRotateTexturePairForNorthUp(
                        contour.Texture,
                        cache.OutlineTexture,
                        _northYawDegrees,
                        out rotatedFill,
                        out rotatedOutline,
                        out rotatedWidthScale,
                        out rotatedHeightScale))
                {
                    cache.RotatedFillTexture = rotatedFill;
                    cache.RotatedOutlineTexture = rotatedOutline;
                    cache.WidthScale = rotatedWidthScale;
                    cache.HeightScale = rotatedHeightScale;
                    cache.NorthYawDegrees = _northYawDegrees;
                }
            }

            if (cache.RotatedFillTexture != null)
            {
                fillTexture = cache.RotatedFillTexture;
                outlineTexture = cache.RotatedOutlineTexture;
                widthScale = Mathf.Max(0.001f, cache.WidthScale);
                heightScale = Mathf.Max(0.001f, cache.HeightScale);
            }
        }

        private Texture2D BuildFullContourOutline(Texture2D source)
        {
            if (source == null)
                return null;

            int width = source.width;
            int height = source.height;
            Color32[] land = source.GetPixels32();
            Color32[] output = new Color32[land.Length];
            Color32 clear = new Color32(255, 255, 255, 0);
            Color32 visible = new Color32(255, 255, 255, 255);

            int i;
            for (i = 0; i < output.Length; i++)
                output[i] = clear;

            int y;
            int x;
            for (y = 0; y < height; y++)
            {
                for (x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (land[index].a <= 16)
                        continue;

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

                            if (land[ny * width + nx].a <= 16)
                            {
                                shoreline = true;
                                break;
                            }
                        }
                    }

                    if (shoreline)
                        output[index] = visible;
                }
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels32(output);
            texture.Apply();
            return texture;
        }

        private sealed class FullContourDisplayCache
        {
            public Texture2D OutlineTexture;
            public Texture2D RotatedFillTexture;
            public Texture2D RotatedOutlineTexture;
            public float NorthYawDegrees;
            public float WidthScale = 1.0f;
            public float HeightScale = 1.0f;
        }
    }
}

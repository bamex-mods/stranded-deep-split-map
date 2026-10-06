using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using Rewired;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        private const float WreckDetectionDistance = 260.0f;
        private const float WreckIconBaseSize = 26.0f;

        private static readonly string[] KnownWreckPrefabIds =
        {
            "ROWBOAT_3",
            "SHIPWRECK_2A",
            "SHIPWRECK_3A",
            "SHIPWRECK_4A",
            "SHIPWRECK_5A",
            "SHIPWRECK_6A",
            "SHIPWRECK_7A",
            "SHIPWRECK_8A",
            "SHIPWRECK_12A",
            "SHIPWRECK_13A",
            "PLANEWRECK_1A"
        };

        private readonly Dictionary<string, WreckNote> _wreckNotesByKey =
            new Dictionary<string, WreckNote>(StringComparer.Ordinal);

        private readonly Dictionary<string, Texture2D> _wreckIconTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private readonly PendingWreckObservation[] _pendingWrecks =
        {
            null,
            null
        };

        private readonly bool[] _pendingWreckChoiceYes =
        {
            true,
            true
        };

        private readonly WreckMarkerContextState[] _wreckMarkerContexts =
        {
            new WreckMarkerContextState(),
            new WreckMarkerContextState()
        };

        private string _wreckNotesSeed = String.Empty;
        private bool _wreckNotesLoaded;

        private GUIStyle _wreckPromptTitleStyle;
        private GUIStyle _wreckPromptChoiceStyle;
        private GUIStyle _wreckPromptSelectedChoiceStyle;
        private GUIStyle _wreckPromptHelpStyle;
        private GUIStyle _wreckImportantStyle;

        private void InitializeWreckNotes()
        {
            LoadWreckIconAssets();
        }

        private void PrepareWreckPromptBeforeOpeningMap(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _pendingWrecks.Length)
                return;

            ClearPendingWreck(viewIndex);
            EnsureWreckNotesLoadedForCurrentWorld();

            PendingWreckObservation observation;
            if (!TryFindWreckInPlayerView(viewIndex, out observation))
                return;

            if (observation == null ||
                String.IsNullOrEmpty(observation.WreckKey))
            {
                return;
            }

            WreckNote existing;
            if (_wreckNotesByKey.TryGetValue(observation.WreckKey, out existing))
            {
                // v0.10c migration: old markers used the prefab root pivot as their
                // map coordinate. For large wrecks that pivot can sit tens of metres
                // away from the visible hull. Re-observing an already marked wreck
                // silently re-anchors the saved marker to the combined visible geometry
                // center without creating a duplicate or showing the add prompt again.
                if (existing != null)
                {
                    float dx = existing.WorldPosition.x - observation.WorldPosition.x;
                    float dz = existing.WorldPosition.z - observation.WorldPosition.z;
                    float horizontalDeltaSq = dx * dx + dz * dz;

                    if (horizontalDeltaSq > 0.25f)
                    {
                        Vector3 oldPosition = existing.WorldPosition;
                        existing.WorldPosition = observation.WorldPosition;
                        existing.ZoneId = observation.ZoneId;
                        existing.InternalPrefabId = observation.InternalPrefabId;
                        existing.RotationY = observation.RotationY;
                        SaveWreckNotes();

                        Logger.LogInfo(
                            "P" +
                            (viewIndex + 1) +
                            " re-anchored wreck marker " +
                            existing.InternalPrefabId +
                            " from " +
                            oldPosition.x.ToString("0.0", CultureInfo.InvariantCulture) +
                            "," +
                            oldPosition.z.ToString("0.0", CultureInfo.InvariantCulture) +
                            " to geometry center " +
                            existing.WorldPosition.x.ToString("0.0", CultureInfo.InvariantCulture) +
                            "," +
                            existing.WorldPosition.z.ToString("0.0", CultureInfo.InvariantCulture));
                    }
                }

                return;
            }

            _pendingWrecks[viewIndex] = observation;
            _pendingWreckChoiceYes[viewIndex] = true;
        }

        private bool HasPendingWreckPrompt(int viewIndex)
        {
            return
                viewIndex >= 0 &&
                viewIndex < _pendingWrecks.Length &&
                _pendingWrecks[viewIndex] != null;
        }

        private void ClearPendingWreck(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _pendingWrecks.Length)
                return;

            _pendingWrecks[viewIndex] = null;
            _pendingWreckChoiceYes[viewIndex] = true;
        }

        private bool HandlePendingWreckGamepadInput(
            int viewIndex,
            Joystick joystick,
            RawGamepadProfile profile)
        {
            if (!HasPendingWreckPrompt(viewIndex) ||
                joystick == null ||
                profile == null)
            {
                return false;
            }

            if (GetRawButtonDown(joystick, profile.ToggleMap))
            {
                ClearPendingWreck(viewIndex);
                ToggleView(viewIndex);
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadLeft) ||
                GetRawButtonDown(joystick, profile.DPadRight))
            {
                _pendingWreckChoiceYes[viewIndex] =
                    !_pendingWreckChoiceYes[viewIndex];

                return true;
            }

            if (GetRawButtonDown(joystick, profile.Center))
            {
                if (_pendingWreckChoiceYes[viewIndex])
                    AcceptPendingWreck(viewIndex);
                else
                    ClearPendingWreck(viewIndex);

                return true;
            }

            // Freeze normal map actions while the confirmation is visible.
            return true;
        }

        private bool HandlePendingWreckKeyboardInput(int viewIndex)
        {
            if (!HasPendingWreckPrompt(viewIndex))
                return false;

            if (Input.GetKeyDown(KeyCode.LeftArrow) ||
                Input.GetKeyDown(KeyCode.RightArrow))
            {
                _pendingWreckChoiceYes[viewIndex] =
                    !_pendingWreckChoiceYes[viewIndex];
            }

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (_pendingWreckChoiceYes[viewIndex])
                    AcceptPendingWreck(viewIndex);
                else
                    ClearPendingWreck(viewIndex);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
                ClearPendingWreck(viewIndex);

            return true;
        }

        private void AcceptPendingWreck(int viewIndex)
        {
            if (!HasPendingWreckPrompt(viewIndex))
                return;

            PendingWreckObservation pending =
                _pendingWrecks[viewIndex];

            if (pending == null ||
                String.IsNullOrEmpty(pending.WreckKey))
            {
                ClearPendingWreck(viewIndex);
                return;
            }

            EnsureWreckNotesLoadedForCurrentWorld();

            if (!_wreckNotesByKey.ContainsKey(pending.WreckKey))
            {
                WreckNote note = new WreckNote();
                note.WreckKey = pending.WreckKey;
                note.ZoneId = pending.ZoneId;
                note.InternalPrefabId = pending.InternalPrefabId;
                note.WorldPosition = pending.WorldPosition;
                note.RotationY = pending.RotationY;
                note.Explored = false;
                note.Important = false;

                _wreckNotesByKey.Add(note.WreckKey, note);
                SaveWreckNotes();

                Logger.LogInfo(
                    "P" +
                    (viewIndex + 1) +
                    " added wreck marker: " +
                    note.InternalPrefabId +
                    " @ " +
                    note.WorldPosition.x.ToString("0.0", CultureInfo.InvariantCulture) +
                    ", " +
                    note.WorldPosition.z.ToString("0.0", CultureInfo.InvariantCulture));

                if (viewIndex >= 0 && viewIndex < _views.Length)
                {
                    _views[viewIndex].HomeFeedbackText = MapL("ОБЛОМКИ ДОБАВЛЕНЫ", "WRECK ADDED");
                    _views[viewIndex].HomeFeedbackUntil =
                        Time.unscaledTime + 1.6f;
                }
            }

            ClearPendingWreck(viewIndex);
        }


        private bool TryFindWreckInPlayerView(
            int viewIndex,
            out PendingWreckObservation observation)
        {
            observation = null;

            if (_players == null ||
                viewIndex < 0 ||
                viewIndex >= _players.Length ||
                _players[viewIndex] == null ||
                _players[viewIndex].PlayerCamera == null ||
                _players[viewIndex].PlayerCamera.Camera == null)
            {
                return false;
            }

            Camera camera =
                _players[viewIndex].PlayerCamera.Camera;

            // A small cone around the exact center is more forgiving than a
            // single-pixel ray, while still requiring the player to genuinely
            // aim their view at the wreck.
            Vector2[] viewportOffsets =
            {
                new Vector2(0.000f, 0.000f),
                new Vector2(-0.025f, 0.000f),
                new Vector2(0.025f, 0.000f),
                new Vector2(0.000f, -0.025f),
                new Vector2(0.000f, 0.025f),
                new Vector2(-0.018f, -0.018f),
                new Vector2(0.018f, -0.018f),
                new Vector2(-0.018f, 0.018f),
                new Vector2(0.018f, 0.018f)
            };

            PendingWreckObservation best = null;
            float bestDistance = Single.MaxValue;

            int i;
            for (i = 0; i < viewportOffsets.Length; i++)
            {
                Vector2 offset = viewportOffsets[i];

                Ray ray = camera.ViewportPointToRay(
                    new Vector3(
                        0.5f + offset.x,
                        0.5f + offset.y,
                        0.0f));

                RaycastHit hit;
                if (!Physics.Raycast(
                        ray,
                        out hit,
                        WreckDetectionDistance,
                        ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                Transform wreckRoot;
                string prefabId;

                if (!TryResolveWreckRoot(
                        hit.collider != null
                            ? hit.collider.transform
                            : null,
                        out wreckRoot,
                        out prefabId))
                {
                    continue;
                }

                if (wreckRoot == null || String.IsNullOrEmpty(prefabId))
                    continue;

                float distance = hit.distance;
                if (best != null && distance >= bestDistance)
                    continue;

                Vector3 rootPosition = wreckRoot.position;
                Vector3 markerPosition = rootPosition;

                Vector3 geometryCenter;
                if (TryGetWreckGeometryCenter(wreckRoot, out geometryCenter))
                    markerPosition = geometryCenter;

                // Identity remains anchored to the stable prefab root pivot so old
                // saves and duplicate detection remain compatible. Only the visual
                // marker coordinate uses the actual wreck geometry center.
                string zoneId = FindNearestNormalZoneKey(rootPosition);
                string key = BuildWreckKey(prefabId, zoneId, rootPosition);

                PendingWreckObservation candidate =
                    new PendingWreckObservation();

                candidate.WreckKey = key;
                candidate.ZoneId = zoneId;
                candidate.InternalPrefabId = prefabId;
                candidate.RootPosition = rootPosition;
                candidate.WorldPosition = markerPosition;
                candidate.RotationY = wreckRoot.eulerAngles.y;

                best = candidate;
                bestDistance = distance;
            }

            observation = best;
            return observation != null;
        }

        private bool TryGetWreckGeometryCenter(
            Transform wreckRoot,
            out Vector3 geometryCenter)
        {
            geometryCenter = wreckRoot != null
                ? wreckRoot.position
                : Vector3.zero;

            if (wreckRoot == null)
                return false;

            bool found = false;
            Bounds combined = new Bounds();

            try
            {
                Renderer[] renderers =
                    wreckRoot.GetComponentsInChildren<Renderer>(true);

                int i;
                for (i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                        continue;

                    // Particle/line effects can have synthetic bounds far away from
                    // the actual hull, so they must never influence the map anchor.
                    if (renderer is ParticleSystemRenderer || renderer is LineRenderer)
                        continue;

                    Bounds b = renderer.bounds;
                    Vector3 size = b.size;

                    if (Single.IsNaN(b.center.x) ||
                        Single.IsNaN(b.center.y) ||
                        Single.IsNaN(b.center.z) ||
                        Single.IsNaN(size.x) ||
                        Single.IsNaN(size.y) ||
                        Single.IsNaN(size.z))
                    {
                        continue;
                    }

                    // Ignore obviously synthetic/global render bounds while keeping
                    // every realistic Stranded Deep wreck, including cargo hulls.
                    if (size.x > 250.0f || size.y > 250.0f || size.z > 250.0f)
                        continue;

                    if (!found)
                    {
                        combined = b;
                        found = true;
                    }
                    else
                    {
                        combined.Encapsulate(b);
                    }
                }
            }
            catch
            {
                found = false;
            }

            if (!found)
            {
                try
                {
                    Collider[] colliders =
                        wreckRoot.GetComponentsInChildren<Collider>(true);

                    int i;
                    for (i = 0; i < colliders.Length; i++)
                    {
                        Collider collider = colliders[i];
                        if (collider == null || collider.isTrigger)
                            continue;

                        Bounds b = collider.bounds;
                        Vector3 size = b.size;

                        if (size.x > 250.0f || size.y > 250.0f || size.z > 250.0f)
                            continue;

                        if (!found)
                        {
                            combined = b;
                            found = true;
                        }
                        else
                        {
                            combined.Encapsulate(b);
                        }
                    }
                }
                catch
                {
                    found = false;
                }
            }

            if (!found)
                return false;

            geometryCenter = combined.center;
            return true;
        }

        private bool TryResolveWreckRoot(
            Transform hitTransform,
            out Transform wreckRoot,
            out string prefabId)
        {
            wreckRoot = null;
            prefabId = String.Empty;

            Transform current = hitTransform;
            int depth = 0;

            while (current != null && depth < 24)
            {
                string matched = MatchKnownWreckPrefabId(current.name);

                if (!String.IsNullOrEmpty(matched))
                {
                    wreckRoot = current;
                    prefabId = matched;
                    return true;
                }

                current = current.parent;
                depth++;
            }

            return false;
        }

        private static string MatchKnownWreckPrefabId(string objectName)
        {
            if (String.IsNullOrEmpty(objectName))
                return String.Empty;

            int i;
            for (i = 0; i < KnownWreckPrefabIds.Length; i++)
            {
                string prefabId = KnownWreckPrefabIds[i];

                if (objectName.IndexOf(
                        prefabId,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return prefabId;
                }
            }

            return String.Empty;
        }

        private string FindNearestNormalZoneKey(Vector3 worldPosition)
        {
            Zone[] zones = GetZones();

            if (zones == null || zones.Length == 0)
                return String.Empty;

            Zone best = null;
            float bestDistanceSq = Single.MaxValue;

            int i;
            for (i = 0; i < zones.Length; i++)
            {
                Zone zone = zones[i];

                if (zone == null || IsHiddenBiome(zone))
                    continue;

                Vector3 delta =
                    zone.transform.position - worldPosition;

                delta.y = 0.0f;

                float distanceSq = delta.sqrMagnitude;
                if (best == null || distanceSq < bestDistanceSq)
                {
                    best = zone;
                    bestDistanceSq = distanceSq;
                }
            }

            return best != null
                ? GetZoneKey(best)
                : String.Empty;
        }

        private static string BuildWreckKey(
            string prefabId,
            string zoneId,
            Vector3 worldPosition)
        {
            // Quantize to half-meter units. This stays stable when the same
            // procedural object is reconstructed, but still distinguishes two
            // same-prefab wrecks located close to each other.
            int qx = Mathf.RoundToInt(worldPosition.x * 2.0f);
            int qy = Mathf.RoundToInt(worldPosition.y * 2.0f);
            int qz = Mathf.RoundToInt(worldPosition.z * 2.0f);

            return
                (prefabId ?? String.Empty) + "@" +
                (zoneId ?? String.Empty) + "@" +
                qx.ToString(CultureInfo.InvariantCulture) + "@" +
                qy.ToString(CultureInfo.InvariantCulture) + "@" +
                qz.ToString(CultureInfo.InvariantCulture);
        }

        private bool HasOpenWreckMarkerContext(int viewIndex)
        {
            return
                viewIndex >= 0 &&
                viewIndex < _wreckMarkerContexts.Length &&
                _wreckMarkerContexts[viewIndex] != null &&
                _wreckMarkerContexts[viewIndex].Open;
        }

        private void CloseWreckMarkerContext(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= _wreckMarkerContexts.Length)
                return;

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];
            context.Open = false;
            context.WreckKey = String.Empty;
            context.SelectedIndex = 0;
        }

        private bool TryOpenWreckMarkerContextAtMapCenter(int viewIndex)
        {
            if (!AreWreckMarkersEnabled())
                return false;

            if (viewIndex < 0 || viewIndex >= _wreckMarkerContexts.Length)
                return false;

            EnsureWreckNotesLoadedForCurrentWorld();

            WreckNote note = FindWreckMarkerAtMapCenter(viewIndex);
            if (note == null || String.IsNullOrEmpty(note.WreckKey))
                return false;

            CloseManualMarkerMenu(viewIndex);
            CloseManualMarkerContext(viewIndex);

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];
            context.Open = true;
            context.WreckKey = note.WreckKey;
            context.SelectedIndex = 0;
            return true;
        }

        private WreckNote FindWreckMarkerAtMapCenter(int viewIndex)
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

            float wreckSize = GetWreckMarkerSize(view.Zoom);

            float selectionRadiusPx = Mathf.Max(32.0f, wreckSize * 0.82f);
            float bestDistanceSq = selectionRadiusPx * selectionRadiusPx;
            WreckNote best = null;

            foreach (KeyValuePair<string, WreckNote> pair in _wreckNotesByKey)
            {
                WreckNote note = pair.Value;
                if (note == null)
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

        private bool HandleWreckMarkerContextGamepadInput(
            int viewIndex,
            Joystick joystick,
            RawGamepadProfile profile)
        {
            if (!HasOpenWreckMarkerContext(viewIndex) || joystick == null || profile == null)
                return false;

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];

            if (GetRawButtonDown(joystick, profile.ToggleMap) ||
                GetRawButtonDown(joystick, profile.DPadLeft))
            {
                CloseWreckMarkerContext(viewIndex);
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadUp))
            {
                context.SelectedIndex = (context.SelectedIndex + 3) % 4;
                return true;
            }

            if (GetRawButtonDown(joystick, profile.DPadDown))
            {
                context.SelectedIndex = (context.SelectedIndex + 1) % 4;
                return true;
            }

            if (GetRawButtonDown(joystick, profile.Center) ||
                GetRawButtonDown(joystick, profile.DPadRight))
            {
                ActivateWreckMarkerContextSelection(viewIndex);
                return true;
            }

            return true;
        }

        private bool HandleWreckMarkerContextKeyboardInput(int viewIndex)
        {
            if (!HasOpenWreckMarkerContext(viewIndex))
                return false;

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];

            if (Input.GetKeyDown(KeyCode.UpArrow))
                context.SelectedIndex = (context.SelectedIndex + 3) % 4;

            if (Input.GetKeyDown(KeyCode.DownArrow))
                context.SelectedIndex = (context.SelectedIndex + 1) % 4;

            if (Input.GetKeyDown(KeyCode.Escape) ||
                Input.GetKeyDown(KeyCode.Backspace) ||
                Input.GetKeyDown(KeyCode.LeftArrow))
            {
                CloseWreckMarkerContext(viewIndex);
                return true;
            }

            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter) ||
                Input.GetKeyDown(KeyCode.RightArrow))
            {
                ActivateWreckMarkerContextSelection(viewIndex);
                return true;
            }

            return true;
        }

        private void ActivateWreckMarkerContextSelection(int viewIndex)
        {
            if (!HasOpenWreckMarkerContext(viewIndex))
                return;

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];
            WreckNote note;
            if (String.IsNullOrEmpty(context.WreckKey) ||
                !_wreckNotesByKey.TryGetValue(context.WreckKey, out note) ||
                note == null)
            {
                CloseWreckMarkerContext(viewIndex);
                return;
            }

            if (context.SelectedIndex == 0)
            {
                note.Explored = !note.Explored;
                SaveWreckNotes();
                _views[viewIndex].HomeFeedbackText =
                    note.Explored ? MapL("ОТМЕЧЕНО КАК ИССЛЕДОВАННОЕ", "WRECK EXPLORED") : MapL("ОТМЕТКА ИССЛЕДОВАНИЯ СНЯТА", "WRECK RESTORED");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }
            else if (context.SelectedIndex == 1)
            {
                note.Important = !note.Important;
                SaveWreckNotes();
                _views[viewIndex].HomeFeedbackText =
                    note.Important ? MapL("ОТМЕЧЕНО КАК ВАЖНОЕ", "WRECK IMPORTANT") : MapL("ОТМЕТКА «ВАЖНО» СНЯТА", "IMPORTANT OFF");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }
            else if (context.SelectedIndex == 2)
            {
                _wreckNotesByKey.Remove(note.WreckKey);
                SaveWreckNotes();
                _views[viewIndex].HomeFeedbackText = MapL("ОБЛОМКИ УДАЛЕНЫ", "WRECK REMOVED");
                _views[viewIndex].HomeFeedbackUntil = Time.unscaledTime + 1.5f;
            }

            CloseWreckMarkerContext(viewIndex);
        }

        private void DrawWreckMarkerContextMenu(int viewIndex, Rect localViewport)
        {
            if (!HasOpenWreckMarkerContext(viewIndex))
                return;

            EnsureManualMenuStyles();

            WreckMarkerContextState context = _wreckMarkerContexts[viewIndex];
            WreckNote note;
            if (String.IsNullOrEmpty(context.WreckKey) ||
                !_wreckNotesByKey.TryGetValue(context.WreckKey, out note) ||
                note == null)
            {
                CloseWreckMarkerContext(viewIndex);
                return;
            }

            GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.28f);
            GUI.DrawTexture(localViewport, _pixelTexture);
            GUI.color = Color.white;

            float width = 450.0f;
            float height = 282.0f;
            Rect panel = new Rect(
                localViewport.center.x - width * 0.5f,
                localViewport.center.y - height * 0.5f,
                width,
                height);

            GUI.color = new Color(0.10f, 0.045f, 0.015f, 0.98f);
            GUI.DrawTexture(panel, _pixelTexture);
            GUI.color = new Color(0.91f, 0.80f, 0.61f, 0.99f);
            GUI.DrawTexture(
                new Rect(panel.x + 3.0f, panel.y + 3.0f, panel.width - 6.0f, panel.height - 6.0f),
                _pixelTexture);
            GUI.color = Color.white;

            string title = MapL("Затонувший корабль", "Shipwreck");
            if (!String.IsNullOrEmpty(note.InternalPrefabId))
                title += "  [" + note.InternalPrefabId + "]";

            GUI.Label(
                new Rect(panel.x + 18.0f, panel.y + 12.0f, panel.width - 36.0f, 34.0f),
                title,
                _manualMenuTitleStyle);

            string[] options = new string[]
            {
                note.Explored ? MapL("Снять отметку «Исследовано»", "Clear Explored") : MapL("Отметить исследованным", "Mark as Explored"),
                note.Important ? MapL("Снять «Важно»", "Clear Important") : MapL("Отметить важным", "Mark as Important"),
                MapL("Удалить с карты", "Remove from Map"),
                MapL("Отмена", "Cancel")
            };

            float rowY = panel.y + 62.0f;
            float rowHeight = 46.0f;
            int i;
            for (i = 0; i < options.Length; i++)
            {
                Rect row = new Rect(
                    panel.x + 22.0f,
                    rowY + i * rowHeight,
                    panel.width - 44.0f,
                    40.0f);

                bool selected = i == context.SelectedIndex;
                if (selected)
                {
                    GUI.color = new Color(0.28f, 0.13f, 0.035f, 0.98f);
                    GUI.DrawTexture(row, _pixelTexture);
                    GUI.color = Color.white;
                }

                GUI.Label(
                    row,
                    options[i],
                    selected ? _manualMenuSelectedStyle : _manualMenuItemStyle);
            }
        }

        private void EnsureWreckNotesLoadedForCurrentWorld()
        {
            string seed = ReadWorldSeed();

            if (_wreckNotesLoaded &&
                String.Equals(
                    _wreckNotesSeed,
                    seed,
                    StringComparison.Ordinal))
            {
                return;
            }

            _wreckNotesByKey.Clear();
            _wreckNotesSeed = seed;
            _wreckNotesLoaded = true;

            ClearPendingWreck(0);
            ClearPendingWreck(1);
            CloseWreckMarkerContext(0);
            CloseWreckMarkerContext(1);

            string path = GetWreckNotesPath(seed);
            if (!File.Exists(path))
                return;

            try
            {
                string[] lines = File.ReadAllLines(path);

                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    if (String.IsNullOrWhiteSpace(line) ||
                        line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] parts = line.Split('\t');
                    if (parts.Length < 10 ||
                        !String.Equals(parts[0], "WRECK", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    WreckNote note = new WreckNote();
                    note.WreckKey = parts[1];
                    note.InternalPrefabId = parts[2];
                    note.ZoneId = parts[3];
                    note.WorldPosition = new Vector3(
                        ParseInvariantFloat(parts[4]),
                        ParseInvariantFloat(parts[5]),
                        ParseInvariantFloat(parts[6]));
                    note.RotationY = ParseInvariantFloat(parts[7]);
                    note.Explored = ParseInvariantBool(parts[8]);
                    note.Important = ParseInvariantBool(parts[9]);

                    if (String.IsNullOrEmpty(note.WreckKey))
                        continue;

                    _wreckNotesByKey[note.WreckKey] = note;
                }

                Logger.LogInfo(
                    "Loaded " +
                    _wreckNotesByKey.Count +
                    " wreck marker(s) for world " +
                    seed +
                    ".");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to load wreck markers: " +
                    ex.Message);
            }
        }

        private void SaveWreckNotes()
        {
            if (!_wreckNotesLoaded)
                return;

            string path = GetWreckNotesPath(_wreckNotesSeed);

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                List<string> lines = new List<string>();
                lines.Add("# Stranded Deep Split Map wreck markers v1");
                lines.Add("# type\tkey\tprefab\tzone\tx\ty\tz\trotationY\texplored\timportant");

                List<WreckNote> notes =
                    new List<WreckNote>(_wreckNotesByKey.Values);

                notes.Sort(delegate(WreckNote a, WreckNote b)
                {
                    return String.Compare(
                        a != null ? a.WreckKey : String.Empty,
                        b != null ? b.WreckKey : String.Empty,
                        StringComparison.Ordinal);
                });

                int i;
                for (i = 0; i < notes.Count; i++)
                {
                    WreckNote note = notes[i];
                    if (note == null || String.IsNullOrEmpty(note.WreckKey))
                        continue;

                    lines.Add(
                        "WRECK\t" +
                        SanitizeWreckField(note.WreckKey) + "\t" +
                        SanitizeWreckField(note.InternalPrefabId) + "\t" +
                        SanitizeWreckField(note.ZoneId) + "\t" +
                        note.WorldPosition.x.ToString("R", CultureInfo.InvariantCulture) + "\t" +
                        note.WorldPosition.y.ToString("R", CultureInfo.InvariantCulture) + "\t" +
                        note.WorldPosition.z.ToString("R", CultureInfo.InvariantCulture) + "\t" +
                        note.RotationY.ToString("R", CultureInfo.InvariantCulture) + "\t" +
                        (note.Explored ? "1" : "0") + "\t" +
                        (note.Important ? "1" : "0"));
                }

                string tempPath = path + ".tmp";
                File.WriteAllLines(tempPath, lines.ToArray());
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Failed to save wreck markers: " +
                    ex.Message);
            }
        }

        private string GetWreckNotesPath(string seed)
        {
            return Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepMap",
                "Wrecks",
                SafeFileName(seed) + ".txt");
        }

        private static string SanitizeWreckField(string value)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;

            return value
                .Replace("\t", " ")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private static bool ParseInvariantBool(string value)
        {
            return
                String.Equals(value, "1", StringComparison.Ordinal) ||
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private float GetWreckMarkerSize(float zoom)
        {
            float zoomSize = Mathf.Clamp(
                WreckIconBaseSize *
                Mathf.Pow(Mathf.Max(1.0f, zoom), 0.25f),
                WreckIconBaseSize,
                68.0f);

            return zoomSize * GetMarkerScale();
        }

        private void DrawWreckMarkers(
            Rect mapRect,
            WorldBounds bounds,
            MapViewState view,
            int viewIndex)
        {
            if (view == null || _wreckNotesByKey.Count == 0)
                return;

            EnsureWreckNotesLoadedForCurrentWorld();

            foreach (KeyValuePair<string, WreckNote> pair in _wreckNotesByKey)
            {
                WreckNote note = pair.Value;
                if (note == null)
                    continue;

                Vector2 normalized =
                    WorldToNormalized(note.WorldPosition, bounds);

                if (!IsInsideMap(normalized))
                    continue;

                Vector2 screenNormalized =
                    ApplyView(normalized, view);

                Vector2 point =
                    NormalizedToMap(screenNormalized, mapRect);

                float size = GetWreckMarkerSize(view.Zoom);

                Texture2D texture;
                _wreckIconTextures.TryGetValue(
                    note.InternalPrefabId ?? String.Empty,
                    out texture);

                Rect iconRect =
                    new Rect(
                        point.x - size * 0.5f,
                        point.y - size * 0.5f,
                        size,
                        size);

                GUI.color = new Color(1.0f, 1.0f, 1.0f, 0.96f);

                if (texture != null)
                {
                    GUI.DrawTexture(
                        iconRect,
                        texture,
                        ScaleMode.ScaleToFit,
                        true);
                }
                else if (_circleTexture != null)
                {
                    GUI.color =
                        new Color(
                            0.27f,
                            0.13f,
                            0.045f,
                            0.88f);

                    GUI.DrawTexture(iconRect, _circleTexture);
                }

                GUI.color = Color.white;

                if (note.Explored)
                    DrawManualMarkerCross(point, size * 0.90f);

                if (note.Important)
                    DrawWreckImportantMark(point, size);

            }
        }

        private void DrawWreckImportantMark(Vector2 point, float iconSize)
        {
            if (_wreckImportantStyle == null)
            {
                _wreckImportantStyle = new GUIStyle(GUI.skin.label);
                _wreckImportantStyle.alignment = TextAnchor.MiddleCenter;
                _wreckImportantStyle.fontSize = 24;
                _wreckImportantStyle.fontStyle = FontStyle.Bold;
                _wreckImportantStyle.normal.textColor =
                    new Color(0.34f, 0.11f, 0.025f, 1.0f);
            }

            float badgeSize = Mathf.Clamp(iconSize * 0.62f, 22.0f, 34.0f);
            Rect badge = new Rect(
                point.x + iconSize * 0.28f,
                point.y - iconSize * 0.58f,
                badgeSize,
                badgeSize);

            Color old = GUI.color;
            GUI.color = new Color(0.93f, 0.82f, 0.62f, 0.96f);
            GUI.DrawTexture(badge, _pixelTexture);
            GUI.color = Color.white;
            GUI.Label(badge, "!", _wreckImportantStyle);
            GUI.color = old;
        }

        private void DrawWreckPrompt(
            int viewIndex,
            Rect localViewport)
        {
            if (!HasPendingWreckPrompt(viewIndex))
                return;

            EnsureWreckPromptStyles();

            // Dim the local player's map behind the modal.
            GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.34f);
            GUI.DrawTexture(localViewport, _pixelTexture);

            float width =
                Mathf.Min(
                    720.0f,
                    localViewport.width * 0.90f);

            float height = 248.0f;

            Rect box =
                new Rect(
                    localViewport.center.x - width * 0.5f,
                    localViewport.center.y - height * 0.5f,
                    width,
                    height);

            // High-contrast dark frame + warm parchment panel.
            GUI.color = new Color(0.10f, 0.045f, 0.012f, 0.99f);
            GUI.DrawTexture(box, _pixelTexture);

            Rect inner = new Rect(
                box.x + 5.0f,
                box.y + 5.0f,
                box.width - 10.0f,
                box.height - 10.0f);

            GUI.color = new Color(0.93f, 0.82f, 0.62f, 1.0f);
            GUI.DrawTexture(inner, _pixelTexture);
            GUI.color = Color.white;

            GUI.Label(
                new Rect(
                    box.x + 24.0f,
                    box.y + 22.0f,
                    box.width - 48.0f,
                    56.0f),
                MapL("Нанести затонувший корабль на карту?", "Add this wreck to the map?"),
                _wreckPromptTitleStyle);

            bool yes = _pendingWreckChoiceYes[viewIndex];

            float buttonWidth = Mathf.Min(190.0f, (box.width - 92.0f) * 0.5f);
            float buttonHeight = 56.0f;
            float gap = 24.0f;
            float totalWidth = buttonWidth * 2.0f + gap;
            float startX = box.center.x - totalWidth * 0.5f;
            float buttonY = box.y + 100.0f;

            DrawWreckPromptButton(
                new Rect(startX, buttonY, buttonWidth, buttonHeight),
                MapL("ДА", "YES"),
                yes);

            DrawWreckPromptButton(
                new Rect(startX + buttonWidth + gap, buttonY, buttonWidth, buttonHeight),
                MapL("НЕТ", "NO"),
                !yes);

            GUI.Label(
                new Rect(
                    box.x + 28.0f,
                    box.y + 174.0f,
                    box.width - 56.0f,
                    48.0f),
                MapL("D-pad ←/→ — выбор     A / Cross — подтвердить", "D-pad ←/→ — select     A / Cross — confirm"),
                _wreckPromptHelpStyle);

            GUI.color = Color.white;
        }

        private void DrawWreckPromptButton(
            Rect rect,
            string text,
            bool selected)
        {
            if (selected)
            {
                GUI.color = new Color(0.24f, 0.10f, 0.022f, 1.0f);
                GUI.DrawTexture(rect, _pixelTexture);
                GUI.color = Color.white;
                GUI.Label(rect, text, _wreckPromptSelectedChoiceStyle);
                return;
            }

            GUI.color = new Color(0.20f, 0.085f, 0.018f, 0.96f);
            GUI.DrawTexture(rect, _pixelTexture);

            Rect inner = new Rect(
                rect.x + 3.0f,
                rect.y + 3.0f,
                rect.width - 6.0f,
                rect.height - 6.0f);

            GUI.color = new Color(0.96f, 0.87f, 0.70f, 1.0f);
            GUI.DrawTexture(inner, _pixelTexture);
            GUI.color = Color.white;
            GUI.Label(rect, text, _wreckPromptChoiceStyle);
        }

        private void EnsureWreckPromptStyles()
        {
            if (_wreckPromptTitleStyle != null)
                return;

            _wreckPromptTitleStyle =
                new GUIStyle(GUI.skin.label);

            _wreckPromptTitleStyle.alignment =
                TextAnchor.MiddleCenter;
            _wreckPromptTitleStyle.fontSize = 25;
            _wreckPromptTitleStyle.fontStyle = FontStyle.Bold;
            _wreckPromptTitleStyle.normal.textColor =
                new Color(0.14f, 0.055f, 0.015f, 1.0f);
            _wreckPromptTitleStyle.wordWrap = true;

            _wreckPromptChoiceStyle =
                new GUIStyle(GUI.skin.label);

            _wreckPromptChoiceStyle.alignment =
                TextAnchor.MiddleCenter;
            _wreckPromptChoiceStyle.fontSize = 23;
            _wreckPromptChoiceStyle.fontStyle = FontStyle.Bold;
            _wreckPromptChoiceStyle.normal.textColor =
                new Color(0.14f, 0.055f, 0.015f, 1.0f);

            _wreckPromptSelectedChoiceStyle =
                new GUIStyle(_wreckPromptChoiceStyle);
            _wreckPromptSelectedChoiceStyle.normal.textColor =
                new Color(1.0f, 0.95f, 0.82f, 1.0f);

            _wreckPromptHelpStyle =
                new GUIStyle(GUI.skin.label);

            _wreckPromptHelpStyle.alignment =
                TextAnchor.MiddleCenter;
            _wreckPromptHelpStyle.fontSize = 15;
            _wreckPromptHelpStyle.fontStyle = FontStyle.Bold;
            _wreckPromptHelpStyle.normal.textColor =
                new Color(0.20f, 0.085f, 0.02f, 1.0f);
            _wreckPromptHelpStyle.wordWrap = true;
        }

        private void LoadWreckIconAssets()
        {
            try
            {
                string pluginDirectory =
                    Path.GetDirectoryName(
                        typeof(SplitMapPlugin).Assembly.Location);

                if (String.IsNullOrEmpty(pluginDirectory))
                    return;

                string wreckDirectory =
                    Path.Combine(
                        pluginDirectory,
                        "assets",
                        "wrecks");

                int i;
                for (i = 0; i < KnownWreckPrefabIds.Length; i++)
                {
                    string prefabId = KnownWreckPrefabIds[i];
                    string path =
                        Path.Combine(
                            wreckDirectory,
                            prefabId + ".png");

                    Texture2D texture =
                        LoadTextureFromFile(
                            path,
                            "SplitMap_Wreck_" + prefabId);

                    if (texture != null)
                    {
                        _wreckIconTextures[prefabId] = texture;
                    }
                    else
                    {
                        Logger.LogWarning(
                            "Wreck icon was not loaded: " +
                            path);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Wreck icon loading failed: " +
                    ex.Message);
            }
        }

        private void DestroyWreckNotesResources()
        {
            foreach (KeyValuePair<string, Texture2D> pair in _wreckIconTextures)
                DestroyTexture(pair.Value);

            _wreckIconTextures.Clear();
        }

        private sealed class PendingWreckObservation
        {
            public string WreckKey = String.Empty;
            public string ZoneId = String.Empty;
            public string InternalPrefabId = String.Empty;
            public Vector3 RootPosition;
            public Vector3 WorldPosition;
            public float RotationY;
        }

        private sealed class WreckMarkerContextState
        {
            public bool Open;
            public string WreckKey = String.Empty;
            public int SelectedIndex;
        }

        private sealed class WreckNote
        {
            public string WreckKey = String.Empty;
            public string ZoneId = String.Empty;
            public string InternalPrefabId = String.Empty;
            public Vector3 WorldPosition;
            public float RotationY;
            public bool Explored;
            public bool Important;
        }
    }
}

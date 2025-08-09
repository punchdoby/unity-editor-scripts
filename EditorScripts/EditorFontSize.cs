#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using UnityEditor;
using UnityEngine;

/*
EditorFontSize.cs
Last changed on Unity 6 (6000.0.34f1) Silicon LTS

Allows you to change the font sizes inside the Unity editor using a global zoom, item-specific overrides or both.
Everything will be under Window > Editor Font Size > Open Window. Changes are reflected and snapshotted instantly.

DISCLAIMER: DEFAULTS.FONTSIZE.JSON IS THE SINGLE SOURCE OF TRUTH FOR BASELINE SIZES!
If it's somehow wrong:
- Close Unity
- Delete ProjectName/UserSettings/EditorFontSize/defaults.fontsize.json
- Reopen Unity (it will regenerate correctly)

The options under Snapshot are mostly meant as a debugging tool. Please don't use them unless you know what you're doing :)

Inspired by the original script from Nukadelic: https://gist.github.com/nukadelic/47474c7e5d4ee5909462e3b900f4cb82

Key differences:
- Global zoom persistence (EditorPrefs)
- Per-style persistence via two JSON snapshots (UserSettings/EditorFontSize/)
- ...and rewrote pretty much the whole thing, lol

If you wish to use your custom settings in a different project, just copy your current.fontsize.json over
*/
namespace EditorScripts
{
    public sealed class EditorFontSize : EditorWindow
    {
        // ---------------------- Config ----------------------
        private const string PrefKeyGlobalDelta = "EditorFontSize.GlobalDelta";
        private const int MinFont = 8;
        private const int MaxFont = 24;
        private const int DefaultFontSize = 11;

        private const int ButtonWidth = 22;
        private const int ButtonMidWidth = 34;

        private static readonly bool ApplyOnLaunch = true; // apply on domain reload/start, no need to turn this off, only here for debugging :)

        // Snapshots on disk (per-user, per-project)
        [Serializable] private class StyleEntry { public string name; public int size; }
        [Serializable] private class StyleSnapshot { public int version = 1; public List<StyleEntry> entries = new(); }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string SettingsDir => Path.Combine(ProjectRoot, "UserSettings", "EditorFontSize");
        private static string DefaultsPath => Path.Combine(SettingsDir, "defaults.fontsize.json");
        private static string CurrentPath  => Path.Combine(SettingsDir, "current.fontsize.json");

        // ---------------------- State -----------------------
        private Vector2 _scroll;
        private readonly Dictionary<string, bool> _foldouts = new();
        private GUIStyle _evenBg, _oddBg;
        private int _rowCount;

        // Reflection caches
        private PropertyInfo[] _editorStyleProps;
        private PropertyInfo[] _guiSkinProps;

        // Deferred init (runs inside OnGUI to access GUI.skin safely)
        private Action _initCallback;

        // Snapshots in memory
        private Dictionary<string,int> _defaultsMap; // style -> baseline size
        private Dictionary<string,int> _currentMap;  // style -> override size (diff vs defaults)

        // Debounced saving flag
        private bool _dirtyCurrentPendingSave;

        // ---------------------- Startup ---------------------
        [InitializeOnLoadMethod]
        private static void ApplyPersistedOnLaunch()
        {
            if (!ApplyOnLaunch) return;

            // Create invisible utility window; run _initCallback in its first OnGUI
            var temp = CreateInstance<EditorFontSize>();
            temp._initCallback = () =>
            {
                try
                {
                    temp.GrabProperties();

                    // 1) Ensure defaults snapshot exists
                    temp._defaultsMap = SnapshotFromJson(DefaultsPath);
                    if (temp._defaultsMap.Count == 0)
                    {
                        temp._defaultsMap = temp.CaptureAllCurrentSizes();
                        SaveSnapshot(temp._defaultsMap, DefaultsPath);
                    }

                    // 2) Load current (may be legacy full snapshot); ensure overrides-only
                    var loadedCurrent = SnapshotFromJson(CurrentPath);
                    Dictionary<string,int> overridesOnly = temp.ComputeOverrides(loadedCurrent, temp._defaultsMap);

                    // 3) If file was missing, or it contained full map == defaults, start with empty overrides
                    if (loadedCurrent.Count == 0 || overridesOnly.Count != loadedCurrent.Count)
                    {
                        SaveSnapshot(overridesOnly, CurrentPath); // migrate to overrides-only
                    }
                    temp._currentMap = overridesOnly;

                    // 4) Apply global delta baseline
                    var delta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
                    if (delta != 0) temp.ApplyDeltaToAllStyles(delta);

                    // 5) Apply overrides (absolute sizes for changed styles only)
                    temp.ApplyOverrides(temp._currentMap);

                    RepaintAll();
                }
                catch { /* non-fatal */ }
                finally
                {
                    temp.Close();
                }
            };

            temp.ShowUtility();
            // Park off-screen and tiny so the window never flashes
            temp.position = new Rect(-10000, -10000, 1, 1);
        }

        
        // ---------------------- Menu ------------------------
        [MenuItem("Window/Editor Font Size/Open Window")]
        private static void Open()
        {
            var w = GetWindow<EditorFontSize>("Editor Font Size");
            w.minSize = new Vector2(220, 36);
        }

        
        [MenuItem("Window/Editor Font Size/Reset Global Zoom")]
        private static void ResetGlobalZoomMenu()
        {
            var delta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
            if (delta == 0)
            {
                RepaintAll();
                return;
            }

            var temp = CreateInstance<EditorFontSize>();
            temp._initCallback = () =>
            {
                try
                {
                    temp.GrabProperties();
                    temp.ApplyDeltaToAllStyles(-delta);
                    EditorPrefs.DeleteKey(PrefKeyGlobalDelta);
                    RepaintAll();
                }
                finally { temp.Close(); }
            };
            temp.ShowUtility();
            temp.position = new Rect(-10000, -10000, 1, 1);
        }

        
        [MenuItem("Window/Editor Font Size/Snapshots/Rebuild Defaults From Current Editor")]
        private static void RebuildDefaults()
        {
            var temp = CreateInstance<EditorFontSize>();
            temp._initCallback = () =>
            {
                try
                {
                    temp.GrabProperties();
                    var snap = temp.CaptureAllCurrentSizes();
                    SaveSnapshot(snap, DefaultsPath);
                }
                finally { temp.Close(); }
            };
            temp.ShowUtility();
            temp.position = new Rect(-10000, -10000, 1, 1);
        }

        
        [MenuItem("Window/Editor Font Size/Snapshots/Reset Current To Defaults")]
        private static void ResetCurrentToDefaults()
        {
            var temp = CreateInstance<EditorFontSize>();
            temp._initCallback = () =>
            {
                try
                {
                    temp.GrabProperties();

                    // Load defaults (or capture if missing)
                    var defs = SnapshotFromJson(DefaultsPath);
                    if (defs.Count == 0) defs = temp.CaptureAllCurrentSizes();

                    // Clear overrides file
                    SaveSnapshot(new Dictionary<string,int>(), CurrentPath);

                    // Force editor back to defaults + current global delta
                    temp.ApplySnapshot(defs); // set to defaults absolute
                    var delta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
                    if (delta != 0) temp.ApplyDeltaToAllStyles(delta);

                    RepaintAll();
                }
                finally { temp.Close(); }
            };
            temp.ShowUtility();
            temp.position = new Rect(-10000, -10000, 1, 1);
        }

        
        [MenuItem("Window/Editor Font Size/Snapshots/Open Settings Folder")]
        private static void OpenSettingsFolder()
        {
            EnsureDirs();
            EditorUtility.RevealInFinder(SettingsDir);
        }

        
        // ---------------------- Unity -----------------------
        private void OnDisable()
        {
            _editorStyleProps = null;
            _guiSkinProps = null;
        }

        
        private void OnGUI()
        {
            // Handle deferred init (runs once, then bails this frame)
            if (_initCallback != null)
            {
                _initCallback();
                _initCallback = null;
                return;
            }

            InitRowBgs();
            GrabProperties();

            DrawGlobalControls();
            GUILayout.Space(4);
            EditorGUILayout.LabelField(GUIContent.none, GUI.skin.horizontalSlider);
            GUILayout.Space(2);

            using var scope = new GUILayout.ScrollViewScope(_scroll);
            _scroll = scope.scrollPosition;

            if (Header("Editor Styles"))
            {
                ForEachStyleProperty(_editorStyleProps, null, FontSizeRow);
            }

            if (Header("GUI Skins"))
            {
                ForEachStyleProperty(_guiSkinProps, GUI.skin, FontSizeRow);
            }

            if (Header("Custom Styles"))
            {
                foreach (var s in GUI.skin.customStyles)
                    FontSizeRow(s);
            }
        }

        
        // ---------------------- UI --------------------------
        private void DrawGlobalControls()
        {
            _rowCount = -1;

            var currentDelta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);

            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Global Zoom", EditorStyles.boldLabel);

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("-", EditorStyles.miniButtonLeft, GUILayout.Width(ButtonWidth)))
                {
                    NudgeGlobal(-1);
                    currentDelta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    GUILayout.Button(currentDelta == 0 ? "0" : currentDelta.ToString(),
                                     EditorStyles.miniButtonMid, GUILayout.Width(ButtonMidWidth));
                }

                if (GUILayout.Button("+", EditorStyles.miniButtonRight, GUILayout.Width(ButtonWidth)))
                {
                    NudgeGlobal(+1);
                    currentDelta = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
                }
            }

            
            // Slider for quick jumps
            EditorGUI.BeginChangeCheck();
            var newDelta = EditorGUILayout.IntSlider("Zoom (Δ font size)", currentDelta, -6, +10);
            if (EditorGUI.EndChangeCheck())
            {
                var diff = newDelta - currentDelta;
                if (diff != 0) NudgeGlobal(diff);
            }

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset Global", GUILayout.Width(110)))
                {
                    var d = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0);
                    if (d != 0) NudgeGlobal(-d); else RepaintAll();
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.HelpBox($"Clamped {MinFont}–{MaxFont}. Changes repaint all editor windows.", MessageType.None);
            }
        }

        
        private void NudgeGlobal(int delta)
        {
            if (delta == 0) return;

            if (ApplyDeltaToAllStyles(delta))
            {
                var updated = EditorPrefs.GetInt(PrefKeyGlobalDelta, 0) + delta;
                EditorPrefs.SetInt(PrefKeyGlobalDelta, updated);
                RepaintAll();
            }
        }

        
        // ------------------ Snapshots I/O -------------------
        private static void EnsureDirs()
        {
            if (!Directory.Exists(SettingsDir)) Directory.CreateDirectory(SettingsDir);
        }

        
        private static Dictionary<string,int> SnapshotFromJson(string path)
        {
            var map = new Dictionary<string,int>();
            try
            {
                if (!File.Exists(path)) return map;
                var json = File.ReadAllText(path);
                var snap = JsonUtility.FromJson<StyleSnapshot>(json);
                if (snap?.entries != null)
                    foreach (var e in snap.entries)
                        if (!string.IsNullOrEmpty(e.name)) map[e.name] = e.size;
            }
            catch { /* ignore corrupt/IO errors */ }
            return map;
        }

        
        private static void SaveSnapshot(Dictionary<string,int> map, string path)
        {
            try
            {
                EnsureDirs();
                var snap = new StyleSnapshot();
                foreach (var kv in map)
                    snap.entries.Add(new StyleEntry { name = kv.Key, size = kv.Value });
                var json = JsonUtility.ToJson(snap, true);
                File.WriteAllText(path, json);
            }
            catch { /* ignore IO errors */ }
        }

        
        // Build an overrides-only map: entries where current != defaults (or defaults missing)
        private Dictionary<string,int> ComputeOverrides(Dictionary<string,int> current, Dictionary<string,int> defaults)
        {
            var result = new Dictionary<string,int>();
            if (current == null) return result;

            foreach (var kv in current)
            {
                var keyName = kv.Key;
                var size = kv.Value;
                if (!defaults.TryGetValue( keyName, out var def) || size != def)
                    result[ keyName] = size;
            }
            return result;
        }

        
        private void MarkCurrentDirty()
        {
            if (_dirtyCurrentPendingSave) return;
            _dirtyCurrentPendingSave = true;
            EditorApplication.delayCall += () =>
            {
                _dirtyCurrentPendingSave = false;
                if (_currentMap != null) SaveSnapshot(_currentMap, CurrentPath);
            };
        }

        
        // Capture the editor’s *current* sizes to a map (single pass)
        private Dictionary<string,int> CaptureAllCurrentSizes()
        {
            var seen = new HashSet<GUIStyle>();
            var map = new Dictionary<string,int>();

            foreach (var s in EnumerateAllStyles())
            {
                if (s == null || !seen.Add(s) || string.IsNullOrEmpty(s.name)) continue;
                var size = s.fontSize <= 0 ? DefaultFontSize : s.fontSize;
                map[s.name] = size;
            }
            return map;
        }

        
        // Apply absolute sizes from a map (full snapshot)
        private void ApplySnapshot(Dictionary<string,int> map)
        {
            if (map == null || map.Count == 0) return;

            var seen = new HashSet<GUIStyle>();
            foreach (var s in EnumerateAllStyles())
            {
                if (s == null || !seen.Add(s) || string.IsNullOrEmpty(s.name)) continue;
                if (map.TryGetValue(s.name, out var target))
                    s.fontSize = Mathf.Clamp(target, MinFont, MaxFont);
            }
        }

        
        // Apply overrides only (keys present in map)
        private void ApplyOverrides(Dictionary<string,int> overrides)
        {
            ApplySnapshot(overrides);
        }

        
        // ------------- Style listing & application ----------
        private void GrabProperties()
        {
            const BindingFlags flagsStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.GetProperty;
            const BindingFlags flagsInst   = BindingFlags.Instance | BindingFlags.Public;

            _editorStyleProps ??= typeof(EditorStyles).GetProperties(flagsStatic);
            _guiSkinProps    ??= GUI.skin.GetType().GetProperties(flagsInst);
        }

        
        private static bool TryGetStyle(PropertyInfo p, object target, out GUIStyle style)
        {
            style = null;
            if (p?.PropertyType != typeof(GUIStyle)) return false;

            try
            {
                style = p.GetValue(target, null) as GUIStyle;
                return style != null && !string.IsNullOrEmpty(style.name);
            }
            catch
            {
                return false;
            }
        }

        
        private IEnumerable<GUIStyle> EnumerateAllStyles()
        {
            // EditorStyles (static)
            foreach (var p in _editorStyleProps)
            {
                if (TryGetStyle(p, null, out var s))
                    yield return s;
            }

            // GUI.skin (instance)
            foreach (var p in _guiSkinProps)
            {
                if (TryGetStyle(p, GUI.skin, out var s))
                    yield return s;
            }

            // Custom
            foreach (var s in GUI.skin.customStyles)
            {
                if (s != null && !string.IsNullOrEmpty(s.name))
                    yield return s;
            }
        }

        
        private static bool ClampAndApply(GUIStyle s, int delta)
        {
            if (s == null) return false;

            var start = s.fontSize <= 0 ? DefaultFontSize : s.fontSize;
            var next = Mathf.Clamp(start + delta, MinFont, MaxFont);
            if (next == s.fontSize) return false;

            s.fontSize = next;
            return true;
        }

        
        private bool ApplyDeltaToAllStyles(int delta)
        {
            var seen = new HashSet<GUIStyle>();
            var any = false;

            foreach (var s in EnumerateAllStyles())
            {
                if (s == null || !seen.Add(s)) continue;
                any |= ClampAndApply(s, delta);
            }
            return any;
        }

        private static void RepaintAll()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                w.Repaint();

            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        
        // -------------------- UI helpers --------------------
        private bool Header(string headerTitle)
        {
            var expanded = _foldouts.GetValueOrDefault(headerTitle, true);
            expanded = EditorGUILayout.Foldout(expanded, headerTitle, true);
            _foldouts[headerTitle] = expanded;
            return expanded;
        }

        
        private void InitRowBgs()
        {
            if (_evenBg != null) return;

            _evenBg = new GUIStyle("CN EntryBackEven");
            _oddBg  = new GUIStyle("CN EntryBackOdd");
            foreach (var g in new[] { _evenBg, _oddBg })
            {
                g.contentOffset = Vector2.zero;
                g.clipping = TextClipping.Clip;
                g.margin = g.padding = new RectOffset();
            }
        }

        private void ForEachStyleProperty(PropertyInfo[] props, object target, Action<GUIStyle> action)
        {
            foreach (var p in props)
            {
                if (TryGetStyle(p, target, out var s))
                    action(s);
            }
        }

        
        private void FontSizeRow(GUIStyle s)
        {
            if (s == null || string.IsNullOrEmpty(s.name)) return;

            var shown = s.fontSize <= 0 ? DefaultFontSize : s.fontSize;

            // Figure out if this style is currently overridden
            _defaultsMap ??= SnapshotFromJson(DefaultsPath);
            _currentMap  ??= SnapshotFromJson(CurrentPath);
            var isOverride = _currentMap.ContainsKey(s.name);

            var delta = FontSizeRowInternal(s.name, shown.ToString(), isOverride);
            if (delta == 0) return;

            var old = s.fontSize <= 0 ? DefaultFontSize : s.fontSize;
            var next = Mathf.Clamp(old + delta, MinFont, MaxFont);
            if (next == old) return;

            s.fontSize = next;

            // Persist override (diff vs defaults)
            if (_defaultsMap.TryGetValue(s.name, out var def) && next == def)
            {
                if (_currentMap.Remove(s.name)) MarkCurrentDirty();
            }
            else
            {
                _currentMap[s.name] = next;
                MarkCurrentDirty();
            }

            RepaintAll();
        }


        private int FontSizeRowInternal(string itemName, string sizeText, bool isOverride)
        {
            if (string.IsNullOrEmpty(itemName)) return 0;

            _rowCount++;
            using (new GUILayout.HorizontalScope(_rowCount % 2 == 0 ? _evenBg : _oddBg, GUILayout.MaxWidth(Screen.width)))
            {
                GUILayout.Label(itemName);

                // Override status tag, quality of life really is awesome huh :D
                var prev = GUI.color;
                GUI.color = isOverride ? new Color(1f, 0.6f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);
                GUILayout.Label(isOverride ? "override" : "default", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                GUI.color = prev;

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("-", EditorStyles.miniButtonLeft, GUILayout.Width(ButtonWidth)))
                    return -1;

                using (new EditorGUI.DisabledScope(true))
                    GUILayout.Button(sizeText, EditorStyles.miniButtonMid, GUILayout.Width(ButtonMidWidth));

                if (GUILayout.Button("+", EditorStyles.miniButtonRight, GUILayout.Width(ButtonWidth)))
                    return +1;
            }
            return 0;
        }
    }
}

#endif
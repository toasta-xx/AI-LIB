using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace NullAgentLib
{
    public sealed class MenuEntry
    {
        public string Name;
        public bool Known;
        public Func<bool> State;
        public Action Toggle;
    }

    internal static class Menu
    {
        internal static Func<IEnumerable<MenuEntry>> Provider;
        internal static Func<string, bool?, string> Mapper;
        internal static Func<string, bool, bool> Permit;

        static readonly List<MenuEntry> uiCache = new List<MenuEntry>();
        static float uiAt = -999f;

        internal static void AutoWire()
        {
            Probe.Clear();
        }

        internal static string List()
        {
            return NullAgent.Run(() =>
            {
                List<MenuEntry> found = Scan();
                if (found.Count == 0)
                    return null;
                var text = new StringBuilder();
                for (int i = 0; i < found.Count && i < 60; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    text.Append(found[i].Name);
                    bool? state = Read(found[i]);
                    if (state.HasValue)
                        text.Append(state.Value ? " (on)" : " (off)");
                }
                if (found.Count > 60)
                    text.Append(", and " + (found.Count - 60) + " more");
                return text.ToString();
            }, 4000);
        }

        internal static string Set(string spoken, bool? want)
        {
            string result = NullAgent.Run(() => Apply(spoken, want), 3000);
            return result ?? "couldn't reach the menu";
        }

        static string Apply(string spoken, bool? want)
        {
            if (string.IsNullOrWhiteSpace(spoken))
                return "say the entry name";
            if (Mapper != null)
            {
                try
                {
                    string mapped = Mapper(spoken, want);
                    if (!string.IsNullOrEmpty(mapped))
                        return mapped;
                }
                catch (Exception e)
                {
                    Debug.LogError("[NullAgent] mapper " + e.Message);
                }
            }
            MenuEntry entry = Pick(Scan(), spoken);
            if (entry == null)
                return "no entry named " + spoken.Trim();
            Func<string, bool, bool> gate = Permit;
            if (gate != null)
            {
                bool? current = Read(entry);
                try
                {
                    if (!gate(entry.Name, want ?? !(current ?? false)))
                        return entry.Name + " is blocked";
                }
                catch { return entry.Name + " is blocked"; }
            }
            bool? now = Read(entry);
            if (want.HasValue && now.HasValue && now.Value == want.Value)
                return entry.Name + (want.Value ? " is already on" : " is already off");
            try
            {
                entry.Toggle();
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] toggle " + e.Message);
                return "couldn't toggle " + entry.Name;
            }
            Probe.Clear();
            uiCache.Clear();
            uiAt = -999f;
            if (want.HasValue)
                return (want.Value ? "turned on " : "turned off ") + entry.Name;
            return "toggled " + entry.Name;
        }

        static bool? Read(MenuEntry entry)
        {
            if (entry.State == null)
                return null;
            try { return entry.State(); }
            catch { return null; }
        }

        static MenuEntry Pick(List<MenuEntry> found, string spoken)
        {
            string key = Flat(spoken);
            if (key.Length == 0)
                return null;
            MenuEntry best = null;
            int bestLen = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i] == null || string.IsNullOrWhiteSpace(found[i].Name))
                    continue;
                string item = Flat(found[i].Name);
                if (item.Length == 0)
                    continue;
                if (item == key)
                    return found[i];
                if (key.Contains(item) && item.Length > bestLen)
                {
                    best = found[i];
                    bestLen = item.Length;
                }
            }
            if (bestLen >= 4)
                return best;
            if (key.Length < 4)
                return null;
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i] == null)
                    continue;
                string item = Flat(found[i].Name);
                if (item.Length >= 4 && item.Contains(key))
                    return found[i];
            }
            return null;
        }

        static List<MenuEntry> Scan()
        {
            if (Provider != null)
            {
                var provided = new List<MenuEntry>();
                try
                {
                    foreach (MenuEntry entry in Provider())
                    {
                        if (entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                            provided.Add(entry);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[NullAgent] provider " + e.Message);
                }
                if (provided.Count > 0)
                    return provided;
            }
            List<MenuEntry> probed = Probe.Find();
            if (probed != null && probed.Count > 0)
                return probed;
            if (Time.unscaledTime - uiAt < 6f && uiCache.Count > 0)
                return uiCache;
            uiAt = Time.unscaledTime;
            uiCache.Clear();
            Harvest();
            return uiCache;
        }

        static void Harvest()
        {
            Button[] buttons;
            try { buttons = Resources.FindObjectsOfTypeAll<Button>(); }
            catch { return; }
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.isActiveAndEnabled)
                    continue;
                string label = Label(button.transform);
                if (string.IsNullOrWhiteSpace(label) || label.Length > 48)
                    continue;
                Button captured = button;
                uiCache.Add(new MenuEntry
                {
                    Name = label.Trim(),
                    Known = false,
                    Toggle = () => captured.onClick.Invoke()
                });
            }
            Toggle[] toggles;
            try { toggles = Resources.FindObjectsOfTypeAll<Toggle>(); }
            catch { return; }
            for (int i = 0; i < toggles.Length; i++)
            {
                Toggle toggle = toggles[i];
                if (toggle == null || !toggle.isActiveAndEnabled)
                    continue;
                string label = Label(toggle.transform);
                if (string.IsNullOrWhiteSpace(label) || label.Length > 48)
                    continue;
                Toggle captured = toggle;
                uiCache.Add(new MenuEntry
                {
                    Name = label.Trim(),
                    Known = true,
                    State = () => captured.isOn,
                    Toggle = () => captured.isOn = !captured.isOn
                });
            }
        }

        static string Label(Transform root)
        {
            try
            {
                Component[] parts = root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < parts.Length; i++)
                {
                    Component part = parts[i];
                    if (part == null)
                        continue;
                    string typeName = part.GetType().Name;
                    if (typeName != "Text" && typeName != "TMP_Text" && typeName != "TextMeshProUGUI")
                        continue;
                    PropertyInfo text = part.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    string value = text == null ? null : text.GetValue(part, null) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch { }
            return null;
        }

        internal static string Flat(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var built = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToLowerInvariant(text[i]);
                if (char.IsLetterOrDigit(c))
                    built.Append(c);
            }
            return built.ToString();
        }
    }
}

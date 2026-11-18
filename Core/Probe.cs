using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace NullAgentLib
{
    internal static class Probe
    {
        static readonly string[] NameKeys = { "buttonText", "name", "Name", "modName", "title", "label", "displayName", "feature" };
        static readonly string[] StateKeys = { "modEnabled", "enabled", "IsEnabled", "isOn", "toggled", "active", "state" };
        static readonly string[] OnKeys = { "enableMethod", "enabledMethod", "onEnable", "Enable", "OnEnabled", "onEnabled", "TurnOn", "Activate" };
        static readonly string[] OffKeys = { "disableMethod", "disabledMethod", "onDisable", "Disable", "OnDisabled", "onDisabled", "TurnOff", "Deactivate" };

        static List<MenuEntry> cache;
        static float cacheAt = -999f;
        static bool busy;

        internal static List<MenuEntry> Find()
        {
            if (cache != null && UnityEngine.Time.unscaledTime - cacheAt < 8f)
                return cache;
            if (busy)
                return cache ?? new List<MenuEntry>();
            busy = true;
            try
            {
                cacheAt = UnityEngine.Time.unscaledTime;
                cache = Harvest();
                return cache;
            }
            finally
            {
                busy = false;
            }
        }

        internal static void Clear()
        {
            cache = null;
            cacheAt = -999f;
        }

        static List<MenuEntry> Harvest()
        {
            var found = new List<MenuEntry>();
            Assembly[] all;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return found; }
            for (int a = 0; a < all.Length && found.Count < 400; a++)
            {
                Assembly asm = all[a];
                string label;
                try { label = asm.GetName().Name ?? ""; }
                catch { continue; }
                if (label.StartsWith("System", StringComparison.Ordinal) || label.StartsWith("Unity", StringComparison.Ordinal) || label.StartsWith("mscorlib", StringComparison.Ordinal) || label.StartsWith("BepInEx", StringComparison.Ordinal) || label.StartsWith("0Harmony", StringComparison.Ordinal) || label.StartsWith("Mono", StringComparison.Ordinal))
                    continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException partial) { types = partial.Types; }
                catch { continue; }
                for (int t = 0; t < types.Length && found.Count < 400; t++)
                {
                    if (types[t] == null)
                        continue;
                    try { Sweep(types[t], found); }
                    catch { }
                }
            }
            return found;
        }

        static void Sweep(Type type, List<MenuEntry> into)
        {
            FieldInfo[] fields;
            try { fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch { return; }
            Pairs(fields, into);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!Enumerates(field.FieldType))
                    continue;
                object value;
                try { value = field.GetValue(null); }
                catch { continue; }
                Flatten(value, into, 0);
                if (into.Count >= 400)
                    return;
            }
        }

        static void Pairs(FieldInfo[] fields, List<MenuEntry> into)
        {
            for (int n = 0; n < fields.Length; n++)
            {
                if (fields[n].FieldType != typeof(string[]))
                    continue;
                string[] names;
                try { names = fields[n].GetValue(null) as string[]; }
                catch { continue; }
                if (names == null || names.Length == 0)
                    continue;
                for (int s = 0; s < fields.Length; s++)
                {
                    if (fields[s].FieldType != typeof(bool[]))
                        continue;
                    bool[] states;
                    try { states = fields[s].GetValue(null) as bool[]; }
                    catch { continue; }
                    if (states == null || states.Length != names.Length)
                        continue;
                    string[] labels = names;
                    bool[] flags = states;
                    for (int i = 0; i < labels.Length && into.Count < 400; i++)
                    {
                        string name = Clean(labels[i]);
                        if (name == null)
                            continue;
                        int slot = i;
                        bool present = false;
                        for (int k = 0; k < into.Count; k++)
                        {
                            if (into[k].Name == name)
                            {
                                present = true;
                                break;
                            }
                        }
                        if (present)
                            continue;
                        into.Add(new MenuEntry
                        {
                            Name = name,
                            Known = true,
                            State = () => slot < flags.Length && flags[slot],
                            Toggle = () =>
                            {
                                if (slot < flags.Length)
                                    flags[slot] = !flags[slot];
                            }
                        });
                    }
                }
            }
        }

        static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            string text = raw;
            int cut = text.IndexOf('\n');
            if (cut > 0)
                text = text.Substring(0, cut);
            else
            {
                cut = text.IndexOf('\\');
                if (cut > 0)
                    text = text.Substring(0, cut);
            }
            text = text.Trim();
            if (text.Length == 0 || text.Length > 48)
                return null;
            return text;
        }

        static bool Enumerates(Type type)
        {
            if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
                return false;
            Type element = Leaf(type);
            return element != null && Shaped(element);
        }

        static Type Leaf(Type type)
        {
            for (int depth = 0; depth < 4; depth++)
            {
                Type next = null;
                if (type.IsArray)
                    next = type.GetElementType();
                else
                {
                    Type[] args;
                    try { args = type.GetGenericArguments(); }
                    catch { return type; }
                    if (args.Length == 1)
                        next = args[0];
                }
                if (next == null)
                    return type;
                if (next == typeof(string))
                    return null;
                type = next;
            }
            return type;
        }

        static bool Shaped(Type element)
        {
            if (element == null || element.IsPrimitive || element == typeof(string) || element.IsEnum)
                return false;
            if (!HasName(element))
                return false;
            return HasState(element) || HasDelegate(element, OnKeys) || HasDelegate(element, OffKeys);
        }

        static bool HasName(Type type)
        {
            for (int i = 0; i < NameKeys.Length; i++)
            {
                FieldInfo field = type.GetField(NameKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(string))
                    return true;
                PropertyInfo property = type.GetProperty(NameKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(string) && property.CanRead)
                    return true;
            }
            return false;
        }

        static string NameOf(Type type, object item)
        {
            for (int i = 0; i < NameKeys.Length; i++)
            {
                FieldInfo field = type.GetField(NameKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(string))
                {
                    try { return field.GetValue(item) as string; }
                    catch { }
                }
                PropertyInfo property = type.GetProperty(NameKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(string) && property.CanRead)
                {
                    try { return property.GetValue(item, null) as string; }
                    catch { }
                }
            }
            return null;
        }

        static bool HasState(Type type)
        {
            for (int i = 0; i < StateKeys.Length; i++)
            {
                PropertyInfo property = type.GetProperty(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(bool) && property.CanRead)
                    return true;
                FieldInfo field = type.GetField(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(bool))
                    return true;
            }
            return false;
        }

        static bool HasDelegate(Type type, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                FieldInfo field = type.GetField(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType))
                    return true;
                PropertyInfo property = type.GetProperty(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && typeof(Delegate).IsAssignableFrom(property.PropertyType) && property.CanRead)
                    return true;
                MethodInfo method = type.GetMethod(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method != null)
                    return true;
            }
            return false;
        }

        static void Flatten(object value, List<MenuEntry> into, int depth)
        {
            if (value == null || depth > 3)
                return;
            if (value is string || !(value is IEnumerable items))
                return;
            int seen = 0;
            foreach (object item in items)
            {
                if (++seen > 500)
                    return;
                if (item == null || item is string)
                    continue;
                if (item is IEnumerable nested)
                {
                    Flatten(nested, into, depth + 1);
                }
                else
                {
                    MenuEntry entry;
                    try { entry = Build(item); }
                    catch { entry = null; }
                    if (entry != null)
                        into.Add(entry);
                }
                if (into.Count >= 400)
                    return;
            }
        }

        static MenuEntry Build(object item)
        {
            Type type = item.GetType();
            string name = NameOf(type, item);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim();
            if (name.Length > 48)
                return null;
            Func<bool> read = StateRead(type, item);
            Action<bool> write = StateWrite(type, item);
            Action on = Delegate(type, item, OnKeys);
            Action off = Delegate(type, item, OffKeys);
            if (read == null && write == null && on == null && off == null)
                return null;
            return new MenuEntry
            {
                Name = name,
                Known = read != null,
                State = read,
                Toggle = () =>
                {
                    bool next = true;
                    if (read != null)
                    {
                        next = !read();
                        if (write != null)
                            write(next);
                    }
                    Action act = next ? on : off;
                    if (act != null)
                        act();
                }
            };
        }

        static Func<bool> StateRead(Type type, object item)
        {
            for (int i = 0; i < StateKeys.Length; i++)
            {
                PropertyInfo property = type.GetProperty(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(bool) && property.CanRead)
                    return () =>
                    {
                        try { return property.GetValue(item, null) is bool value && value; }
                        catch { return false; }
                    };
                FieldInfo field = type.GetField(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(bool))
                    return () =>
                    {
                        try { return field.GetValue(item) is bool value && value; }
                        catch { return false; }
                    };
            }
            return null;
        }

        static Action<bool> StateWrite(Type type, object item)
        {
            for (int i = 0; i < StateKeys.Length; i++)
            {
                PropertyInfo property = type.GetProperty(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
                    return next =>
                    {
                        try { property.SetValue(item, next, null); }
                        catch { }
                    };
                FieldInfo field = type.GetField(StateKeys[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(bool))
                    return next =>
                    {
                        try { field.SetValue(item, next); }
                        catch { }
                    };
            }
            return null;
        }

        static Action Delegate(Type type, object item, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                FieldInfo field = type.GetField(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType))
                {
                    object host = field.IsStatic ? null : item;
                    return () =>
                    {
                        try
                        {
                            Delegate handler = field.GetValue(host) as Delegate;
                            if (handler != null)
                                handler.DynamicInvoke();
                        }
                        catch { }
                    };
                }
                PropertyInfo property = type.GetProperty(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && typeof(Delegate).IsAssignableFrom(property.PropertyType) && property.CanRead)
                {
                    return () =>
                    {
                        try
                        {
                            Delegate handler = property.GetValue(item, null) as Delegate;
                            if (handler != null)
                                handler.DynamicInvoke();
                        }
                        catch { }
                    };
                }
                MethodInfo method = type.GetMethod(keys[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    object host = method.IsStatic ? null : item;
                    return () =>
                    {
                        try { method.Invoke(host, null); }
                        catch { }
                    };
                }
            }
            return null;
        }
    }
}

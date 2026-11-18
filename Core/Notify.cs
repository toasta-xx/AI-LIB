using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NullAgentLib
{
    internal class Notify : MonoBehaviour
    {
        static Notify live;
        static Action<string> overrideSink;
        static readonly Queue<string> early = new Queue<string>();

        TMP_Text label;
        GameObject hud;
        int decayCounter;
        int decayLimit = 400;
        string lastText = "";
        int repeats;

        internal static void Hook(Action<string> sink)
        {
            overrideSink = sink;
        }

        internal static bool Available
        {
            get { return live != null && live.label != null; }
        }

        internal static void Say(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            Action<string> sink = overrideSink;
            if (sink != null)
            {
                try { sink(line); return; }
                catch { }
            }
            if (NullAgent.OnMain)
            {
                Local(line);
                return;
            }
            NullAgent.Post(() => Local(line));
        }

        static void Local(string line)
        {
            if (live == null)
            {
                Debug.Log("[NullAgent] " + line);
                return;
            }
            if (live.label == null && !live.Build())
            {
                early.Enqueue(line);
                while (early.Count > 20)
                    early.Dequeue();
                Debug.Log("[NullAgent] " + line);
                return;
            }
            live.Show(line);
        }

        void Show(string line)
        {
            string tag = "[<color=#FF00FF>" + Options.Name + "</color>] " + line + Environment.NewLine;
            if (lastText == tag)
            {
                repeats++;
                label.text = "<color=grey>(" + repeats + ")</color> " + tag;
                decayCounter = 0;
                return;
            }
            lastText = tag;
            repeats = 1;
            label.text = lastText;
            decayCounter = 0;
        }

        void FixedUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            if (label == null)
            {
                if (!Build())
                    return;
                while (early.Count > 0)
                    Show(early.Dequeue());
            }
            if (hud == null)
                return;
            if (hud.transform.parent != cam.transform)
                hud.transform.SetParent(cam.transform, false);
            Place(hud.transform);
            if (string.IsNullOrEmpty(label.text))
                return;
            decayCounter++;
            if (decayCounter <= decayLimit)
                return;
            decayCounter = 0;
            string text = label.text;
            int split = text.IndexOf('\n');
            if (split < 0)
            {
                label.text = "";
                lastText = "";
                repeats = 0;
                return;
            }
            label.text = text.Substring(split + 1);
            if (label.text.Length == 0)
            {
                lastText = "";
                repeats = 0;
            }
        }

        static void Place(Transform target)
        {
            target.localPosition = new Vector3(-0.3f, -0.23f, 0.55f);
            target.localRotation = Quaternion.identity;
            target.localScale = new Vector3(0.08f, 0.08f, 0.08f);
        }

        bool Build()
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null)
                    return false;
                hud = new GameObject("NullAgentNotification");
                label = hud.AddComponent<TextMeshPro>();
                label.richText = true;
                label.fontSize = 1.5f;
                label.alignment = TextAlignmentOptions.TopLeft;
                label.overflowMode = TextOverflowModes.Overflow;
                label.color = Color.white;
                TMP_FontAsset font = RigFont();
                if (font != null)
                {
                    label.font = font;
                    label.material = new Material(font.material);
                }
                RectTransform rect = label.rectTransform;
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(9f, 6f);
                rect.localPosition = Vector3.zero;
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
                hud.transform.SetParent(cam.transform, false);
                Place(hud.transform);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] hud " + e.Message);
                return false;
            }
        }

        static TMP_FontAsset RigFont()
        {
            try
            {
                Type tagger = Type.GetType("GorillaTagger, Assembly-CSharp");
                if (tagger != null)
                {
                    object instance = tagger.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                    if (instance != null)
                    {
                        object rig = tagger.GetField("offlineVRRig", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(instance);
                        if (rig != null)
                        {
                            object text = rig.GetType().GetField("playerText1", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(rig);
                            if (text != null)
                            {
                                object font = text.GetType().GetProperty("font", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(text, null);
                                TMP_FontAsset asset = font as TMP_FontAsset;
                                if (asset != null)
                                    return asset;
                            }
                        }
                    }
                }
                TMP_FontAsset[] all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (all != null && all.Length > 0)
                    return all[0];
            }
            catch { }
            return null;
        }

        void Awake()
        {
            if (live != null && live != this)
            {
                Destroy(this);
                return;
            }
            live = this;
        }

        void OnDestroy()
        {
            if (live == this)
                live = null;
        }
    }
}

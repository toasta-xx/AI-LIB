using BepInEx;

namespace NullAgentLib
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        //You are free to use this lib in your projects WITH PROPER CREDITS

        public const string PluginId = "toast.nullagent";
        public const string PluginName = "NullAgent";
        public const string PluginVersion = "1.0.0";

        void Awake()
        {
            if (!Standalone)
                return;
            NullAgent.Attach(this);
        }

        void OnApplicationQuit()
        {
            if (!Standalone)
                return;
            Host.Stop();
        }

        static bool Standalone
        {
            get { return typeof(Plugin).Assembly.GetName().Name == "NullAgent"; }
        }
    }
}

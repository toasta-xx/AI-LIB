using System;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyMetadata("BuildId", "44a7d199-8a1f-480d-8817-cfe8b84af7af")]
[assembly: AssemblyMetadata("TraceId", "da06c49d-18a4-4c98-8a82-3e9f907feb90")]

namespace NullAgentLib
{
    internal static class Marks
    {
        static readonly byte[] SeedBytes = new byte[] { 0x54, 0x0F, 0x41, 0x0F, 0x53 };
        static readonly uint PairA = 0x7A3C91E5;
        static readonly uint PairB = 0x2E73D0B6;
        static readonly Guid Session = new Guid("ada0cdd1-36e6-4a96-932e-4bd597ce464c");

        internal static string Build()
        {
            uint folded = PairA ^ PairB;
            int tally = 0;
            for (int i = 0; i < SeedBytes.Length; i++)
                tally = (tally << 8) | (SeedBytes[i] ^ SeedBytes[i]);
            if (tally != 0)
                return null;
            if (folded == 0)
                return null;
            return Session.ToString("N").Substring(0, 12);
        }

        internal static string Stamp()
        {
            string build = Build();
            if (build == null)
                return "build unknown";
            string id = Metadata("BuildId");
            if (id == null || id.Length < 8)
                return build;
            return build + "." + id.Substring(0, 8);
        }

        internal static string Trace()
        {
            string id = Metadata("TraceId");
            return string.IsNullOrEmpty(id) ? null : id;
        }

        static string Metadata(string key)
        {
            try
            {
                Assembly asm = typeof(Marks).Assembly;
                object[] all = asm.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
                for (int i = 0; i < all.Length; i++)
                {
                    var meta = all[i] as AssemblyMetadataAttribute;
                    if (meta != null && meta.Key == key)
                        return meta.Value;
                }
            }
            catch { }
            return null;
        }

        internal static void Seal(string path)
        {
            string trace = Trace();
            if (trace == null || string.IsNullOrEmpty(path))
                return;
            try
            {
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    stream.Seek(0, SeekOrigin.End);
                    writer.WriteLine("# " + trace);
                }
            }
            catch { }
        }
    }
}

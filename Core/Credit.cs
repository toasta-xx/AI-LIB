using System;

namespace NullAgentLib
{
    internal static class Credit
    {
        static readonly byte[] Sealed = { 95, 95, 52, 9, 235 };
        static readonly byte[] NoticeSealed =
        {
            74, 121, 117, 54, 214, 134, 201, 99, 82, 60, 24, 130, 176, 133, 101, 94, 123, 236, 202, 188, 138, 52, 91, 39, 163, 220, 162, 147, 100, 72
        };

        internal static string Author
        {
            get { return Decode(Sealed); }
        }

        internal static string Notice
        {
            get { return Decode(NoticeSealed); }
        }

        static string Decode(byte[] sealedBytes)
        {
            if (sealedBytes == null || sealedBytes.Length == 0)
                return null;
            var text = new char[sealedBytes.Length];
            for (int i = 0; i < sealedBytes.Length; i++)
            {
                int value = sealedBytes[i] ^ ((i * 37 + 11) & 0xFF);
                if (value < 32 || value > 126)
                    return null;
                text[i] = (char)value;
            }
            return new string(text);
        }
    }
}

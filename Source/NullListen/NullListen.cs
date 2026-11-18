using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NullListen
{
    static class Program
    {
        const int WaveFormatPcm = 1;
        const int CallbackFunction = 0x00030000;
        const int WimData = 0x3C0;
        const int Rate = 16000;
        const int ChunkBytes = 3200;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveInCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Name;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AverageBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public IntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        delegate void WaveInProc(IntPtr handle, uint message, IntPtr instance, IntPtr header, IntPtr reserved);

        [DllImport("winmm.dll")]
        static extern int waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int waveInGetDevCapsW(int deviceId, ref WaveInCaps caps, int size);

        [DllImport("winmm.dll")]
        static extern int waveInOpen(out IntPtr handle, int deviceId, ref WaveFormat format, WaveInProc callback, IntPtr instance, int flags);

        [DllImport("winmm.dll")]
        static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInStart(IntPtr handle);

        [DllImport("winmm.dll")]
        static extern int waveInReset(IntPtr handle);

        [DllImport("winmm.dll")]
        static extern int waveInClose(IntPtr handle);

        static readonly object PrintGate = new object();
        static readonly object PhraseGate = new object();
        static readonly Queue<byte[]> Phrases = new Queue<byte[]>();
        static readonly List<Ear> Ears = new List<Ear>();

        static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string[] args = Environment.GetCommandLineArgs();
            int port = 0;
            int seconds = 0;
            bool steamOnly = false;
            for (int a = 1; a < args.Length; a++)
            {
                if (string.Equals(args[a], "steam", StringComparison.OrdinalIgnoreCase))
                {
                    steamOnly = true;
                    continue;
                }
                int number;
                if (!int.TryParse(args[a], out number))
                    continue;
                if (port == 0)
                    port = number;
                else
                    seconds = number;
            }
            if (port <= 0)
            {
                Say("SPEECHFAIL");
                return 1;
            }

            int count = waveInGetNumDevs();
            Say("DEVICES " + count);
            for (int i = 0; i < count; i++)
            {
                Ear ear = new Ear();
                if (!ear.Open(i))
                {
                    ear.Dispose();
                    continue;
                }
                if (steamOnly && ear.Name.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    ear.Dispose();
                    continue;
                }
                Ears.Add(ear);
                Say("DEVICE " + ear.Name);
            }
            if (Ears.Count == 0)
            {
                Say("NONE");
                return 1;
            }

            PcmPipe mix = new PcmPipe(Rate * 2 * 30);
            Thread mixer = new Thread(delegate () { Mix(mix); });
            mixer.IsBackground = true;
            mixer.Start();
            Thread hear = new Thread(delegate () { Hear(mix); });
            hear.IsBackground = true;
            hear.Start();
            Thread worker = new Thread(delegate () { Transcribe(port); });
            worker.IsBackground = true;
            worker.Start();

            DateTime until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            while (DateTime.UtcNow < until)
                Thread.Sleep(250);
            for (int i = 0; i < Ears.Count; i++)
                Ears[i].Dispose();
            return 0;
        }

        static void Mix(PcmPipe pipe)
        {
            byte[] tmp = new byte[ChunkBytes];
            short[] acc = new short[ChunkBytes / 2];
            byte[] mixed = new byte[ChunkBytes];
            while (true)
            {
                Thread.Sleep(100);
                for (int i = 0; i < acc.Length; i++)
                    acc[i] = 0;
                for (int e = 0; e < Ears.Count; e++)
                {
                    for (int i = 0; i < tmp.Length; i++)
                        tmp[i] = 0;
                    Ears[e].Pull(tmp, tmp.Length);
                    for (int i = 0; i < acc.Length; i++)
                    {
                        short sample = (short)(tmp[i * 2] | (tmp[i * 2 + 1] << 8));
                        int sum = acc[i] + sample;
                        if (sum > 32767) sum = 32767;
                        if (sum < -32768) sum = -32768;
                        acc[i] = (short)sum;
                    }
                }
                for (int i = 0; i < acc.Length; i++)
                {
                    mixed[i * 2] = (byte)acc[i];
                    mixed[i * 2 + 1] = (byte)(acc[i] >> 8);
                }
                pipe.Push(mixed, mixed.Length);
            }
        }

        static void Hear(PcmPipe mix)
        {
            byte[] chunk = new byte[ChunkBytes];
            var roll = new Queue<byte[]>();
            var speech = new List<byte>();
            int loud = 0;
            int quiet = 0;
            bool open = false;
            while (true)
            {
                int n = mix.Read(chunk, 0, chunk.Length);
                if (n <= 0)
                    break;
                byte[] copy = new byte[n];
                Buffer.BlockCopy(chunk, 0, copy, 0, n);
                float rms = Rms(copy);
                if (!open)
                {
                    roll.Enqueue(copy);
                    while (roll.Count > 3)
                        roll.Dequeue();
                    if (rms >= 0.01f)
                        loud++;
                    else
                        loud = 0;
                    if (loud < 2)
                        continue;
                    open = true;
                    quiet = 0;
                    speech.Clear();
                    foreach (byte[] past in roll)
                        speech.AddRange(past);
                    roll.Clear();
                    continue;
                }
                speech.AddRange(copy);
                if (rms >= 0.008f)
                    quiet = 0;
                else
                    quiet++;
                if (quiet < 7 && speech.Count < Rate * 2 * 8)
                    continue;
                int tail = quiet * ChunkBytes;
                if (tail > speech.Count)
                    tail = speech.Count;
                if (speech.Count - tail >= Rate * 2 / 4)
                {
                    byte[] phrase = speech.ToArray();
                    lock (PhraseGate)
                    {
                        Phrases.Enqueue(phrase);
                        Monitor.Pulse(PhraseGate);
                    }
                }
                speech.Clear();
                open = false;
                loud = 0;
                quiet = 0;
            }
        }

        static void Transcribe(int port)
        {
            while (true)
            {
                byte[] pcm;
                lock (PhraseGate)
                {
                    while (Phrases.Count == 0)
                        Monitor.Wait(PhraseGate);
                    pcm = Phrases.Dequeue();
                }
                try
                {
                    string json = PostWav(port, ToWav(pcm));
                    string text = Clean(ReadText(json));
                    if (text.Length > 0)
                        Say("HEARD 0.90 " + text);
                }
                catch (WebException ex)
                {
                    string detail = ex.Message;
                    try
                    {
                        if (ex.Response != null)
                        {
                            using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                                detail = reader.ReadToEnd();
                        }
                    }
                    catch { }
                    Say("WHISPER " + detail);
                }
                catch (Exception e)
                {
                    Say("WHISPER " + e.Message);
                }
            }
        }

        static string PostWav(int port, byte[] wav)
        {
            string boundary = "----Null" + DateTime.UtcNow.Ticks.ToString();
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString() + "/inference");
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Timeout = 25000;
            request.ReadWriteTimeout = 25000;
            using (Stream body = request.GetRequestStream())
            {
                WriteField(body, boundary, "temperature", "0");
                WriteField(body, boundary, "temperature_inc", "0");
                WriteField(body, boundary, "response_format", "json");
                WriteField(body, boundary, "language", "en");
                WriteFile(body, boundary, wav);
                byte[] end = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");
                body.Write(end, 0, end.Length);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        static void WriteField(Stream body, string boundary, string name, string value)
        {
            string part = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(part);
            body.Write(bytes, 0, bytes.Length);
        }

        static void WriteFile(Stream body, string boundary, byte[] wav)
        {
            string head = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"phrase.wav\"\r\nContent-Type: audio/wav\r\n\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(head);
            body.Write(bytes, 0, bytes.Length);
            body.Write(wav, 0, wav.Length);
            byte[] tail = Encoding.UTF8.GetBytes("\r\n");
            body.Write(tail, 0, tail.Length);
        }

        static byte[] ToWav(byte[] pcm)
        {
            byte[] wav = new byte[44 + pcm.Length];
            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            int chunk = 36 + pcm.Length;
            wav[4] = (byte)chunk; wav[5] = (byte)(chunk >> 8); wav[6] = (byte)(chunk >> 16); wav[7] = (byte)(chunk >> 24);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            wav[16] = 16;
            wav[20] = 1;
            wav[22] = 1;
            int rate = Rate;
            wav[24] = (byte)rate; wav[25] = (byte)(rate >> 8); wav[26] = (byte)(rate >> 16); wav[27] = (byte)(rate >> 24);
            int byteRate = rate * 2;
            wav[28] = (byte)byteRate; wav[29] = (byte)(byteRate >> 8); wav[30] = (byte)(byteRate >> 16); wav[31] = (byte)(byteRate >> 24);
            wav[32] = 2;
            wav[34] = 16;
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            wav[40] = (byte)pcm.Length; wav[41] = (byte)(pcm.Length >> 8); wav[42] = (byte)(pcm.Length >> 16); wav[43] = (byte)(pcm.Length >> 24);
            Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
            return wav;
        }

        static string ReadText(string json)
        {
            if (string.IsNullOrEmpty(json))
                return "";
            string key = "\"text\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return "";
            int quote = json.IndexOf('"', json.IndexOf(':', at + key.Length) + 1);
            if (quote < 0)
                return "";
            var built = new StringBuilder();
            for (int i = quote + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    if (next == 'n' || next == 'r' || next == 't')
                        built.Append(' ');
                    else
                        built.Append(next);
                    i++;
                    continue;
                }
                if (c == '"')
                    break;
                built.Append(c);
            }
            return built.ToString();
        }

        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim().ToLowerInvariant();
            if (text.Length == 0)
                return "";
            if (text.Contains("blinding lights") && text.Contains("speedboost") && text.Contains("gorillas"))
                return "";
            string[] junk =
            {
                "thank you",
                "thanks for watching",
                "subscribe",
                "you",
                "bye",
                "music",
                "silence",
                "blank audio"
            };
            for (int i = 0; i < junk.Length; i++)
            {
                if (text == junk[i] || text == "[" + junk[i] + "]" || text == "(" + junk[i] + ")")
                    return "";
            }
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text[i]))
                    return text;
            }
            return "";
        }

        static float Rms(byte[] pcm)
        {
            if (pcm.Length < 2)
                return 0;
            long sum = 0;
            int count = pcm.Length / 2;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                sum += sample * (long)sample;
            }
            return (float)Math.Sqrt(sum / (double)count) / 32768f;
        }

        static void Say(string line)
        {
            lock (PrintGate)
            {
                Console.WriteLine(line);
                Console.Out.Flush();
            }
        }

        sealed class Ear : IDisposable
        {
            const int BufferCount = 3;
            public string Name = "microphone";
            IntPtr handle;
            WaveInProc callback;
            readonly IntPtr[] headers = new IntPtr[BufferCount];
            readonly IntPtr[] samples = new IntPtr[BufferCount];
            int headerSize;
            bool started;

            readonly object qgate = new object();
            readonly byte[] queued = new byte[Rate * 2];
            int qHead;
            int qSize;

            public bool Open(int deviceId)
            {
                WaveInCaps caps = new WaveInCaps();
                if (waveInGetDevCapsW(deviceId, ref caps, Marshal.SizeOf(typeof(WaveInCaps))) == 0 && !string.IsNullOrWhiteSpace(caps.Name))
                    Name = caps.Name.Trim();
                if (!TryWave(deviceId, Rate))
                {
                    Console.Error.WriteLine("open failed " + Name);
                    return false;
                }
                return true;
            }

            public void Pull(byte[] dest, int count)
            {
                lock (qgate)
                {
                    int n = Math.Min(count, qSize);
                    for (int i = 0; i < n; i++)
                    {
                        dest[i] = queued[qHead];
                        qHead = (qHead + 1) % queued.Length;
                        qSize--;
                    }
                }
            }

            void PushSamples(byte[] source, int count)
            {
                lock (qgate)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (qSize == queued.Length)
                        {
                            qHead = (qHead + 1) % queued.Length;
                            qSize--;
                        }
                        queued[(qHead + qSize) % queued.Length] = source[i];
                        qSize++;
                    }
                }
            }

            bool TryWave(int deviceId, int rate)
            {
                CloseWave();
                callback = OnWave;
                var format = new WaveFormat
                {
                    FormatTag = WaveFormatPcm,
                    Channels = 1,
                    SamplesPerSecond = (uint)rate,
                    BitsPerSample = 16,
                    BlockAlign = 2,
                    AverageBytesPerSecond = (uint)(rate * 2)
                };
                if (waveInOpen(out handle, deviceId, ref format, callback, IntPtr.Zero, CallbackFunction) != 0)
                    return false;
                headerSize = Marshal.SizeOf(typeof(WaveHeader));
                uint bytes = (uint)Math.Max(rate / 5, 1600);
                for (int i = 0; i < BufferCount; i++)
                {
                    samples[i] = Marshal.AllocHGlobal((int)bytes);
                    headers[i] = Marshal.AllocHGlobal(headerSize);
                    var header = new WaveHeader { Data = samples[i], BufferLength = bytes };
                    Marshal.StructureToPtr(header, headers[i], false);
                    if (waveInPrepareHeader(handle, headers[i], headerSize) != 0 || waveInAddBuffer(handle, headers[i], headerSize) != 0)
                        return false;
                }
                if (waveInStart(handle) != 0)
                    return false;
                started = true;
                return true;
            }

            void OnWave(IntPtr wave, uint message, IntPtr instance, IntPtr headerPtr, IntPtr reserved)
            {
                if (message != WimData || headerPtr == IntPtr.Zero)
                    return;
                try
                {
                    WaveHeader header = Marshal.PtrToStructure<WaveHeader>(headerPtr);
                    int count = (int)header.BytesRecorded;
                    if (count > 1 && header.Data != IntPtr.Zero)
                    {
                        byte[] copy = new byte[count];
                        Marshal.Copy(header.Data, copy, 0, count);
                        PushSamples(copy, count);
                    }
                    waveInAddBuffer(wave, headerPtr, headerSize);
                }
                catch { }
            }

            public void Dispose()
            {
                CloseWave();
            }

            void CloseWave()
            {
                if (handle != IntPtr.Zero)
                {
                    try { if (started) waveInReset(handle); } catch { }
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (headers[i] != IntPtr.Zero)
                        {
                            try { waveInUnprepareHeader(handle, headers[i], headerSize); } catch { }
                        }
                    }
                    try { waveInClose(handle); } catch { }
                    handle = IntPtr.Zero;
                }
                started = false;
                for (int i = 0; i < BufferCount; i++)
                {
                    if (headers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(headers[i]);
                        headers[i] = IntPtr.Zero;
                    }
                    if (samples[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(samples[i]);
                        samples[i] = IntPtr.Zero;
                    }
                }
            }
        }

        sealed class PcmPipe : Stream
        {
            readonly object gate = new object();
            readonly byte[] data;
            int head;
            int size;
            bool closed;

            public PcmPipe(int bytes)
            {
                data = new byte[Math.Max(bytes, 8000)];
            }

            public void Push(byte[] source, int count)
            {
                lock (gate)
                {
                    if (closed)
                        return;
                    for (int i = 0; i < count; i++)
                    {
                        if (size == data.Length)
                        {
                            head = (head + 1) % data.Length;
                            size--;
                        }
                        data[(head + size) % data.Length] = source[i];
                        size++;
                    }
                    Monitor.PulseAll(gate);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int filled = 0;
                while (filled < count)
                {
                    lock (gate)
                    {
                        while (size == 0 && !closed)
                            Monitor.Wait(gate);
                        if (size == 0)
                            break;
                        int n = Math.Min(count - filled, size);
                        for (int i = 0; i < n; i++)
                        {
                            buffer[offset + filled] = data[head];
                            head = (head + 1) % data.Length;
                            filled++;
                            size--;
                        }
                    }
                }
                return filled;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return size; } }
            public override long Position { get { return 0; } set { } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { return 0; }
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }

            protected override void Dispose(bool disposing)
            {
                lock (gate)
                {
                    closed = true;
                    Monitor.PulseAll(gate);
                }
                base.Dispose(disposing);
            }
        }
    }
}

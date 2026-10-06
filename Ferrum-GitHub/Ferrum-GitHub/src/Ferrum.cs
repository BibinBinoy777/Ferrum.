using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Diagnostics;

namespace DesktopVoiceAssistant
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVoiceAssistant");
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length) data = Path.GetFullPath(args[index + 1]);
            if (args.Contains("--self-test")) return SelfTest.Run(data);
            using (MainForm form = new MainForm(new Store(data)))
            {
                index = Array.IndexOf(args, "--preview");
                if (index >= 0 && index + 1 < args.Length)
                {
                    form.Show();
                    Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(args[index + 1]);
                    }
                    form.Close();
                    return 0;
                }
                Application.Run(form);
            }
            return 0;
        }
    }

    public class Preferences
    {
        public bool Speak = true;
        public string Voice = "coral";
        public Dictionary<string, string> Apps = new Dictionary<string, string>();
    }

    public sealed class Store
    {
        public readonly string Root;
        public string NoteDirectory { get { return Path.Combine(Root, "notes"); } }
        public Preferences Preferences;
        public string Key = "";
        public string LoadWarning = "";
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        public Store(string root)
        {
            Root = root;
            Preferences = new Preferences();
            try
            {
                string prefs = Path.Combine(root, "settings.json");
                if (File.Exists(prefs)) Preferences = Json.Deserialize<Preferences>(File.ReadAllText(prefs)) ?? new Preferences();
                if (Preferences.Apps == null) Preferences.Apps = new Dictionary<string, string>();
                string keyPath = Path.Combine(root, "key.dat");
                if (File.Exists(keyPath)) Key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser));
            }
            catch { LoadWarning = "Saved settings could not be loaded. Open Settings to reconnect."; }
        }
        public void Save(Preferences prefs, string key)
        {
            Directory.CreateDirectory(Root);
            byte[] encrypted = null;
            if (!String.IsNullOrWhiteSpace(key)) encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(Path.Combine(Root, "settings.json"), Json.Serialize(prefs), new UTF8Encoding(false));
            string keyPath = Path.Combine(Root, "key.dat");
            if (encrypted == null) { if (File.Exists(keyPath)) File.Delete(keyPath); }
            else File.WriteAllBytes(keyPath, encrypted);
            Preferences = prefs;
            Key = key.Trim();
        }
        public string SaveNote(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Tell me what to save. Try: save a note buy milk tomorrow.");
            Directory.CreateDirectory(NoteDirectory);
            string path = Path.Combine(NoteDirectory, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + Environment.NewLine + Environment.NewLine + text.Trim() + Environment.NewLine, new UTF8Encoding(false));
            return path;
        }
        public string[] Notes() { return Directory.Exists(NoteDirectory) ? Directory.GetFiles(NoteDirectory, "*.txt").OrderByDescending(p => p).ToArray() : new string[0]; }
    }

    public class Command
    {
        public string Kind, Text;
        public Command(string kind, string text) { Kind = kind; Text = text; }
    }

    public static class Commands
    {
        public static Command Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return new Command("empty", "");
            Match note = Regex.Match(text, @"^(?:save\s+(?:a\s+)?note|take\s+(?:a\s+)?note|note)(?:\s*[:,-]\s*|\s+|$)(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (note.Success) return new Command("note", note.Groups[1].Value.Trim());
            Match open = Regex.Match(text, @"^(?:open|launch|start)(?:\s+|$)(.*)$", RegexOptions.IgnoreCase);
            if (open.Success) return new Command("open", open.Groups[1].Value.Trim().TrimEnd('.', '!', '?'));
            return new Command("question", Regex.Replace(text, @"^(?:answer\s+(?:a\s+)?question|ask)(?:\s*:\s*|\s+)", "", RegexOptions.IgnoreCase));
        }
        public static string NormalizeApp(string name)
        {
            return Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
        }
        public static string ResolveApp(string name, Dictionary<string, string> custom)
        {
            string normalized = NormalizeApp(name);
            string customPath;
            if (custom.TryGetValue(normalized, out customPath))
            {
                if (!Path.IsPathRooted(customPath) || !File.Exists(customPath) || !String.Equals(Path.GetExtension(customPath), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This app has moved or is unavailable. Choose it again in Settings.");
                return customPath;
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            switch (normalized)
            {
                case "notepad": return Path.Combine(windows, "System32", "notepad.exe");
                case "calculator": case "calc": return Path.Combine(windows, "System32", "calc.exe");
                case "paint": return Path.Combine(windows, "System32", "mspaint.exe");
                case "file explorer": case "explorer": case "files": return Path.Combine(windows, "explorer.exe");
                case "browser": case "web browser": return "https://www.google.com/";
                default: throw new InvalidOperationException("I don't have that app yet. Open Settings and add it, or try Notepad, Calculator, Paint, File Explorer, or Browser.");
            }
        }
        public static void Open(string target)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    public sealed class Api : IDisposable
    {
        private readonly HttpClient client;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        public Api(string key) : this(key, new HttpClientHandler()) { }
        internal Api(string key, HttpMessageHandler handler)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Open Settings and add your OpenAI API key to use voice or ask questions.");
            client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/"), Timeout = TimeSpan.FromSeconds(75) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        }
        private async Task<byte[]> Send(string path, HttpContent body, CancellationToken token)
        {
            using (body)
            using (HttpResponseMessage response = await client.PostAsync(path, body, token))
            {
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    if (status == 401) throw new InvalidOperationException("The API key was rejected. Check it in Settings.");
                    if (status == 429) throw new InvalidOperationException("The API limit was reached. Check your API credit or retry later.");
                    if (status == 403) throw new InvalidOperationException("Your API account cannot access this service. Check your account permissions.");
                    if (status == 400 || status == 404) throw new InvalidOperationException("The API could not accept this request. Check model access on your API account.");
                    throw new InvalidOperationException("The voice service returned an error (" + status + "). Please try again.");
                }
                return await response.Content.ReadAsByteArrayAsync();
            }
        }
        public async Task<string> Transcribe(byte[] wave, CancellationToken token)
        {
            MultipartFormDataContent body = new MultipartFormDataContent();
            body.Add(new StringContent("gpt-4o-mini-transcribe"), "model");
            body.Add(new StringContent("json"), "response_format");
            ByteArrayContent audio = new ByteArrayContent(wave);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            body.Add(audio, "file", "speech.wav");
            byte[] result = await Send("audio/transcriptions", body, token);
            Dictionary<string, object> document = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(result));
            object text;
            if (!document.TryGetValue("text", out text) || String.IsNullOrWhiteSpace(text as string)) throw new InvalidOperationException("I didn't catch that. Hold the button and speak clearly, or type your command.");
            return ((string)text).Trim();
        }
        public async Task<string> Answer(string question, CancellationToken token)
        {
            string body = json.Serialize(new { model = "gpt-4.1-mini", instructions = "Your name is Ferrum. You are a helpful desktop voice assistant. Answer in the user's language. Keep answers clear and usually under 100 words. You have no desktop tools and cannot claim to open apps or save notes. You have no live web access; be clear when current facts need verification.", input = question, max_output_tokens = 600, store = false });
            byte[] result = await Send("responses", new StringContent(body, Encoding.UTF8, "application/json"), token);
            return ReadAnswer(Encoding.UTF8.GetString(result));
        }
        public static string ReadAnswer(string jsonText)
        {
            Dictionary<string, object> doc = new JavaScriptSerializer { MaxJsonLength = 8000000 }.Deserialize<Dictionary<string, object>>(jsonText);
            StringBuilder answer = new StringBuilder();
            object output;
            if (doc.TryGetValue("output", out output))
                foreach (Dictionary<string, object> item in (IEnumerable)output)
                {
                    object type, content;
                    if (!item.TryGetValue("type", out type) || !Object.Equals(type, "message") || !item.TryGetValue("content", out content)) continue;
                    foreach (Dictionary<string, object> part in (IEnumerable)content)
                    {
                        object partType, text;
                        if (part.TryGetValue("type", out partType) && (Object.Equals(partType, "output_text") || Object.Equals(partType, "refusal")))
                        {
                            string field = Object.Equals(partType, "refusal") ? "refusal" : "text";
                            if (part.TryGetValue(field, out text)) answer.AppendLine(text as string);
                        }
                    }
                }
            if (answer.Length == 0) throw new InvalidOperationException("The AI returned no answer. Please try again.");
            return answer.ToString().Trim();
        }
        public async Task<byte[]> Speak(string text, string voice, CancellationToken token)
        {
            string body = json.Serialize(new { model = "gpt-4o-mini-tts", input = text, voice = voice, response_format = "wav" });
            return await Send("audio/speech", new StringContent(body, Encoding.UTF8, "application/json"), token);
        }
        public void Dispose() { client.Dispose(); }
    }

    // Native PCM capture uses an event and worker, never a multimedia callback that
    // calls back into waveIn. Recording starts only during a push-to-talk hold.
    public sealed class Recorder : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format { public ushort Tag, Channels; public uint Samples, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Header { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly List<IntPtr> headers = new List<IntPtr>();
        private readonly MemoryStream pcm = new MemoryStream();
        private Thread worker;
        private IntPtr handle;
        private volatile bool capturing, exit;
        private readonly object nativeGate = new object();
        public volatile float Level;
        private Exception workerError;
        private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(Header));
        public static uint DeviceCount { get { return waveInGetNumDevs(); } }
        private static void Check(uint result) { if (result != 0) throw new InvalidOperationException("The microphone could not start (" + result + "). Check the default input device and Windows microphone access."); }
        public void Start()
        {
            if (DeviceCount == 0) throw new InvalidOperationException("No microphone was found. Connect one or type a command.");
            Format format = new Format { Tag = 1, Channels = 1, Samples = 16000, BytesPerSecond = 32000, BlockAlign = 2, Bits = 16 };
            Check(waveInOpen(out handle, UInt32.MaxValue, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000));
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    Header header = new Header { Data = Marshal.AllocHGlobal(4096), Length = 4096 };
                    IntPtr pointer = Marshal.AllocHGlobal((int)headerSize);
                    Marshal.StructureToPtr(header, pointer, false);
                    headers.Add(pointer);
                    Check(waveInPrepareHeader(handle, pointer, headerSize));
                    Check(waveInAddBuffer(handle, pointer, headerSize));
                }
                capturing = true;
                worker = new Thread(Drain) { IsBackground = true, Name = "Microphone PCM" };
                worker.Start();
                Check(waveInStart(handle));
            }
            catch { Dispose(); throw; }
        }
        private void Drain()
        {
            try
            {
                while (true)
                {
                    signal.WaitOne(100);
                    lock (nativeGate) foreach (IntPtr pointer in headers)
                    {
                        Header h = (Header)Marshal.PtrToStructure(pointer, typeof(Header));
                        if ((h.Flags & 1) == 0) continue;
                        if (h.Recorded > 0)
                        {
                            byte[] bytes = new byte[h.Recorded];
                            Marshal.Copy(h.Data, bytes, 0, bytes.Length);
                            pcm.Write(bytes, 0, bytes.Length);
                            double sum = 0;
                            for (int i = 0; i + 1 < bytes.Length; i += 2) { double v = (short)(bytes[i] | (bytes[i + 1] << 8)); sum += v * v; }
                            Level = (float)Math.Min(1, Math.Sqrt(sum / Math.Max(1, bytes.Length / 2)) / 7000.0);
                            Marshal.WriteInt32(pointer, (int)Marshal.OffsetOf(typeof(Header), "Recorded"), 0);
                        }
                        if (capturing) Check(waveInAddBuffer(handle, pointer, headerSize));
                    }
                    if (exit) break;
                }
            }
            catch (Exception ex) { workerError = ex; }
        }
        public byte[] Stop()
        {
            CloseCapture();
            if (workerError != null) throw workerError;
            return Wave(pcm.ToArray());
        }
        public static byte[] Wave(byte[] samples)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length); writer.Write(samples);
                return stream.ToArray();
            }
        }
        private void CloseCapture()
        {
            if (handle == IntPtr.Zero) return;
            lock (nativeGate) { capturing = false; waveInReset(handle); }
            exit = true;
            signal.Set();
            if (worker != null) worker.Join();
            foreach (IntPtr pointer in headers)
            {
                Header h = (Header)Marshal.PtrToStructure(pointer, typeof(Header));
                waveInUnprepareHeader(handle, pointer, headerSize);
                Marshal.FreeHGlobal(h.Data); Marshal.FreeHGlobal(pointer);
            }
            headers.Clear();
            waveInClose(handle); handle = IntPtr.Zero;
        }
        public void Dispose() { CloseCapture(); signal.Dispose(); pcm.Dispose(); }
    }

    public static class Style
    {
        public static readonly Color Background = Color.FromArgb(15, 20, 29), Surface = Color.FromArgb(24, 31, 43), Accent = Color.FromArgb(107, 232, 193), Text = Color.FromArgb(232, 238, 247), Muted = Color.FromArgb(155, 169, 190);
        public static Label Label(string text, float size, Color color)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size), ForeColor = color, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }
        public static Button Button(string text, bool accent)
        {
            Button button = new Button { Text = text, Font = new Font("Segoe UI", 10, FontStyle.Bold), BackColor = accent ? Accent : Surface, ForeColor = accent ? Background : Text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Height = 40, AutoSize = false, Margin = new Padding(4) };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(139, 245, 213) : Color.FromArgb(38, 49, 65);
            return button;
        }
        public static TextBox Input(bool multiline)
        {
            return new TextBox { Multiline = multiline, BackColor = Surface, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11), Dock = DockStyle.Fill, Margin = new Padding(4) };
        }
        public static void PixelFonts(Control control)
        {
            Font f = control.Font;
            if (f.Unit != GraphicsUnit.Pixel) control.Font = new Font(f.FontFamily, f.SizeInPoints * 96f / 72f, f.Style, GraphicsUnit.Pixel);
            foreach (Control child in control.Controls) PixelFonts(child);
        }
    }

    public sealed class VoiceMeter : Control
    {
        public bool Recording;
        public float Level;
        private float phase;
        public VoiceMeter() { DoubleBuffered = true; BackColor = Style.Background; }
        public void Tick(float level) { Level = level; phase += .5f; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int bars = 29;
            float width = Math.Min(6, (Width - 12f) / bars), center = Width / 2f;
            using (Pen pen = new Pen(Recording ? Style.Accent : Color.FromArgb(59, 73, 92), Math.Max(2, width - 2)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                for (int i = 0; i < bars; i++)
                {
                    float height = Recording ? 5 + (float)Math.Abs(Math.Sin(i * .66 + phase)) * (6 + Level * (Height - 18)) : 5 + 12 * (float)Math.Abs(Math.Sin(i * .62));
                    float x = center + (i - bars / 2) * width;
                    e.Graphics.DrawLine(pen, x, (Height - height) / 2, x, (Height + height) / 2);
                }
            }
        }
    }

    public sealed class MainForm : Form
    {
        private readonly Store store;
        private readonly TextBox conversation;
        private readonly TextBox input;
        private readonly Button talk, send, cancel, settings, notes;
        private readonly Label status, connection;
        private readonly VoiceMeter meter;
        private readonly System.Windows.Forms.Timer timer;
        private Recorder recorder;
        private CancellationTokenSource cancellation;
        private SoundPlayer player;
        private MemoryStream playback;
        private bool busy, closed;
        private DateTime recordingStarted;

        public MainForm(Store data)
        {
            SuspendLayout();
            store = data;
            Text = "Ferrum";
            Icon = SystemIcons.Application;
            Size = new Size(860, 780); MinimumSize = new Size(760, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Style.Background; ForeColor = Style.Text; Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.None; KeyPreview = true;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 14), ColumnCount = 1, RowCount = 7, BackColor = Style.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = Style.Label("Ferrum", 20, Style.Text); title.Font = new Font("Segoe UI", 20, FontStyle.Bold);
            header.Controls.Add(title, 0, 0);
            connection = Style.Label("", 9.5f, Style.Muted); header.Controls.Add(connection, 0, 1); UpdateConnection();
            notes = Style.Button("My notes", false); notes.Dock = DockStyle.Fill; notes.Click += delegate { new NotesForm(store).ShowDialog(this); }; header.Controls.Add(notes, 1, 0);
            settings = Style.Button("Settings", false); settings.Dock = DockStyle.Fill; settings.Click += delegate { ShowSettings(); }; header.Controls.Add(settings, 2, 0);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel examples = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 7, 0, 10) };
            examples.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f)); examples.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f)); examples.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334f));
            AddExample(examples, 0, "01  OPEN AN APP", "Open Notepad", "open notepad");
            AddExample(examples, 1, "02  ASK A QUESTION", "Why is the sky blue?", "Why is the sky blue?");
            AddExample(examples, 2, "03  SAVE A NOTE", "Remember something", "save a note Buy milk tomorrow");
            root.Controls.Add(examples, 0, 1);

            conversation = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Style.Surface, ForeColor = Style.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), Margin = new Padding(4, 2, 4, 10) };
            root.Controls.Add(conversation, 0, 2);
            AddMessage("FERRUM", "Hi, I'm Ferrum, your desktop voice assistant. Hold the button to speak, then release to send.\n\nTry “open calculator”, “why is the sky blue?”, or “save a note buy milk tomorrow”.\n\nAdd your API key in Settings to enable voice and AI answers. Typed app commands and notes work right away.");
            if (store.LoadWarning.Length > 0) AddMessage("SETUP", store.LoadWarning);
            root.Controls.Add(Style.Label("OR TYPE A COMMAND", 8.5f, Style.Muted), 0, 3);

            TableLayoutPanel entry = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            input = Style.Input(false); input.Anchor = AnchorStyles.Left | AnchorStyles.Right; input.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunTyped(); } };
            send = Style.Button("Send", true); send.Dock = DockStyle.Fill; send.Click += async delegate { await RunTyped(); };
            entry.Controls.Add(input, 0, 0); entry.Controls.Add(send, 1, 0); root.Controls.Add(entry, 0, 4);

            TableLayoutPanel voice = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 10, 0, 0) };
            voice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); voice.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248)); voice.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            voice.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); voice.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            meter = new VoiceMeter { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 18, 3) }; voice.Controls.Add(meter, 0, 0);
            talk = Style.Button("Hold to talk", true); talk.Dock = DockStyle.Fill;
            talk.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) StartRecording(); };
            talk.MouseUp += async delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) await StopRecording(); };
            talk.MouseCaptureChanged += delegate { if (recorder != null && !talk.Capture) { BeginInvoke((Action)delegate { if (recorder != null) CancelRecording(); }); } };
            voice.Controls.Add(talk, 1, 0);
            cancel = Style.Button("Cancel", false); cancel.Dock = DockStyle.Fill; cancel.Enabled = false; cancel.Click += delegate { CancelWork(); }; voice.Controls.Add(cancel, 2, 0);
            Label shortcut = Style.Label("Hold Ctrl + Space while this window is active", 9, Style.Muted); shortcut.TextAlign = ContentAlignment.MiddleCenter; voice.Controls.Add(shortcut, 0, 1); voice.SetColumnSpan(shortcut, 3);
            root.Controls.Add(voice, 0, 5);
            status = Style.Label("Ready  •  Microphone off", 9, Style.Accent); root.Controls.Add(status, 0, 6);

            timer = new System.Windows.Forms.Timer { Interval = 60 };
            timer.Tick += async delegate { meter.Tick(recorder == null ? 0 : recorder.Level); if (recorder != null && (DateTime.UtcNow - recordingStarted).TotalSeconds >= 45) await StopRecording(); };
            timer.Start();
            // SuppressKeyPress also suppresses KeyUp in WinForms. Preserve KeyUp
            // so releasing Ctrl+Space always ends a hold-to-talk recording.
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Space) { e.Handled = true; StartRecording(); } else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelWork(); } };
            KeyPress += delegate(object sender, KeyPressEventArgs e) { if (recorder != null) e.Handled = true; };
            KeyUp += async delegate(object sender, KeyEventArgs e) { if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.ControlKey) && recorder != null) { e.Handled = true; await StopRecording(); } };
            Deactivate += delegate { if (recorder != null) CancelRecording(); };
            FormClosing += delegate { closed = true; CancelWork(); timer.Stop(); timer.Dispose(); StopPlayback(); };
            Style.PixelFonts(this);
            ResumeLayout(true);
        }
        private void AddExample(TableLayoutPanel parent, int column, string title, string caption, string command)
        {
            Button b = Style.Button(title + "\n" + caption, false); b.Dock = DockStyle.Fill; b.Font = new Font("Segoe UI", 9.5f);
            b.Click += delegate { if (!busy && recorder == null) { input.Text = command; input.Focus(); input.SelectionStart = input.Text.Length; } };
            parent.Controls.Add(b, column, 0);
        }
        private void UpdateConnection() { connection.Text = String.IsNullOrWhiteSpace(store.Key) ? "Your desktop voice assistant  /  Add a key to enable voice" : "Your desktop voice assistant  /  Online voice configured"; }
        private void ShowSettings() { if (busy || recorder != null) return; using (SettingsForm f = new SettingsForm(store)) { f.ShowDialog(this); } UpdateConnection(); }
        private void AddMessage(string role, string text)
        {
            if (closed) return;
            conversation.SelectionStart = conversation.TextLength;
            conversation.AppendText("\r\n  " + role + "\r\n");
            conversation.AppendText("  " + text.Replace("\r\n", "\n").Replace("\n", "\r\n  ") + "\r\n");
            conversation.ScrollToCaret();
        }
        private void SetBusy(bool value, string message)
        {
            busy = value;
            if (closed) return;
            input.Enabled = send.Enabled = talk.Enabled = settings.Enabled = notes.Enabled = !value;
            cancel.Enabled = value || player != null;
            cancel.Text = value ? "Cancel" : "Stop";
            status.Text = message;
        }
        private void StopPlayback()
        {
            if (player != null) { player.Stop(); player.Dispose(); player = null; }
            if (playback != null) { playback.Dispose(); playback = null; }
        }
        private void StartRecording()
        {
            if (busy || recorder != null || closed) return;
            StopPlayback();
            if (String.IsNullOrWhiteSpace(store.Key)) { ShowSettings(); return; }
            Recorder r = new Recorder();
            try
            {
                r.Start(); recorder = r; recordingStarted = DateTime.UtcNow;
                meter.Recording = true; talk.Text = "Listening... release to send";
                send.Enabled = input.Enabled = settings.Enabled = notes.Enabled = false; cancel.Enabled = true;
                status.Text = "Listening  •  Recording only while you hold";
            }
            catch (Exception ex) { r.Dispose(); AddMessage("MICROPHONE", ex.Message); status.Text = "Microphone unavailable  •  You can type instead"; }
        }
        private void CancelRecording()
        {
            Recorder r = recorder; recorder = null;
            if (r != null) r.Dispose();
            meter.Recording = false; talk.Text = "Hold to talk";
            SetBusy(false, "Recording discarded  •  Microphone off");
        }
        private async Task StopRecording()
        {
            if (recorder == null || closed) return;
            Recorder r = recorder; recorder = null; meter.Recording = false; talk.Text = "Hold to talk";
            byte[] wave;
            try { wave = r.Stop(); }
            catch (Exception ex) { AddMessage("MICROPHONE", ex.Message); SetBusy(false, "Microphone off"); return; }
            finally { r.Dispose(); }
            if (wave.Length < 6444) { SetBusy(false, "Hold a little longer and speak  •  Microphone off"); return; }
            // Skip near-silence to avoid hallucinated transcripts and needless API calls.
            double energy = 0;
            for (int i = 44; i + 1 < wave.Length; i += 2) { double value = (short)(wave[i] | (wave[i + 1] << 8)); energy += value * value; }
            if (Math.Sqrt(energy / Math.Max(1, (wave.Length - 44) / 2)) < 30) { SetBusy(false, "No speech detected  •  Check your microphone"); return; }
            await Work(async delegate(CancellationToken token)
            {
                status.Text = "Understanding your voice  •  Microphone off";
                using (Api api = new Api(store.Key))
                {
                    string text = await api.Transcribe(wave, token); token.ThrowIfCancellationRequested();
                    input.Text = text;
                    await Execute(text, token);
                }
            });
        }
        private async Task RunTyped()
        {
            if (busy || recorder != null || String.IsNullOrWhiteSpace(input.Text)) return;
            string text = input.Text.Trim(); input.Clear(); StopPlayback();
            await Work(token => Execute(text, token));
        }
        internal Task TestTyped(string text) { input.Text = text; return RunTyped(); }
        internal string ConversationText { get { return conversation.Text; } }
        private async Task Work(Func<CancellationToken, Task> action)
        {
            cancellation = new CancellationTokenSource();
            SetBusy(true, "Working  •  Microphone off");
            try { await action(cancellation.Token); if (!closed) status.Text = "Ready  •  Microphone off"; }
            catch (OperationCanceledException) { if (!closed) status.Text = cancellation.IsCancellationRequested ? "Cancelled  •  Microphone off" : "The request timed out  •  Please retry"; }
            catch (HttpRequestException) { AddMessage("CONNECTION", "Couldn't reach the voice service. Check your internet connection and try again."); if (!closed) status.Text = "Connection unavailable  •  Microphone off"; }
            catch (Exception ex) { AddMessage("FERRUM", ex.Message); if (!closed) status.Text = "Needs attention  •  Microphone off"; }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                if (!closed) { string message = status.Text; SetBusy(false, message); input.Focus(); }
            }
        }
        private async Task Execute(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); AddMessage("YOU", text);
            Command command = Commands.Parse(text);
            string answer;
            if (command.Kind == "note") { store.SaveNote(command.Text); answer = "Saved your note. You can find it in My notes."; }
            else if (command.Kind == "open")
            {
                string target = Commands.ResolveApp(command.Text, store.Preferences.Apps);
                try { Commands.Open(target); } catch { throw new InvalidOperationException("That app couldn't open. Check that it is installed, or add its executable in Settings."); }
                answer = "Opened " + command.Text + ".";
            }
            else if (command.Kind == "question")
            {
                status.Text = "Thinking  •  Microphone off";
                using (Api api = new Api(store.Key)) answer = await api.Answer(command.Text, token);
                token.ThrowIfCancellationRequested();
            }
            else return;
            AddMessage("FERRUM", answer);
            if (store.Preferences.Speak && !String.IsNullOrWhiteSpace(store.Key))
            {
                status.Text = "Preparing spoken reply  •  Microphone off";
                try
                {
                    using (Api api = new Api(store.Key))
                    {
                        byte[] sound = await api.Speak(answer, store.Preferences.Voice, token); token.ThrowIfCancellationRequested();
                        StopPlayback(); playback = new MemoryStream(sound); player = new SoundPlayer(playback); player.Load(); player.Play();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { AddMessage("VOICE", "The result is shown above, but spoken playback was unavailable."); }
            }
        }
        private void CancelWork() { if (recorder != null) CancelRecording(); if (cancellation != null) cancellation.Cancel(); StopPlayback(); if (!busy && !closed) SetBusy(false, "Ready  •  Microphone off"); }
    }

    public sealed class SettingsForm : Form
    {
        public SettingsForm(Store store)
        {
            SuspendLayout();
            Text = "Settings"; Size = new Size(640, 650); MinimumSize = new Size(640, 650); StartPosition = FormStartPosition.CenterParent;
            BackColor = Style.Background; ForeColor = Style.Text; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.None;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 12 };
            int[] heights = { 40, 65, 25, 40, 40, 34, 32, 28, 100, 45, 40, 45 };
            foreach (int h in heights) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            Controls.Add(root);
            root.Controls.Add(Style.Label("Make it yours", 20, Style.Text), 0, 0);
            root.Controls.Add(Style.Label("Voice and questions use your OpenAI API account. API usage is billed separately. Your audio is sent after you release the button; notes are saved on this PC.", 9.5f, Style.Muted), 0, 1);
            root.Controls.Add(Style.Label("OPENAI API KEY", 8.5f, Style.Muted), 0, 2);
            TextBox key = Style.Input(false); key.UseSystemPasswordChar = true; key.Text = store.Key; root.Controls.Add(key, 0, 3);
            FlowLayoutPanel keyActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            CheckBox show = new CheckBox { Text = "Show key", AutoSize = true, ForeColor = Style.Muted, Margin = new Padding(4, 10, 12, 0) }; show.CheckedChanged += delegate { key.UseSystemPasswordChar = !show.Checked; };
            Button get = Style.Button("Get an API key", false); get.Width = 145; get.Height = 32; get.Click += delegate { try { Commands.Open("https://platform.openai.com/api-keys"); } catch { MessageBox.Show("Open https://platform.openai.com/api-keys in your browser."); } };
            keyActions.Controls.Add(show); keyActions.Controls.Add(get); root.Controls.Add(keyActions, 0, 4);
            CheckBox speak = new CheckBox { Text = "Speak replies aloud (AI-generated voice)", Checked = store.Preferences.Speak, Dock = DockStyle.Fill }; root.Controls.Add(speak, 0, 5);
            FlowLayoutPanel voices = new FlowLayoutPanel { Dock = DockStyle.Fill };
            voices.Controls.Add(new Label { Text = "Voice", AutoSize = true, Margin = new Padding(4, 7, 15, 0) });
            ComboBox voice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, BackColor = Style.Surface, ForeColor = Style.Text };
            voice.Items.AddRange(new object[] { "coral", "alloy", "nova", "sage" }); voice.SelectedItem = store.Preferences.Voice; if (voice.SelectedIndex < 0) voice.SelectedIndex = 0;
            voices.Controls.Add(voice); root.Controls.Add(voices, 0, 6);
            root.Controls.Add(Style.Label("YOUR APPS  /  Built in: Notepad, Calculator, Paint, Files, Browser", 8.5f, Style.Muted), 0, 7);
            Dictionary<string, string> custom = new Dictionary<string, string>(store.Preferences.Apps);
            ListBox apps = new ListBox { Dock = DockStyle.Fill, BackColor = Style.Surface, ForeColor = Style.Text, BorderStyle = BorderStyle.None };
            Action refresh = delegate { apps.Items.Clear(); foreach (string app in custom.Keys.OrderBy(k => k)) apps.Items.Add(app); };
            refresh(); root.Controls.Add(apps, 0, 8);
            FlowLayoutPanel appActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            Button add = Style.Button("Add an app", false); add.Width = 125; add.Height = 34;
            add.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Title = "Choose the app executable", Filter = "Windows apps (*.exe)|*.exe", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        string name = AppNameDialog.Ask(this, Path.GetFileNameWithoutExtension(dialog.FileName));
                        if (!String.IsNullOrWhiteSpace(name)) { custom[Commands.NormalizeApp(name)] = dialog.FileName; refresh(); }
                    }
            };
            Button remove = Style.Button("Remove", false); remove.Width = 100; remove.Height = 34; remove.Click += delegate { if (apps.SelectedItem != null) { custom.Remove(apps.SelectedItem.ToString()); refresh(); } };
            appActions.Controls.Add(add); appActions.Controls.Add(remove); root.Controls.Add(appActions, 0, 9);
            root.Controls.Add(Style.Label("Your key is encrypted for your Windows account. Clear it and save to disconnect. Recordings are kept in memory only.", 9, Style.Muted), 0, 10);
            Button save = Style.Button("Save settings", true); save.Dock = DockStyle.Fill;
            save.Click += delegate
            {
                try { store.Save(new Preferences { Speak = speak.Checked, Voice = voice.SelectedItem.ToString(), Apps = custom }, key.Text); DialogResult = DialogResult.OK; Close(); }
                catch { MessageBox.Show(this, "Settings couldn't be saved. Check that your Windows user can write to the app data folder.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            root.Controls.Add(save, 0, 11); AcceptButton = save;
            Style.PixelFonts(this);
            ResumeLayout(true);
        }
    }

    public sealed class AppNameDialog : Form
    {
        public static string Ask(IWin32Window owner, string suggested)
        {
            using (AppNameDialog f = new AppNameDialog())
            {
                f.Text = "Name your app"; f.ClientSize = new Size(390, 145); f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.BackColor = Style.Background; f.ForeColor = Style.Text; f.MinimizeBox = f.MaximizeBox = false;
                Label label = Style.Label("What should you say after “open”?", 11, Style.Text); label.Dock = DockStyle.None; label.SetBounds(15, 10, 355, 30); f.Controls.Add(label);
                TextBox input = Style.Input(false); input.Dock = DockStyle.None; input.SetBounds(15, 47, 355, 30); input.Text = suggested; f.Controls.Add(input);
                Button ok = Style.Button("Add app", true); ok.SetBounds(250, 92, 120, 35); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok); f.AcceptButton = ok;
                f.AutoScaleMode = AutoScaleMode.None; Style.PixelFonts(f);
                return f.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
    }

    public sealed class NotesForm : Form
    {
        public NotesForm(Store store)
        {
            SuspendLayout();
            Text = "My notes"; Size = new Size(800, 560); MinimumSize = new Size(680, 450); StartPosition = FormStartPosition.CenterParent; BackColor = Style.Background; ForeColor = Style.Text; AutoScaleMode = AutoScaleMode.None;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); Controls.Add(root);
            root.Controls.Add(Style.Label("Saved on this PC", 18, Style.Text), 0, 0);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Style.Background, SplitterDistance = 240 }; root.Controls.Add(split, 0, 1);
            ListBox list = new ListBox { Dock = DockStyle.Fill, BackColor = Style.Surface, ForeColor = Style.Text, Font = new Font("Segoe UI", 10), BorderStyle = BorderStyle.None, HorizontalScrollbar = true };
            TextBox view = Style.Input(true); view.ReadOnly = true; view.ScrollBars = ScrollBars.Both; split.Panel1.Controls.Add(list); split.Panel2.Controls.Add(view);
            string[] paths = store.Notes();
            foreach (string path in paths) list.Items.Add(Path.GetFileNameWithoutExtension(path).Substring(0, 19).Replace('_', ' '));
            list.SelectedIndexChanged += delegate { if (list.SelectedIndex >= 0) try { view.Text = File.ReadAllText(paths[list.SelectedIndex]); } catch { view.Text = "This note could not be read."; } };
            if (paths.Length > 0) list.SelectedIndex = 0; else view.Text = "No notes yet. Try: save a note Buy milk tomorrow.";
            Button folder = Style.Button("Open notes folder", false); folder.Dock = DockStyle.Fill; folder.Click += delegate { try { Directory.CreateDirectory(store.NoteDirectory); Commands.Open(store.NoteDirectory); } catch { MessageBox.Show("The notes folder could not be opened."); } }; root.Controls.Add(folder, 0, 2);
            Shown += delegate { split.SplitterDistance = (int)(split.Width * .32); };
            Style.PixelFonts(this);
            ResumeLayout(true);
        }
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Handle(request); }
    }

    internal static class SelfTest
    {
        private static readonly List<string> results = new List<string>();
        private static void Assert(bool pass, string name) { if (!pass) throw new Exception("FAILED: " + name); results.Add("PASS: " + name); }
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            try
            {
                Assert(Commands.Parse("OPEN Notepad.").Kind == "open" && Commands.Parse("OPEN Notepad.").Text == "Notepad", "app command and punctuation");
                Assert(Commands.Parse("save a note: Buy milk.\nCall Alex.").Text == "Buy milk.\nCall Alex.", "note payload and line breaks preserved");
                Assert(Commands.Parse("save note").Kind == "note" && Commands.Parse("save note").Text == "", "empty note recognized");
                Assert(Commands.Parse("noteworthy news").Kind == "question", "question is not mistaken for a note");
                Assert(Commands.Parse("Why is the sky blue?").Kind == "question", "question route");
                Assert(Commands.Parse("ask: Explain gravity").Text == "Explain gravity", "question prefix");
                Store data = new Store(Path.Combine(folder, "test-data-" + Guid.NewGuid().ToString("N")));
                string note = data.SaveNote("A note with Unicode: café, नमस्ते.");
                Assert(File.ReadAllText(note).Contains("café, नमस्ते."), "note writes and reloads Unicode");
                Assert(data.Notes().Length >= 1, "saved notes discovery");
                bool rejected = false; try { data.SaveNote(" "); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "empty note cannot be saved");
                rejected = false; try { Commands.ResolveApp("cmd /c something", new Dictionary<string, string>()); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "unregistered command cannot launch a shell");
                Assert(Commands.ResolveApp("calculator", new Dictionary<string, string>()).EndsWith("calc.exe"), "built-in app resolution");
                rejected = false; try { Commands.ResolveApp("test", new Dictionary<string, string> { { "test", "relative.exe" } }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "custom launch target must be an existing absolute executable");
                string key = "sk-test-" + Guid.NewGuid().ToString("N"); data.Save(new Preferences { Speak = false, Voice = "nova" }, key);
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(data.Root, "key.dat"))).Contains(key), "API key encrypted at rest");
                Store loaded = new Store(data.Root); Assert(loaded.Key == key && loaded.Preferences.Voice == "nova" && !loaded.Preferences.Speak, "settings and protected key round trip");
                loaded.Save(loaded.Preferences, ""); Assert(!File.Exists(Path.Combine(data.Root, "key.dat")), "disconnect removes saved key");
                byte[] wav = Recorder.Wave(new byte[32000]);
                Assert(wav.Length == 32044 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt32(wav, 40) == 32000, "valid mono 16 kHz PCM WAV");
                Assert(Api.ReadAnswer("{\"output\":[{\"type\":\"reasoning\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"First\"},{\"type\":\"output_text\",\"text\":\"Second\"}]}]}") == "First\r\nSecond", "response parser handles multiple text parts and reasoning");
                Assert(Api.ReadAnswer("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"Cannot help\"}]}]}") == "Cannot help", "refusal is displayed");
                ApiTests(wav).GetAwaiter().GetResult();
                using (MainForm main = new MainForm(new Store(Path.Combine(folder, "preview-data"))))
                {
                    main.Show(); Application.DoEvents();
                    using (Graphics g = main.CreateGraphics()) results.Add("INFO: Window DPI: " + g.DpiX + "; pixel fonts keep control labels within their bounds.");
                    using (Bitmap bitmap = new Bitmap(main.Width, main.Height)) { main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "app-preview.png")); }
                    main.TestTyped("save a note: Remember the meeting at 10.").GetAwaiter().GetResult();
                    Assert(main.ConversationText.Contains("Saved your note.") && new Store(Path.Combine(folder, "preview-data")).Notes().Length > 0, "typed command reaches UI and persists a note");
                    main.TestTyped("open unknown-app-12345").GetAwaiter().GetResult();
                    Assert(main.ConversationText.Contains("I don't have that app yet"), "unknown app produces visible guidance");
                    main.TestTyped("What is gravity?").GetAwaiter().GetResult();
                    Assert(main.ConversationText.Contains("add your OpenAI API key"), "question without a key produces visible setup guidance");
                    main.Close();
                }
                using (SettingsForm settings = new SettingsForm(new Store(Path.Combine(folder, "preview-data"))))
                {
                    settings.Show(); Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "settings-preview.png")); }
                    settings.Close();
                }
                using (NotesForm notes = new NotesForm(data))
                {
                    notes.Show(); Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(notes.Width, notes.Height)) { notes.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "notes-preview.png")); }
                    notes.Close();
                }
                results.Add("INFO: Microphone devices reported: " + Recorder.DeviceCount + ". Live recording and paid API calls are not part of this test.");
                results.Add("PASS: Main window, settings, and notes rendered successfully");
                File.WriteAllLines(Path.Combine(folder, "test-results.txt"), results); return 0;
            }
            catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(folder, "test-results.txt"), results); return 1; }
        }
        private static async Task ApiTests(byte[] wav)
        {
            FakeHandler handler = new FakeHandler();
            int calls = 0;
            handler.Handle = async request =>
            {
                Assert(request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-key", "API authorization header");
                string path = request.RequestUri.AbsolutePath;
                byte[] body = await request.Content.ReadAsByteArrayAsync();
                if (path == "/v1/audio/transcriptions")
                {
                    string multipart = Encoding.UTF8.GetString(body);
                    Assert(multipart.Contains("gpt-4o-mini-transcribe") && multipart.Contains("audio/wav") && multipart.Contains("speech.wav"), "multipart transcription includes WAV and model");
                    calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"open notepad\"}") };
                }
                if (path == "/v1/responses")
                {
                    Dictionary<string, object> parsed = Store.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                    Assert(parsed["input"].ToString() == "What is gravity?" && Object.Equals(parsed["store"], false), "answer request preserves question and disables response storage");
                    calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Gravity is attraction.\"}]}]}") };
                }
                Dictionary<string, object> speech = Store.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                Assert(path == "/v1/audio/speech" && speech["response_format"].ToString() == "wav" && speech["voice"].ToString() == "coral", "speech request uses selected voice and playable WAV");
                calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
            };
            using (Api api = new Api("test-key", handler))
            {
                Assert(await api.Transcribe(wav, CancellationToken.None) == "open notepad", "transcription round trip using mock service");
                Assert(await api.Answer("What is gravity?", CancellationToken.None) == "Gravity is attraction.", "answer round trip using mock service");
                Assert((await api.Speak("Hello", "coral", CancellationToken.None)).Length == wav.Length, "speech round trip using mock service");
            }
            Assert(calls == 3, "three expected API requests");
            FakeHandler denied = new FakeHandler { Handle = request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)) };
            using (Api api = new Api("test-key", denied))
            {
                bool rejected = false; try { await api.Answer("test", CancellationToken.None); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("key was rejected"); }
                Assert(rejected, "invalid key produces helpful error without exposing the key");
            }
            FakeHandler cancelled = new FakeHandler { Handle = request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) };
            using (Api api = new Api("test-key", cancelled))
            using (CancellationTokenSource token = new CancellationTokenSource())
            {
                token.Cancel(); bool stopped = false;
                try { await api.Answer("test", token.Token); } catch (OperationCanceledException) { stopped = true; }
                Assert(stopped, "cancelled request stops before calling the service");
            }
        }
    }
}

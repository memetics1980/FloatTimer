using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace FloatTimer {
    public sealed class Mp3Playback : IDisposable {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        static extern uint Send(string command, StringBuilder result, int length, IntPtr callback);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciGetErrorStringW")]
        static extern bool ErrorText(uint error, StringBuilder text, int length);
        readonly string alias = "ft" + Guid.NewGuid().ToString("N");
        bool opened;
        public string TempFilePath { get; private set; }
        public int DurationMilliseconds { get; private set; }
        public Mp3Playback(byte[] bytes) {
            CheckHeader(bytes);
            TempFilePath = Path.Combine(Path.GetTempPath(), "FloatTimer-" + Guid.NewGuid().ToString("N") + ".mp3");
            try {
                File.WriteAllBytes(TempFilePath, bytes);
                Command("open \"" + TempFilePath + "\" type mpegvideo alias " + alias); opened = true;
                Command("set " + alias + " time format milliseconds");
                int duration;
                if (!int.TryParse(Command("status " + alias + " length"), out duration) || duration <= 0) throw new InvalidDataException("无法读取此 MP3 的时长，请重新导出音频。");
                if (duration > 120000) throw new InvalidDataException("提示音请控制在 120 秒以内。");
                DurationMilliseconds = duration;
            } catch { Dispose(); throw; }
        }
        static string Command(string command) {
            var result = new StringBuilder(256); uint error = Send(command, result, result.Capacity, IntPtr.Zero);
            if (error != 0) { var message = new StringBuilder(256); ErrorText(error, message, message.Capacity); throw new InvalidDataException("MP3 无法播放：" + message + " 请确认音频有效且 Windows 能播放 MP3。"); }
            return result.ToString();
        }
        public static void CheckHeader(byte[] bytes) {
            if (bytes == null || bytes.Length < 24 || bytes.Length > WavFiles.MaxBytes) throw new InvalidDataException("MP3 文件为空、无效或超过 10 MB。");
            int start = 0;
            if (bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3') {
                if (bytes.Length < 10 || bytes.Skip(6).Take(4).Any(v => v >= 128)) throw new InvalidDataException("MP3 标签数据损坏。");
                start = 10 + (bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9]);
                if (bytes[3] == 4 && (bytes[5] & 16) != 0) start += 10;
            }
            for (int i = start; i < Math.Min(bytes.Length - 3L, (long)start + 65536); i++) {
                if (bytes[i] == 255 && (bytes[i + 1] & 224) == 224 && ((bytes[i + 1] >> 3) & 3) != 1 && ((bytes[i + 1] >> 1) & 3) == 1 && (bytes[i + 2] >> 4) > 0 && (bytes[i + 2] >> 4) < 15 && ((bytes[i + 2] >> 2) & 3) != 3) return;
            }
            throw new InvalidDataException("不是有效的 MP3 音频，请勿仅修改文件扩展名。");
        }
        public void Play() { if (!opened) throw new ObjectDisposedException("Mp3Playback"); Command("play " + alias + " from 0"); }
        public string Mode { get { return opened ? Command("status " + alias + " mode") : "closed"; } }
        public void Dispose() {
            if (opened) { Send("stop " + alias, null, 0, IntPtr.Zero); Send("close " + alias, null, 0, IntPtr.Zero); opened = false; }
            if (TempFilePath != null) { try { File.Delete(TempFilePath); } catch { } TempFilePath = null; }
        }
    }
    public static class WavFiles {
        public const int MaxBytes = 10 * 1024 * 1024;
        public static byte[] Read(string path) {
            if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 WAV 文件。");
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new InvalidDataException("WAV 文件不能超过 10 MB。");
            return Prepare(File.ReadAllBytes(path));
        }
        static string Tag(byte[] b, int p) { return Encoding.ASCII.GetString(b, p, 4); }
        public static byte[] Prepare(byte[] b) {
            if (b == null || b.Length < 44 || b.Length > MaxBytes || Tag(b, 0) != "RIFF" || Tag(b, 8) != "WAVE") throw new InvalidDataException("不是有效的 WAV 音频文件。");
            long end = (long)BitConverter.ToUInt32(b, 4) + 8;
            if (end > b.Length || end < 44) throw new InvalidDataException("WAV 文件不完整，请重新导出。");
            int fmt = -1, fmtSize = 0, data = -1, bytes = 0;
            for (long p = 12; p + 8 <= end;) {
                uint size = BitConverter.ToUInt32(b, (int)p + 4); long next = p + 8 + size;
                if (next > end) throw new InvalidDataException("WAV 数据块损坏。");
                string tag = Tag(b, (int)p);
                if (tag == "fmt " && fmt < 0) { fmt = (int)p + 8; fmtSize = (int)size; }
                if (tag == "data" && data < 0) { data = (int)p + 8; bytes = (int)size; }
                p = next + (size % 2);
            }
            if (fmt < 0 || fmtSize < 16 || data < 0 || bytes == 0) throw new InvalidDataException("WAV 中没有可播放的音频数据。");
            int format = BitConverter.ToUInt16(b, fmt), channels = BitConverter.ToUInt16(b, fmt + 2), bits = BitConverter.ToUInt16(b, fmt + 14);
            uint rate = BitConverter.ToUInt32(b, fmt + 4); int align = BitConverter.ToUInt16(b, fmt + 12);
            if (format == 65534) {
                if (fmtSize < 40 || BitConverter.ToUInt16(b, fmt + 16) < 22) throw new InvalidDataException("WAV 扩展格式不完整。");
                byte[] guidTail = { 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113 };
                if (!b.Skip(fmt + 26).Take(14).SequenceEqual(guidTail)) throw new InvalidDataException("请将此 WAV 导出为 PCM 或浮点格式后重试。");
                format = BitConverter.ToUInt16(b, fmt + 24);
            }
            if ((channels != 1 && channels != 2) || rate < 8000 || rate > 192000) throw new InvalidDataException("请选择单声道或双声道、采样率 8–192 kHz 的 WAV。");
            if ((format != 1 || (bits != 8 && bits != 16 && bits != 24 && bits != 32)) && (format != 3 || (bits != 32 && bits != 64))) throw new InvalidDataException("请使用未压缩 PCM 或浮点 WAV；不支持压缩编码的 WAV。");
            if (align != channels * (bits / 8) || bytes % align != 0) throw new InvalidDataException("WAV 的采样数据不完整。");
            int frames = bytes / align;
            if ((double)frames / rate > 120) throw new InvalidDataException("提示音请控制在 120 秒以内。");
            if (format == 1 && bits == 16 && BitConverter.ToUInt16(b, fmt) == 1) return b;
            // Convert uncommon PCM/float encodings to PCM16 without changing rate or channels.
            using (var output = new MemoryStream()) using (var writer = new BinaryWriter(output)) {
                int size = frames * channels * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write((int)rate); writer.Write((int)rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
                for (int p = data; p < data + bytes; p += bits / 8) {
                    double sample;
                    if (format == 3) sample = bits == 32 ? BitConverter.ToSingle(b, p) : BitConverter.ToDouble(b, p);
                    else if (bits == 8) sample = (b[p] - 128) / 128.0;
                    else if (bits == 16) sample = BitConverter.ToInt16(b, p) / 32768.0;
                    else if (bits == 24) { int v = b[p] | b[p + 1] << 8 | b[p + 2] << 16; if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); sample = v / 8388608.0; }
                    else sample = BitConverter.ToInt32(b, p) / 2147483648.0;
                    if (double.IsNaN(sample) || double.IsInfinity(sample)) throw new InvalidDataException("WAV 包含无效的音频采样。");
                    writer.Write((short)Math.Round(Math.Max(-32768, Math.Min(32767, sample * 32768))));
                }
                writer.Flush(); byte[] result = output.ToArray(); if (result.Length > MaxBytes) throw new InvalidDataException("转换后的音频超过 10 MB，请缩短音频后重试。"); return result;
            }
        }
    }

    [DataContract] public sealed class CustomSound {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string WaveBase64;
        [DataMember(EmitDefaultValue = false)] public string Format;
        public bool IsMp3 { get { return string.Equals(Format, "mp3", StringComparison.OrdinalIgnoreCase); } }
        public CustomSound Copy() { return (CustomSound)MemberwiseClone(); }
        public byte[] ReadWave() {
            if (IsMp3) throw new InvalidDataException("此音频为 MP3，请使用 MP3 播放。");
            return WavFiles.Prepare(ReadAudio());
        }
        public byte[] ReadAudio() {
            if (string.IsNullOrEmpty(WaveBase64) || WaveBase64.Length > (WavFiles.MaxBytes + 2L) / 3 * 4) throw new InvalidDataException("自定义提示音不可用，请重新选择音频文件。");
            byte[] bytes = Convert.FromBase64String(WaveBase64); if (IsMp3) Mp3Playback.CheckHeader(bytes); return bytes;
        }
    }
    [DataContract] public sealed class ReminderCue {
        [DataMember] public int Seconds;
        [DataMember] public int Tone;
        [DataMember(EmitDefaultValue = false)] public string CustomSoundId;
        public ReminderCue Copy() { return (ReminderCue)MemberwiseClone(); }
    }
    public sealed class SoundSelection {
        public bool Inherit;
        public int Tone;
        public string CustomSoundId;
        public SoundSelection Copy() { return (SoundSelection)MemberwiseClone(); }
    }
    public sealed class SoundLibrary {
        public readonly List<CustomSound> Sounds;
        public event Action Changed;
        public SoundLibrary(IEnumerable<CustomSound> sounds) { Sounds = (sounds ?? new CustomSound[0]).Where(s => s != null).Select(s => s.Copy()).ToList(); }
        public CustomSound Find(string id) { return Sounds.FirstOrDefault(s => s.Id == id); }
        public CustomSound Import(string path) {
            string extension = Path.GetExtension(path).ToLowerInvariant(); byte[] bytes;
            if (extension == ".mp3") {
                if (new FileInfo(path).Length > WavFiles.MaxBytes) throw new InvalidDataException("MP3 文件不能超过 10 MB。");
                bytes = File.ReadAllBytes(path); using (var test = new Mp3Playback(bytes)) { }
            } else if (extension == ".wav") bytes = WavFiles.Read(path);
            else throw new InvalidDataException("请选择 WAV 或 MP3 音频文件。");
            string id;
            using (var sha = SHA256.Create()) id = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            var existing = Find(id); if (existing != null) return existing;
            long total = Sounds.Sum(s => (long)(s.WaveBase64 ?? "").Length) + ((bytes.Length + 2L) / 3 * 4);
            if (total > 44L * 1024 * 1024) throw new InvalidDataException("本计时器的自定义提示音已较多，请选择更短的音频文件。");
            var sound = new CustomSound { Id = id, Name = Path.GetFileName(path), WaveBase64 = Convert.ToBase64String(bytes), Format = extension == ".mp3" ? "mp3" : null }; Sounds.Add(sound);
            if (Changed != null) Changed(); return sound;
        }
    }

    public sealed class SoundPicker : Grid {
        sealed class Option {
            public string Label { get; set; }
            public SoundSelection Selection;
        }
        readonly SoundLibrary library;
        readonly AlarmAudio audio;
        readonly bool completion, allowInherit;
        readonly Func<SoundSelection> inheritedSound;
        public readonly ComboBox Choice;
        public readonly Button ImportButton, PreviewButton;
        public readonly TextBlock Status;
        public SoundSelection Selected { get { var option = Choice.SelectedItem as Option; return option == null ? new SoundSelection { Inherit = allowInherit } : option.Selection.Copy(); } }
        public SoundPicker(SoundLibrary library, AlarmAudio audio, bool completion, bool allowInherit, string name, string previewLabel, SoundSelection initial, Func<SoundSelection> inheritedSound = null) {
            this.library = library; this.audio = audio; this.completion = completion; this.allowInherit = allowInherit; this.inheritedSound = inheritedSound;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Choice = new ComboBox { Name = name, FontSize = 13, Padding = new Thickness(7, 7, 7, 7), DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 6, 0) }; Children.Add(Choice);
            ImportButton = Ui.Button("选音频…", false, ChooseFile); ImportButton.Name = name + "Import"; ImportButton.Padding = new Thickness(9, 6, 9, 6); ImportButton.Margin = new Thickness(0, 0, 6, 0); Grid.SetColumn(ImportButton, 1); Children.Add(ImportButton);
            PreviewButton = Ui.Button(previewLabel, false, delegate { Preview(); }); PreviewButton.Name = name + "Preview"; PreviewButton.Padding = new Thickness(9, 6, 9, 6); Grid.SetColumn(PreviewButton, 2); Children.Add(PreviewButton);
            Status = new TextBlock { Name = name + "Status", FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed }; Grid.SetRow(Status, 1); Grid.SetColumnSpan(Status, 3); Children.Add(Status);
            Refresh(initial ?? new SoundSelection { Inherit = allowInherit });
            Choice.SelectionChanged += delegate { Choice.ToolTip = (Choice.SelectedItem as Option) == null ? null : ((Option)Choice.SelectedItem).Label; Status.Visibility = Visibility.Collapsed; };
            library.Changed += delegate { Refresh(Selected); };
        }
        void Refresh(SoundSelection selected) {
            var options = new List<Option>();
            if (allowInherit) options.Add(new Option { Label = "沿用默认分段音", Selection = new SoundSelection { Inherit = true } });
            string[] names = completion ? AlarmAudio.CompletionToneNames : AlarmAudio.ReminderToneNames;
            for (int i = 0; i < names.Length; i++) options.Add(new Option { Label = names[i], Selection = new SoundSelection { Tone = i } });
            foreach (var sound in library.Sounds) options.Add(new Option { Label = "自定义：" + sound.Name, Selection = new SoundSelection { CustomSoundId = sound.Id } });
            Option active = options.FirstOrDefault(o => Same(o.Selection, selected));
            if (active == null && !string.IsNullOrEmpty(selected.CustomSoundId)) { active = new Option { Label = "自定义音频不可用，请重新选择", Selection = selected.Copy() }; options.Add(active); }
            Choice.ItemsSource = options; Choice.SelectedItem = active ?? options[0];
        }
        static bool Same(SoundSelection a, SoundSelection b) { return a.Inherit == b.Inherit && (a.Inherit || (!string.IsNullOrEmpty(a.CustomSoundId) ? a.CustomSoundId == b.CustomSoundId : string.IsNullOrEmpty(b.CustomSoundId) && a.Tone == b.Tone)); }
        public void Select(int tone, string id, bool inherit = false) { Refresh(new SoundSelection { Tone = tone, CustomSoundId = id, Inherit = inherit }); }
        public void EnableSound(bool value) { Choice.IsEnabled = ImportButton.IsEnabled = PreviewButton.IsEnabled = value; }
        void Message(string text, bool failed) { Status.Text = text; Status.Foreground = failed ? Ui.Brush("#FFAE9B") : Ui.Green; Status.Visibility = Visibility.Visible; }
        public bool ImportFile(string path) {
            try { var sound = library.Import(path); Select(0, sound.Id); Message("已载入：" + sound.Name + "；保存设置后随计时器保存。", false); return true; }
            catch (Exception ex) { Message("无法导入：" + ex.Message, true); return false; }
        }
        void ChooseFile() {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择本地提示音", Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3|WAV 音频 (*.wav)|*.wav|MP3 音频 (*.mp3)|*.mp3", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) ImportFile(dialog.FileName);
        }
        public bool Preview() {
            var selected = Selected; if (selected.Inherit && inheritedSound != null) selected = inheritedSound();
            string error; bool ok;
            if (!string.IsNullOrEmpty(selected.CustomSoundId)) ok = audio.TryPlayCustom(library.Find(selected.CustomSoundId), completion, out error);
            else ok = completion ? audio.TryPlay(selected.Tone, out error) : audio.TryPlayReminder(selected.Tone, out error);
            Message(ok ? "正在试听。" : error, !ok); return ok;
        }
    }
}

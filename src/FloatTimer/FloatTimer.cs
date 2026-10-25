using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: System.Reflection.AssemblyTitle("浮光计时")]
[assembly: System.Reflection.AssemblyProduct("FloatTimer")]
[assembly: System.Reflection.AssemblyCompany("金波")]
[assembly: System.Reflection.AssemblyVersion("1.0.22.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.22.0")]

namespace FloatTimer {
    public sealed class AlarmAudio : IDisposable {
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
        readonly GCHandle[] buffers = new GCHandle[5];
        GCHandle customBuffer;
        string customBufferKey;
        Mp3Playback mp3Player;
        string mp3BufferKey;
        public static readonly string[] ReminderToneNames = { "短嘀一声", "前台铃 · 叮", "双音叮咚", "柔和木敲" };
        public static readonly string[] CompletionToneNames = { "三秒提示铃", "短嘀一声", "前台铃 · 叮", "双音叮咚", "柔和木敲" };
        bool disposed;
        public int Requests { get; private set; }
        public int Accepted { get; private set; }
        public int ReminderRequests { get; private set; }
        public int CompletionRequests { get; private set; }
        public int LastReminderTone { get; private set; }
        public int LastCompletionTone { get; private set; }
        public string LastReminderCustomId { get; private set; }
        public string LastCompletionCustomId { get; private set; }
        public bool TryPlay(out string error) { return TryPlay(0, out error); }
        public bool TryPlay(int tone, out string error) { tone = ValidCompletionTone(tone); CompletionRequests++; LastCompletionTone = tone; LastCompletionCustomId = null; return TryPlayCore(tone == 0 ? 4 : tone - 1, out error); }
        public bool TryPlayReminder(out string error) { return TryPlayReminder(0, out error); }
        public bool TryPlayReminder(int tone, out string error) { tone = ValidReminderTone(tone); ReminderRequests++; LastReminderTone = tone; LastReminderCustomId = null; return TryPlayCore(tone, out error); }
        public bool TryPlayReminder(TimerData data, int seconds, out string error) {
            var cue = data.ReminderCues == null ? null : data.ReminderCues.FirstOrDefault(c => c.Seconds == seconds);
            string id = cue == null ? data.ReminderCustomSoundId : cue.CustomSoundId;
            return string.IsNullOrEmpty(id) ? TryPlayReminder(cue == null ? data.ReminderTone : cue.Tone, out error) : TryPlayCustom(data.FindSound(id), false, out error);
        }
        public bool TryPlay(TimerData data, out string error) { return string.IsNullOrEmpty(data.CompletionCustomSoundId) ? TryPlay(data.CompletionTone, out error) : TryPlayCustom(data.FindSound(data.CompletionCustomSoundId), true, out error); }
        public bool TryPlayCustom(CustomSound sound, bool completion, out string error) {
            Requests++; error = null;
            if (completion) { CompletionRequests++; LastCompletionTone = -1; LastCompletionCustomId = sound == null ? null : sound.Id; }
            else { ReminderRequests++; LastReminderTone = -1; LastReminderCustomId = sound == null ? null : sound.Id; }
            try {
                if (disposed) throw new ObjectDisposedException("AlarmAudio");
                if (sound == null) throw new InvalidDataException("自定义提示音不可用，请在设置中重新选择音频。");
                if (sound.IsMp3) {
                    if (mp3Player == null || mp3BufferKey != sound.WaveBase64) { StopMp3(); mp3Player = new Mp3Playback(sound.ReadAudio()); mp3BufferKey = sound.WaveBase64; }
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0); mp3Player.Play(); Accepted++; return true;
                }
                StopMp3();
                if (!customBuffer.IsAllocated || customBufferKey != sound.WaveBase64) {
                    byte[] wave = sound.ReadWave();
                    if (customBuffer.IsAllocated) { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); customBuffer.Free(); customBufferKey = null; }
                    customBuffer = GCHandle.Alloc(wave, GCHandleType.Pinned); customBufferKey = sound.WaveBase64;
                }
                if (!PlaySound(customBuffer.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004)) throw new InvalidOperationException("请检查 Windows 输出设备及应用音量。");
                Accepted++; return true;
            } catch (Exception ex) { error = "自定义提示音未能播放：" + ex.Message; return false; }
        }
        public static int ValidReminderTone(int tone) { return tone >= 0 && tone < ReminderToneNames.Length ? tone : 0; }
        public static int ValidCompletionTone(int tone) { return tone >= 0 && tone < CompletionToneNames.Length ? tone : 0; }
        bool TryPlayCore(int tone, out string error) {
            Requests++; error = null;
            try {
                if (disposed) throw new ObjectDisposedException("AlarmAudio");
                StopMp3();
                if (!buffers[tone].IsAllocated) buffers[tone] = GCHandle.Alloc(tone == 4 ? CreateWave() : CreateReminderWave(tone), GCHandleType.Pinned);
                // Play an actual PCM buffer in this application's audio session. Do not
                // use a Windows event alias or route to the System Sounds mixer session.
                // Keep the buffer pinned until asynchronous playback has been stopped.
                if (!PlaySound(buffers[tone].AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004)) {
                    error = "提示音未能播放，请检查 Windows 声音输出设备及应用音量。"; return false;
                }
                Accepted++; return true;
            } catch (Exception ex) { error = "提示音未能播放：" + ex.Message; return false; }
        }
        public static byte[] CreateWave() {
            const int rate = 44100, samples = rate * 3;
            using (var stream = new MemoryStream(44 + samples * 2)) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(new byte[] { 82, 73, 70, 70 }); writer.Write(36 + samples * 2);
                    writer.Write(new byte[] { 87, 65, 86, 69, 102, 109, 116, 32 });
                    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                    writer.Write(new byte[] { 100, 97, 116, 97 }); writer.Write(samples * 2);
                    for (int i = 0; i < samples; i++) {
                        double t = (double)i / rate, phase = t % 1.0, value = 0;
                        for (int note = 0; note < 2; note++) {
                            double u = phase - note * .28, length = note == 0 ? .25 : .58;
                            if (u < 0 || u > length) continue;
                            double frequency = note == 0 ? 1046.502 : 1318.510;
                            double envelope = Math.Min(1, u / .008) * Math.Pow(1 - u / length, 1.6);
                            value += .72 * envelope * (.82 * Math.Sin(2 * Math.PI * frequency * u) + .18 * Math.Sin(4 * Math.PI * frequency * u));
                        }
                        writer.Write((short)(Math.Max(-1, Math.Min(1, value)) * short.MaxValue));
                    }
                    writer.Flush(); return stream.ToArray();
                }
            }
        }
        public void Dispose() {
            if (disposed) return; disposed = true;
            if (buffers.Any(b => b.IsAllocated) || customBuffer.IsAllocated) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            for (int i = 0; i < buffers.Length; i++) if (buffers[i].IsAllocated) buffers[i].Free();
            if (customBuffer.IsAllocated) customBuffer.Free(); customBufferKey = null;
            StopMp3();
        }
        void StopMp3() { if (mp3Player != null) mp3Player.Dispose(); mp3Player = null; mp3BufferKey = null; }
        public void Stop() { if (!disposed && (buffers.Any(b => b.IsAllocated) || customBuffer.IsAllocated)) PlaySound(IntPtr.Zero, IntPtr.Zero, 0); StopMp3(); }
        // The user-supplied bell WAV is embedded unchanged; other cues are synthesized locally.
        // No external sound files or network access are needed at runtime.
        public static byte[] CreateReminderWave(int tone) {
            tone = ValidReminderTone(tone);
            if (tone == 0) return CreateReminderWave();
            if (tone == 1) {
                using (var source = typeof(AlarmAudio).Assembly.GetManifestResourceStream("FloatTimer.DeskBell.wav")) {
                    if (source == null) throw new InvalidOperationException("内置前台铃音效缺失，请重新下载完整程序。");
                    using (var result = new MemoryStream()) { source.CopyTo(result); return result.ToArray(); }
                }
            }
            const int rate = 44100;
            double length = tone == 2 ? 1.05 : .4;
            int samples = (int)(rate * length);
            using (var stream = new MemoryStream(44 + samples * 2)) using (var writer = new BinaryWriter(stream)) {
                writer.Write(new byte[] { 82, 73, 70, 70 }); writer.Write(36 + samples * 2); writer.Write(new byte[] { 87, 65, 86, 69, 102, 109, 116, 32 });
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(new byte[] { 100, 97, 116, 97 }); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) {
                    double t = (double)i / rate, value = 0;
                    if (tone == 2) {
                        for (int note = 0; note < 2; note++) {
                            double u = t - note * .28; if (u < 0) continue;
                            double f = note == 0 ? 1318.51 : 1046.5;
                            value += .58 * Math.Min(1, u / .003) * Math.Exp(-u / .17) * (.85 * Math.Sin(2 * Math.PI * f * u) + .15 * Math.Sin(4 * Math.PI * f * u));
                        }
                    } else {
                        value = .8 * Math.Min(1, t / .002) * Math.Exp(-t / .045) * (.7 * Math.Sin(2 * Math.PI * 740 * t) + .3 * Math.Sin(2 * Math.PI * 1190 * t));
                    }
                    value *= Math.Min(1, Math.Max(0, (length - t) / .06));
                    writer.Write((short)(Math.Max(-1, Math.Min(1, value)) * short.MaxValue));
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        public static byte[] CreateReminderWave() {
            const int rate = 44100, samples = rate / 5;
            using (var stream = new MemoryStream(44 + samples * 2)) using (var writer = new BinaryWriter(stream)) {
                writer.Write(new byte[] { 82, 73, 70, 70 }); writer.Write(36 + samples * 2); writer.Write(new byte[] { 87, 65, 86, 69, 102, 109, 116, 32 });
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(new byte[] { 100, 97, 116, 97 }); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) { double t = (double)i / rate, envelope = Math.Min(1, t / .008) * Math.Min(1, (.2 - t) / .035); writer.Write((short)(.65 * Math.Max(0, envelope) * Math.Sin(2 * Math.PI * 1174.66 * t) * short.MaxValue)); }
                writer.Flush(); return stream.ToArray();
            }
        }
    }

    public static class ReminderTimes {
        public static bool TryParse(string text, bool countdown, int duration, out List<int> points, out string error) {
            points = new List<int>(); error = null;
            foreach (string token in Regex.Split((text ?? "").Trim(), @"[,，;；、\s]+")) {
                if (token.Length == 0) continue;
                string[] parts = token.Replace('：', ':').Split(':'); int m, s = 0;
                if (parts.Length > 2 || !int.TryParse(parts[0], out m) || m < 0 || m > 99999 || (parts.Length == 2 && (!int.TryParse(parts[1], out s) || s < 0 || s > 59)) || m * 60 + s < 1) { error = "分段时间请填正整数分钟或分:秒，例如 5, 7, 15 或 0:30；秒须为 0–59。"; return false; }
                int value = m * 60 + s;
                if (countdown && value > duration) { error = "分段提示时间不能超过倒计时总时长。时间点按已计时多久计算。"; return false; }
                points.Add(value);
            }
            points = points.Distinct().OrderBy(v => v).ToList(); return true;
        }
        public static string Format(IEnumerable<int> points) { return string.Join(", ", (points ?? new int[0]).Select(v => v % 60 == 0 ? (v / 60).ToString(CultureInfo.InvariantCulture) : (v / 60).ToString(CultureInfo.InvariantCulture) + ":" + (v % 60).ToString("00", CultureInfo.InvariantCulture))); }
    }

    [DataContract]
    public sealed class TimerData {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "专注时间";
        [DataMember] public bool Countdown = true;
        [DataMember] public int Duration = 25 * 60;
        [DataMember] public double Elapsed;
        [DataMember] public string Background = "#152A32";
        [DataMember] public string Foreground = "#F3F8F6";
        [DataMember] public string Accent = "#65DEBA";
        [DataMember] public double BackgroundOpacity = 0;
        [DataMember] public double Opacity = 1;
        [DataMember] public bool Topmost = true;
        [DataMember] public bool Sound = true;
        [DataMember(EmitDefaultValue = false)] public bool RemindersEnabled;
        [DataMember(EmitDefaultValue = false)] public List<int> ReminderSeconds = new List<int>();
        [DataMember(EmitDefaultValue = false)] public int ReminderStyle; // 0: sound, 1: text, 2: both; legacy tone remains the short beep.
        [DataMember(EmitDefaultValue = false)] public int ReminderTone;
        [DataMember(EmitDefaultValue = false)] public int CompletionTone;
        [DataMember(EmitDefaultValue = false)] public string ReminderCustomSoundId;
        [DataMember(EmitDefaultValue = false)] public string CompletionCustomSoundId;
        [DataMember(EmitDefaultValue = false)] public List<CustomSound> CustomSounds = new List<CustomSound>();
        [DataMember(EmitDefaultValue = false)] public List<ReminderCue> ReminderCues = new List<ReminderCue>();
        [DataMember] public double Left = 100;
        [DataMember] public double Top = 100;
        [DataMember] public double Width = 390;
        [DataMember] public double Height = 304;
        [DataMember] public bool Visible = true;
        public TimerData Copy(bool prepareFresh) {
            var copy = (TimerData)MemberwiseClone();
            copy.ReminderSeconds = new List<int>(ReminderSeconds ?? new List<int>());
            copy.CustomSounds = (CustomSounds ?? new List<CustomSound>()).Where(s => s != null).Select(s => s.Copy()).ToList();
            copy.ReminderCues = (ReminderCues ?? new List<ReminderCue>()).Where(c => c != null).Select(c => c.Copy()).ToList();
            if (prepareFresh) { copy.Id = Guid.NewGuid().ToString("N"); copy.Elapsed = 0; copy.Visible = true; }
            return copy;
        }
        public void Normalize() {
            if (string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(Name)) Name = "计时器";
            Name = Name.Substring(0, Math.Min(40, Name.Length));
            Duration = Math.Max(1, Math.Min(5999999, Duration));
            if (double.IsNaN(Elapsed) || double.IsInfinity(Elapsed)) Elapsed = 0;
            Elapsed = Math.Max(0, Elapsed);
            if (Countdown) Elapsed = Math.Min(Duration, Elapsed);
            ReminderSeconds = (ReminderSeconds ?? new List<int>()).Where(v => v > 0 && v <= 5999999).Distinct().OrderBy(v => v).ToList();
            if (ReminderStyle < 0 || ReminderStyle > 2) ReminderStyle = 0;
            ReminderTone = AlarmAudio.ValidReminderTone(ReminderTone); CompletionTone = AlarmAudio.ValidCompletionTone(CompletionTone);
            CustomSounds = (CustomSounds ?? new List<CustomSound>()).Where(s => s != null && !string.IsNullOrEmpty(s.Id)).GroupBy(s => s.Id).Select(g => g.First()).ToList();
            ReminderCues = (ReminderCues ?? new List<ReminderCue>()).Where(c => c != null && ReminderSeconds.Contains(c.Seconds)).GroupBy(c => c.Seconds).Select(g => g.Last()).OrderBy(c => c.Seconds).ToList();
            foreach (var cue in ReminderCues) cue.Tone = AlarmAudio.ValidReminderTone(cue.Tone);
            Background = Ui.ValidColor(Background, "#152A32");
            Foreground = Ui.ValidColor(Foreground, "#F3F8F6");
            Accent = Ui.ValidColor(Accent, "#65DEBA");
            BackgroundOpacity = Ui.Clamp(BackgroundOpacity, 0, 1, 0);
            Opacity = Ui.Clamp(Opacity, .2, 1, 1);
            Width = Ui.Clamp(Width, 280, 2400, 390);
            Height = Ui.Clamp(Height, 200, 1600, 304);
            if (double.IsNaN(Left) || double.IsInfinity(Left)) Left = 100;
            if (double.IsNaN(Top) || double.IsInfinity(Top)) Top = 100;
        }
        public CustomSound FindSound(string id) { return CustomSounds == null ? null : CustomSounds.FirstOrDefault(s => s.Id == id); }
    }

    public sealed class TimerEngine {
        readonly Func<double> clock;
        double saved, anchor, reminderCursor;
        public readonly TimerData Data;
        public bool Running { get; private set; }
        public bool Completed { get; private set; }
        public bool ReminderDue { get; private set; }
        public int ReminderPoint { get; private set; }
        public TimerEngine(TimerData data, Func<double> clockSource) {
            Data = data; clock = clockSource; saved = data.Elapsed;
            reminderCursor = saved;
            Completed = data.Countdown && saved >= data.Duration;
        }
        public TimerEngine(TimerData data) : this(data, () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }
        public double Elapsed { get { return saved + (Running ? Math.Max(0, clock() - anchor) : 0); } }
        public double Remaining { get { return Math.Max(0, Data.Duration - Elapsed); } }
        public void Start() {
            if (Running || Completed) return;
            anchor = clock(); Running = true;
        }
        public void Pause() { saved = Elapsed; Running = false; Snapshot(); }
        public void Reset() { saved = 0; reminderCursor = 0; ReminderDue = false; ReminderPoint = 0; Running = false; Completed = false; Snapshot(); }
        public void RefreshReminderSchedule() { reminderCursor = Elapsed; ReminderDue = false; ReminderPoint = 0; }
        public bool Tick() {
            ReminderDue = false; ReminderPoint = 0;
            if (!Running) return false;
            double current = Data.Countdown ? Math.Min(Data.Duration, Elapsed) : Elapsed;
            if (Data.RemindersEnabled && Data.ReminderSeconds != null) ReminderPoint = Data.ReminderSeconds.Where(v => v > reminderCursor && v <= current).DefaultIfEmpty(0).Max();
            ReminderDue = ReminderPoint > 0;
            reminderCursor = current;
            if (Running && Data.Countdown && current >= Data.Duration) {
                saved = Data.Duration; Running = false; Completed = true; Snapshot(); return true;
            }
            return false;
        }
        public void Snapshot() { Data.Elapsed = Data.Countdown ? Math.Min(Data.Duration, Elapsed) : Elapsed; }
        public string Display { get {
            long total = (long)(Data.Countdown ? Math.Ceiling(Remaining) : Math.Floor(Elapsed));
            return (total / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        } }
        public string Status { get { return Completed ? "时间到" : Running ? "计时中" : Elapsed > 0 ? "已暂停" : "准备就绪"; } }
    }

    public static class Ui {
        public static readonly Brush Ink = Brush("#EAF1F6"), Muted = Brush("#98A8BB"), Panel = Brush("#192332"), Green = Brush("#65DEBA");
        public static Brush Brush(string color) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        public static string ValidColor(string value, string fallback) { try { ColorConverter.ConvertFromString(value); return value; } catch { return fallback; } }
        public static double Clamp(double n, double min, double max, double fallback) { return double.IsNaN(n) || double.IsInfinity(n) ? fallback : Math.Max(min, Math.Min(max, n)); }
        public static TextBlock Text(string text, double size, Brush foreground) {
            return new TextBlock { Text = text, FontSize = size, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        public static double Luminance(Color color) {
            double[] channels = { color.R / 255.0, color.G / 255.0, color.B / 255.0 };
            for (int i = 0; i < 3; i++) channels[i] = channels[i] <= .04045 ? channels[i] / 12.92 : Math.Pow((channels[i] + .055) / 1.055, 2.4);
            return .2126 * channels[0] + .7152 * channels[1] + .0722 * channels[2];
        }
        public static Brush ContrastText(string background) {
            double value = Luminance((Color)ColorConverter.ConvertFromString(background));
            double dark = Luminance((Color)ColorConverter.ConvertFromString("#101923"));
            double light = Luminance((Color)ColorConverter.ConvertFromString("#F7FAFC"));
            return Brush((value + .05) / (dark + .05) >= (light + .05) / (value + .05) ? "#101923" : "#F7FAFC");
        }
        public static Button Button(string text, bool primary, Action action, bool readableDisabled = false) {
            Button b = new Button { Content = text, Foreground = primary ? Brush("#102821") : Ink, Background = primary ? Green : Brush("#283647"), BorderThickness = new Thickness(0), Padding = new Thickness(14, 9, 14, 9), Cursor = Cursors.Hand, FontSize = 13, MinHeight = 34, FontWeight = FontWeights.SemiBold };
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonFace";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "ButtonContent";
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            ControlTemplate template = new ControlTemplate(typeof(Button)); template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .78)); template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, readableDisabled ? 1.0 : .35));
            if (readableDisabled) {
                disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#172331"), "ButtonFace"));
                disabled.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, Brush("#94A3B5"), "ButtonContent"));
                disabled.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "ButtonFace"));
            }
            template.Triggers.Add(disabled);
            b.Template = template; b.Click += delegate { action(); }; return b;
        }
        public static TextBox Input(string text) {
            return new TextBox { Text = text, FontSize = 15, Padding = new Thickness(10, 8, 10, 8), Foreground = Ink, Background = Brush("#111B28"), BorderBrush = Brush("#344359"), BorderThickness = new Thickness(1), CaretBrush = Green, VerticalContentAlignment = VerticalAlignment.Center };
        }
        public static StackPanel Row() { return new StackPanel { Orientation = Orientation.Horizontal }; }
        public static void Gap(Panel p, double width) { p.Children.Add(new Border { Width = width }); }
        public static void Label(Panel panel, string text) { panel.Children.Add(new TextBlock { Text = text, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 16, 0, 7) }); }
        public static void KeepOnScreen(Window window) {
            Forms.Screen[] screens = Forms.Screen.AllScreens;
            double scaleX = 1, scaleY = 1;
            PresentationSource source = PresentationSource.FromVisual(window);
            if (source != null && source.CompositionTarget != null) { scaleX = source.CompositionTarget.TransformToDevice.M11; scaleY = source.CompositionTarget.TransformToDevice.M22; }
            bool visible = screens.Any(s => new Rect(s.WorkingArea.X / scaleX, s.WorkingArea.Y / scaleY, s.WorkingArea.Width / scaleX, s.WorkingArea.Height / scaleY).IntersectsWith(new Rect(window.Left + 30, window.Top, Math.Max(50, window.Width - 60), 40)));
            if (!visible) { Rect area = SystemParameters.WorkArea; window.Left = area.Left + 60; window.Top = area.Top + 60; }
        }
        public static Rect WorkAreaFor(Window window) {
            if (window == null) return SystemParameters.WorkArea;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
            var area = Forms.Screen.FromHandle(handle).WorkingArea;
            var source = PresentationSource.FromVisual(window);
            double sx = 1, sy = 1;
            if (source != null && source.CompositionTarget != null) { sx = source.CompositionTarget.TransformToDevice.M11; sy = source.CompositionTarget.TransformToDevice.M22; }
            return new Rect(area.X / sx, area.Y / sy, area.Width / sx, area.Height / sy);
        }
    }

    public static class WindowArrangement {
        public static bool TryPlaceNew(Rect workArea, IEnumerable<Rect> occupied, double width, double height, out Rect slot) {
            const double margin = 12, gap = 8;
            slot = Rect.Empty;
            Rect bounds = new Rect(workArea.Left + margin, workArea.Top + margin, Math.Max(0, workArea.Width - 2 * margin), Math.Max(0, workArea.Height - 2 * margin));
            if (width > bounds.Width || height > bounds.Height) return false;
            var existing = occupied.ToList();
            var xs = new List<double> { bounds.Right - width, bounds.Left };
            var ys = new List<double> { bounds.Top };
            foreach (Rect r in existing) { xs.Add(r.Left); xs.Add(r.Left - gap - width); xs.Add(r.Right + gap); ys.Add(r.Bottom + gap); }
            foreach (double x in xs.Distinct().OrderByDescending(v => v)) foreach (double y in ys.Distinct().OrderBy(v => v)) {
                Rect candidate = new Rect(x, y, width, height);
                if (!bounds.Contains(candidate)) continue;
                Rect spaced = candidate; spaced.Inflate(gap / 2, gap / 2);
                if (existing.Any(r => spaced.IntersectsWith(r))) continue;
                slot = candidate; return true;
            }
            return false;
        }
        public static List<Rect> Plan(Rect workArea, int count, double preferredWidth, double preferredHeight) {
            var result = new List<Rect>(); if (count <= 0) return result;
            const double margin = 12, gap = 8, minWidth = 280, minHeight = 200;
            double availableWidth = workArea.Width - margin * 2, availableHeight = workArea.Height - margin * 2;
            int rowCapacity = (int)Math.Floor((availableHeight + gap) / (minHeight + gap));
            int columnCapacity = (int)Math.Floor((availableWidth + gap) / (minWidth + gap));
            if (rowCapacity < 1 || columnCapacity < 1 || (long)rowCapacity * columnCapacity < count) throw new InvalidOperationException("当前屏幕无法同时容纳这些计时窗口，请减少本组窗口数量或使用更大的屏幕。");
            int rows = Math.Min(count, rowCapacity), columns = (count + rows - 1) / rows;
            double width = Math.Min(Ui.Clamp(preferredWidth, minWidth, 390, 390), (availableWidth - gap * (columns - 1)) / columns);
            double height = Math.Min(Ui.Clamp(preferredHeight, minHeight, 304, 304), (availableHeight - gap * (rows - 1)) / rows);
            for (int i = 0; i < count; i++) {
                int column = i / rows, row = i % rows;
                result.Add(new Rect(workArea.Right - margin - width - column * (width + gap), workArea.Top + margin + row * (height + gap), width, height));
            }
            return result;
        }
    }

    [DataContract] public sealed class TimerAppearance {
        [DataMember] public string Background = "#152A32";
        [DataMember] public string Foreground = "#F3F8F6";
        [DataMember] public string Accent = "#65DEBA";
        [DataMember] public double BackgroundOpacity = 0;
        [DataMember] public double Opacity = 1;
        public static TimerAppearance FromTimer(TimerData data) {
            return new TimerAppearance { Background = data.Background, Foreground = data.Foreground, Accent = data.Accent, BackgroundOpacity = data.BackgroundOpacity, Opacity = data.Opacity };
        }
        public void Normalize() {
            Background = Ui.ValidColor(Background, "#152A32"); Foreground = Ui.ValidColor(Foreground, "#F3F8F6"); Accent = Ui.ValidColor(Accent, "#65DEBA");
            BackgroundOpacity = Ui.Clamp(BackgroundOpacity, 0, 1, 0); Opacity = Ui.Clamp(Opacity, .2, 1, 1);
        }
        public void ApplyTo(TimerData data) {
            data.Background = Background; data.Foreground = Foreground; data.Accent = Accent; data.BackgroundOpacity = BackgroundOpacity; data.Opacity = Opacity;
        }
        public TimerAppearance Copy() { return (TimerAppearance)MemberwiseClone(); }
    }

    [DataContract] public sealed class TimerGroup {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "默认分组";
        [DataMember] public List<TimerData> Timers = new List<TimerData>();
        [DataMember(EmitDefaultValue = false)] public bool ArrangeRight;
        public string DisplayName { get { return Name; } }
    }

    [DataContract] public sealed class SavedState {
        [DataMember] public int Version = 4;
        [DataMember] public List<TimerData> Timers = new List<TimerData>();
        [DataMember(EmitDefaultValue = false)] public TimerAppearance DefaultAppearance;
        [DataMember(EmitDefaultValue = false)] public List<TimerGroup> Groups;
        [DataMember(EmitDefaultValue = false)] public string ActiveGroupId;
    }

    public sealed class Store {
        public string PathName;
        public string Warning;
        public TimerAppearance DefaultAppearance = new TimerAppearance();
        public List<TimerGroup> Groups = new List<TimerGroup>();
        public string ActiveGroupId;
        public TimerGroup ActiveGroup { get { return Groups.First(g => g.Id == ActiveGroupId); } }
        public Store(string overridePath) {
            PathName = overridePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FloatTimer.settings.json");
            var first = new TimerGroup(); Groups.Add(first); ActiveGroupId = first.Id;
        }
        public List<TimerData> Load() {
            if (!File.Exists(PathName)) return null;
            try {
                using (FileStream stream = File.OpenRead(PathName)) {
                    var state = (SavedState)new DataContractJsonSerializer(typeof(SavedState)).ReadObject(stream);
                    if (state == null || state.Timers == null) throw new InvalidDataException();
                    DefaultAppearance = state.DefaultAppearance ?? new TimerAppearance(); DefaultAppearance.Normalize();
                    Groups = (state.Groups ?? new List<TimerGroup>()).Where(g => g != null).ToList();
                    if (Groups.Count == 0) Groups.Add(new TimerGroup { Timers = state.Timers });
                    var ids = new HashSet<string>();
                    foreach (var group in Groups) {
                        if (string.IsNullOrEmpty(group.Id) || !ids.Add(group.Id)) { group.Id = Guid.NewGuid().ToString("N"); ids.Add(group.Id); }
                        if (string.IsNullOrWhiteSpace(group.Name)) group.Name = "计时分组";
                        group.Name = group.Name.Trim().Substring(0, Math.Min(40, group.Name.Trim().Length));
                        group.Timers = (group.Timers ?? new List<TimerData>()).Where(t => t != null).ToList();
                        foreach (var data in group.Timers) data.Normalize();
                    }
                    ActiveGroupId = Groups.Any(g => g.Id == state.ActiveGroupId) ? state.ActiveGroupId : Groups[0].Id;
                    return ActiveGroup.Timers.Select(t => t.Copy(false)).ToList();
                }
            } catch {
                try { File.Copy(PathName, PathName + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmss"), false); } catch { }
                Warning = "原设置文件无法读取，已尝试保留备份。本次将使用默认设置。"; return null;
            }
        }
        public bool Save(IEnumerable<TimerData> timers) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                string temp = PathName + ".tmp";
                var current = timers.Select(t => t.Copy(false)).ToList(); ActiveGroup.Timers = current;
                using (FileStream stream = File.Create(temp)) new DataContractJsonSerializer(typeof(SavedState)).WriteObject(stream, new SavedState { Timers = current, DefaultAppearance = DefaultAppearance, Groups = Groups, ActiveGroupId = ActiveGroupId });
                if (File.Exists(PathName)) {
                    try { File.Replace(temp, PathName, PathName + ".bak"); }
                    catch (UnauthorizedAccessException) { CopyWithBackup(temp); }
                    catch (IOException) { CopyWithBackup(temp); }
                } else File.Move(temp, PathName);
                return true;
            } catch { Warning = "设置暂时无法保存，请把程序放到可写入的文件夹后重试。"; return false; }
        }
        void CopyWithBackup(string temp) {
            // Some restricted folders allow writes but reject ReplaceFile metadata operations.
            File.Copy(PathName, PathName + ".bak", true);
            File.Copy(temp, PathName, true);
            File.Delete(temp);
        }
    }

    public sealed class TimerWindow : Window {
        readonly Host host;
        public readonly TimerEngine Engine;
        readonly Border card;
        readonly Border completionAlert;
        bool completionAlertActive, timerStopped;
        readonly TextBlock title, mode, time, secondsText, colonText, minuteUnit, secondUnit, status;
        readonly Button start, pause, reset, pin;
        readonly StackPanel tools;
        readonly Grid body, digitsArea;
        readonly StackPanel middle;
        readonly Border controlsDock;
        readonly TextBlock grip;
        bool compact, interacting;
        readonly Border progress;
        readonly Grid progressTrack;
        readonly DispatcherTimer pulse;
        readonly TextBlock finished;
        bool dragPending, dragging;
        Point dragPointer;
        double dragLeft, dragTop;
        string soundError;
        string reminderNotice;
        double reminderNoticeUntil;
        public TimerWindow(Host host, TimerData data) {
            this.host = host; Engine = new TimerEngine(data);
            Title = data.Name + " · 浮光计时"; FontFamily = new FontFamily("Microsoft YaHei UI");
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            MinWidth = 280; MinHeight = 200; Width = data.Width; Height = data.Height; Left = data.Left; Top = data.Top;
            ShowInTaskbar = false;
            Grid root = new Grid(); Content = root;
            card = new Border { Name = "TimerCard", CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), Cursor = Cursors.SizeAll }; root.Children.Add(card);
            completionAlert = new Border { Name = "CompletionAlert", CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(3), BorderBrush = Ui.Brush("#FFD166"), IsHitTestVisible = false, Visibility = Visibility.Hidden };
            completionAlert.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = .85 };
            root.Children.Add(completionAlert);
            body = new Grid { Margin = new Thickness(20, 12, 20, 16) }; card.Child = body;
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid head = new Grid { Background = Brushes.Transparent }; body.Children.Add(head);
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }; head.Children.Add(heading);
            title = Ui.Text(data.Name, 23, Ui.Ink); title.FontWeight = FontWeights.Bold; title.TextAlignment = TextAlignment.Center; title.Margin = new Thickness(0, 8, 0, 2); Grid.SetRow(title, 1); Grid.SetColumnSpan(title, 2); head.Children.Add(title);
            mode = Ui.Text("", 10, Ui.Muted); heading.Children.Add(mode);
            tools = Ui.Row(); tools.Name = "WindowTools"; tools.Visibility = Visibility.Hidden; Grid.SetColumn(tools, 1); head.Children.Add(tools);
            MouseEnter += delegate { SetInteractionVisible(true); };
            MouseLeave += delegate { SetInteractionVisible(false); };
            IsVisibleChanged += delegate { if (!IsVisible) SetInteractionVisible(false); else Update(); };
            pin = SmallButton("●", "置顶 / 取消置顶", delegate { data.Topmost = !data.Topmost; ApplyStyle(); host.Changed(); }); tools.Children.Add(pin);
            tools.Children.Add(SmallButton("⚙", "名称、时长、颜色和透明度", delegate { host.Edit(this); }));
            tools.Children.Add(SmallButton("×", "隐藏窗口（可在管理面板恢复）", delegate { Hide(); data.Visible = false; host.Changed(); }));
            digitsArea = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 5, 0, 0) }; Grid.SetRow(digitsArea, 1); body.Children.Add(digitsArea);
            digitsArea.ColumnDefinitions.Add(new ColumnDefinition()); digitsArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); digitsArea.ColumnDefinitions.Add(new ColumnDefinition());
            digitsArea.RowDefinitions.Add(new RowDefinition()); digitsArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            time = Ui.Text("25", 84, Ui.Ink); secondsText = Ui.Text("00", 84, Ui.Ink); colonText = Ui.Text(":", 60, Ui.Ink);
            TextBlock[] values = { time, colonText, secondsText };
            for (int i = 0; i < values.Length; i++) {
                values[i].FontFamily = new FontFamily("Consolas"); values[i].FontWeight = FontWeights.Bold;
                var box = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 2) }; box.Child = values[i]; Grid.SetColumn(box, i); digitsArea.Children.Add(box);
            }
            minuteUnit = Ui.Text("分", 12, Ui.Ink); secondUnit = Ui.Text("秒", 12, Ui.Ink);
            foreach (var unit in new[] { minuteUnit, secondUnit }) { unit.HorizontalAlignment = HorizontalAlignment.Center; unit.FontWeight = FontWeights.SemiBold; unit.Margin = new Thickness(0, 0, 0, 5); Grid.SetRow(unit, 1); digitsArea.Children.Add(unit); }
            Grid.SetColumn(secondUnit, 2);
            middle = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; Grid.SetRow(middle, 2); body.Children.Add(middle);
            Grid stateLine = new Grid(); middle.Children.Add(stateLine);
            status = Ui.Text("", 11, Ui.Muted); status.Name = "TimerStatus"; status.MinHeight = 16; stateLine.Children.Add(status);
            finished = Ui.Text("已完成", 11, Ui.Green); finished.HorizontalAlignment = HorizontalAlignment.Right; finished.Visibility = Visibility.Collapsed; stateLine.Children.Add(finished);
            progressTrack = new Grid { Name = "TimerProgress", Height = 3, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Margin = new Thickness(0, 9, 0, 0), ClipToBounds = true }; middle.Children.Add(progressTrack);
            progress = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) }; progressTrack.Children.Add(progress);
            controlsDock = new Border { Name = "TimerControls", CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(244, 16, 26, 38)), BorderBrush = new SolidColorBrush(Color.FromArgb(100, 190, 205, 220)), BorderThickness = new Thickness(1), Padding = new Thickness(3), Visibility = Visibility.Hidden };
            Grid.SetRow(controlsDock, 3); body.Children.Add(controlsDock); Grid buttons = new Grid(); controlsDock.Child = buttons;
            for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
            start = Ui.Button("开始", true, delegate { Engine.Start(); host.Changed(); Update(); }, true);
            pause = Ui.Button("暂停", false, delegate { Engine.Pause(); host.Changed(); Update(); }, true);
            reset = Ui.Button("重置", false, ResetTimer, true);
            Button[] btns = { start, pause, reset };
            for (int i = 0; i < 3; i++) { btns[i].Padding = new Thickness(4, 7, 4, 7); btns[i].Margin = new Thickness(i == 0 ? 0 : 2, 0, i == 2 ? 0 : 2, 0); Grid.SetColumn(btns[i], i); buttons.Children.Add(btns[i]); }
            AddResize(root, HorizontalAlignment.Left, VerticalAlignment.Stretch, 6, double.NaN, Cursors.SizeWE, -1, 0);
            AddResize(root, HorizontalAlignment.Right, VerticalAlignment.Stretch, 6, double.NaN, Cursors.SizeWE, 1, 0);
            AddResize(root, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 6, Cursors.SizeNS, 0, -1);
            AddResize(root, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 6, Cursors.SizeNS, 0, 1);
            AddResize(root, HorizontalAlignment.Left, VerticalAlignment.Top, 12, 12, Cursors.SizeNWSE, -1, -1);
            AddResize(root, HorizontalAlignment.Right, VerticalAlignment.Top, 12, 12, Cursors.SizeNESW, 1, -1);
            AddResize(root, HorizontalAlignment.Left, VerticalAlignment.Bottom, 12, 12, Cursors.SizeNESW, -1, 1);
            AddResize(root, HorizontalAlignment.Right, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNWSE, 1, 1);
            grip = Ui.Text("◢", 11, new SolidColorBrush(Color.FromArgb(95, 255, 255, 255))); grip.Name = "ResizeGrip"; grip.Visibility = Visibility.Hidden; grip.IsHitTestVisible = false; grip.HorizontalAlignment = HorizontalAlignment.Right; grip.VerticalAlignment = VerticalAlignment.Bottom; grip.Margin = new Thickness(0, 0, 5, 3); root.Children.Add(grip);
            ContextMenu context = new ContextMenu();
            MenuItem settings = new MenuItem { Header = "设置计时器" }; settings.Click += delegate { host.Edit(this); }; context.Items.Add(settings);
            MenuItem add = new MenuItem { Header = "添加计时器" }; add.Click += delegate { host.Add(); }; context.Items.Add(add);
            MenuItem manage = new MenuItem { Header = "打开管理面板" }; manage.Click += delegate { host.ShowManager(); }; context.Items.Add(manage);
            MenuItem resetItem = new MenuItem { Header = "重置（恢复初始状态，停止计时）" }; resetItem.Click += delegate { ResetTimer(); }; context.Items.Add(resetItem);
            MenuItem remove = new MenuItem { Header = "删除此计时器…" }; remove.Click += delegate { host.Delete(this); }; context.Items.Add(remove); card.ContextMenu = context;
            // Handle movement before child controls consume mouse events. Capture the mouse
            // so movement continues beyond the original window bounds, including with a
            // transparent background. Buttons and resize handles keep their own gestures.
            PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                DependencyObject source = e.OriginalSource as DependencyObject;
                bool inHeading = false;
                for (DependencyObject current = source; current != null; current = VisualTreeHelper.GetParent(current)) {
                    if (current is ButtonBase || current is Thumb || current is TextBoxBase || current is ComboBox) return;
                    if (current == heading || current == title) inHeading = true;
                    if (current == this) break;
                }
                if (inHeading && e.ClickCount == 2) { e.Handled = true; host.Edit(this); return; }
                dragPointer = PointToScreen(e.GetPosition(this)); dragLeft = Left; dragTop = Top;
                dragging = false; dragPending = CaptureMouse(); e.Handled = dragPending;
            };
            PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (!dragPending) return;
                Vector delta = PointToScreen(e.GetPosition(this)) - dragPointer;
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null) delta = source.CompositionTarget.TransformFromDevice.Transform(delta);
                if (!dragging && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true; Left = dragLeft + delta.X; Top = dragTop + delta.Y; e.Handled = true;
            };
            PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                if (!dragPending) return;
                bool moved = dragging && (Left != dragLeft || Top != dragTop);
                dragPending = false; dragging = false; ReleaseMouseCapture();
                if (moved) host.ManualLayoutChanged(this); else host.Changed(false); e.Handled = true;
            };
            LostMouseCapture += delegate { bool moved = dragPending && dragging && (Left != dragLeft || Top != dragTop); dragPending = false; dragging = false; if (moved) host.ManualLayoutChanged(this); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Space) { if (!e.IsRepeat) { if (Engine.Running) Engine.Pause(); else Engine.Start(); host.Changed(); Update(); } e.Handled = true; }
                if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { if (!e.IsRepeat) ResetTimer(); e.Handled = true; }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!host.Exiting) { e.Cancel = true; Hide(); data.Visible = false; host.Changed(); } };
            LocationChanged += delegate { SaveGeometry(); }; SizeChanged += delegate { SaveGeometry(); ApplyCompactLayout(); Update(); };
            SourceInitialized += delegate { Ui.KeepOnScreen(this); };
            pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            pulse.Tick += delegate {
                bool completed = Engine.Tick();
                bool shortAtEnd = completed && data.RemindersEnabled && data.ReminderSeconds != null && data.ReminderSeconds.Contains(data.Duration);
                if (completed && data.Sound && !shortAtEnd) { soundError = null; host.Audio.TryPlay(data, out soundError); }
                else if (Engine.ReminderDue && data.ReminderStyle != 1) { soundError = null; host.Audio.TryPlayReminder(data, Engine.ReminderPoint, out soundError); }
                if (Engine.ReminderDue && data.ReminderStyle != 0) {
                    reminderNotice = "已计时 " + FormatPoint(Engine.ReminderPoint) + (data.Countdown ? " · 剩余 " + FormatPoint(Math.Max(0, data.Duration - Engine.ReminderPoint)) : "");
                    reminderNoticeUntil = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency + 6;
                }
                if (completed) host.Changed();
                Update();
            }; pulse.Start();
            ApplyStyle(); ApplyCompactLayout(); Update();
        }
        void SetInteractionVisible(bool visible) {
            interacting = visible; tools.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (controlsDock != null) controlsDock.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (progressTrack != null) progressTrack.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (grip != null) grip.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            Update();
        }
        void ApplyCompactLayout() {
            if (start == null) return;
            compact = Height < 260;
            body.Margin = compact ? new Thickness(20, 8, 20, 10) : new Thickness(20, 12, 20, 16);
            title.FontSize = compact ? 20 : 23; title.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 8, 0, 2);
            mode.Visibility = compact ? Visibility.Collapsed : interacting ? Visibility.Visible : Visibility.Hidden;
            foreach (Button button in tools.Children) { button.MinHeight = compact ? 26 : 32; button.Width = compact ? 28 : 32; button.FontSize = compact ? 16 : 18; }
            foreach (var button in new[] { start, pause, reset }) { button.MinHeight = compact ? 26 : 30; button.Padding = new Thickness(4, compact ? 5 : 7, 4, compact ? 5 : 7); }
            digitsArea.Margin = new Thickness(0, compact ? 0 : 5, 0, 0); middle.Margin = new Thickness(0, 0, 0, compact ? 6 : 12);
            progressTrack.Margin = new Thickness(0, compact ? 4 : 9, 0, 0);
            foreach (var unit in new[] { minuteUnit, secondUnit }) unit.Margin = new Thickness(0, 0, 0, compact ? 1 : 5);
        }
        public void ResetTimer() { Engine.Reset(); host.Audio.Stop(); host.Changed(); Update(); }
        Button SmallButton(string label, string hint, Action action) {
            var button = Ui.Button(label, false, action); StyleNeutralControl(button);
            button.FontFamily = new FontFamily("Segoe UI Symbol"); button.FontSize = 18; button.MinHeight = 32; button.Width = 32; button.Padding = new Thickness(0); button.Margin = new Thickness(4, 0, 0, 0); button.ToolTip = hint; return button;
        }
        static void StyleNeutralControl(Button button) {
            button.Background = Ui.Brush("#172332"); button.Foreground = Ui.Brush("#F7FAFC");
            button.BorderBrush = Ui.Brush("#AAB9C9"); button.BorderThickness = new Thickness(1);
        }
        static void AddTextHalo(TextBlock text) {
            Color foreground = ((SolidColorBrush)text.Foreground).Color;
            Color halo = Ui.Luminance(foreground) > .35 ? Colors.Black : Colors.White;
            var existing = text.Effect as System.Windows.Media.Effects.DropShadowEffect;
            if (existing != null && existing.Color == halo) return;
            text.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = halo, BlurRadius = 2.5, ShadowDepth = 0, Opacity = .95, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality };
        }
        void AddResize(Grid root, HorizontalAlignment h, VerticalAlignment v, double width, double height, Cursor cursor, int dx, int dy) {
            Thumb thumb = new Thumb { HorizontalAlignment = h, VerticalAlignment = v, Width = width, Height = height, Cursor = cursor };
            Rect beforeResize = Rect.Empty;
            thumb.DragStarted += delegate { beforeResize = new Rect(Left, Top, Width, Height); };
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent); thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            thumb.DragDelta += delegate(object sender, DragDeltaEventArgs e) {
                if (dx != 0) { double next = Math.Max(MinWidth, Math.Min(2400, Width + dx * e.HorizontalChange)); if (dx < 0) Left += Width - next; Width = next; }
                if (dy != 0) { double next = Math.Max(MinHeight, Math.Min(1600, Height + dy * e.VerticalChange)); if (dy < 0) Top += Height - next; Height = next; }
            }; root.Children.Add(thumb);
            thumb.DragCompleted += delegate { if (!beforeResize.IsEmpty && beforeResize != new Rect(Left, Top, Width, Height)) host.ManualLayoutChanged(this); beforeResize = Rect.Empty; };
        }
        void SaveGeometry() {
            if (host == null || !IsLoaded) return;
            Engine.Data.Left = Left; Engine.Data.Top = Top; Engine.Data.Width = Width; Engine.Data.Height = Height; host.Changed(false);
        }
        public void ApplyStyle() {
            TimerData d = Engine.Data; Color color = (Color)ColorConverter.ConvertFromString(d.Background);
            // Layered windows discard mouse hits on pixels with exactly zero alpha.
            // Keep an imperceptible hit surface even when the background is set to 0%.
            color.A = (byte)Math.Max(Math.Ceiling(1.0 / d.Opacity), Math.Round(255 * d.BackgroundOpacity));
            card.Background = new SolidColorBrush(color); Opacity = d.Opacity; Topmost = d.Topmost;
            title.Text = d.Name; title.ToolTip = d.Name + "\n按住拖动；双击打开设置"; Title = d.Name + " · 浮光计时"; title.Foreground = Ui.Brush(d.Foreground);
            foreach (var text in new[] { time, secondsText, colonText, minuteUnit, secondUnit }) text.Foreground = Ui.Brush(d.Foreground);
            Brush secondary = Ui.Brush(d.Foreground); secondary.Opacity = d.BackgroundOpacity < .5 ? .9 : .7; mode.Foreground = secondary; status.Foreground = secondary;
            mode.Text = d.Countdown ? "倒计时  /  COUNTDOWN" : "正计时  /  STOPWATCH";
            progress.Background = Ui.Brush(d.Accent); finished.Foreground = Ui.Brush(d.Accent);
            double accentLuminance = Ui.Luminance((Color)ColorConverter.ConvertFromString(d.Accent));
            double dockLuminance = Ui.Luminance((Color)ColorConverter.ConvertFromString("#253E39"));
            bool readableAccent = (Math.Max(accentLuminance, dockLuminance) + .05) / (Math.Min(accentLuminance, dockLuminance) + .05) >= 4.5;
            pin.Content = d.Topmost ? "●" : "○"; pin.Background = Ui.Brush("#172332"); pin.Foreground = d.Topmost && readableAccent ? Ui.Brush(d.Accent) : Ui.Brush("#F7FAFC");
            pin.ToolTip = d.Topmost ? "已置顶，点击取消" : "未置顶，点击开启";
            start.Background = Ui.Brush("#253E39"); start.Foreground = readableAccent ? Ui.Brush(d.Accent) : Ui.Brush("#F7FAFC"); start.BorderThickness = new Thickness(0);
            foreach (Button b in new[] { pause, reset }) { b.Background = Ui.Brush("#263445"); b.Foreground = Ui.Brush("#E8EEF5"); b.BorderThickness = new Thickness(0); }
            foreach (TextBlock text in new[] { title, mode, time, secondsText, colonText, minuteUnit, secondUnit, status, finished }) AddTextHalo(text);
            Update();
        }
        public void Update() {
            if (time == null || start == null) return;
            if (!Engine.Completed && Engine.Elapsed == 0) soundError = null;
            if (Engine.Elapsed == 0 || !Engine.Data.RemindersEnabled || Engine.Data.ReminderStyle == 0 || (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency >= reminderNoticeUntil) ClearReminderNotice();
            bool hasNotice = reminderNotice != null;
            bool reserveNotice = Engine.Data.RemindersEnabled && Engine.Data.ReminderStyle != 0;
            mode.Visibility = compact ? Visibility.Collapsed : interacting ? Visibility.Visible : Visibility.Hidden;
            status.Visibility = hasNotice || Engine.Completed || soundError != null ? Visibility.Visible : compact && !reserveNotice ? Visibility.Collapsed : interacting ? Visibility.Visible : Visibility.Hidden;
            progressTrack.Visibility = interacting ? Visibility.Visible : Visibility.Hidden;
            string[] parts = Engine.Display.Split(':'); time.Text = parts[0]; secondsText.Text = parts[1];
            status.Text = hasNotice ? reminderNotice : Engine.Status + (soundError == null ? "" : " · 提示音未播放");
            status.TextAlignment = hasNotice ? TextAlignment.Center : TextAlignment.Left;
            status.FontWeight = hasNotice ? FontWeights.SemiBold : FontWeights.Normal;
            status.ToolTip = soundError == null ? status.Text : status.Text + "\n" + soundError; start.Content = Engine.Elapsed > 0 ? "继续" : "开始";
            start.IsEnabled = !Engine.Running && !Engine.Completed; pause.IsEnabled = Engine.Running;
            finished.Visibility = Engine.Completed && !hasNotice ? Visibility.Visible : Visibility.Collapsed;
            progress.Width = Math.Max(0, progressTrack.ActualWidth * (Engine.Data.Countdown ? Math.Min(1, Engine.Elapsed / Engine.Data.Duration) : (Engine.Elapsed % 60) / 60));
            Brush displayColor = Ui.Brush(Engine.Completed ? Engine.Data.Accent : Engine.Data.Foreground);
            foreach (var text in new[] { time, secondsText, colonText }) { text.Foreground = displayColor; AddTextHalo(text); }
            card.BorderBrush = interacting ? (Engine.Completed ? Ui.Brush(Engine.Data.Accent) : new SolidColorBrush(Color.FromArgb(45, 255, 255, 255))) : Brushes.Transparent;
            UpdateCompletionAlert();
        }
        public void ClearReminderNotice() { reminderNotice = null; reminderNoticeUntil = 0; }
        static string FormatPoint(int seconds) { return (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture); }
        void UpdateCompletionAlert() {
            bool shouldAlert = Engine.Completed && IsVisible && !timerStopped;
            if (!shouldAlert) { if (completionAlertActive) StopCompletionAlert(); return; }
            if (completionAlertActive) return;
            completionAlertActive = true; completionAlert.Visibility = Visibility.Visible;
            // A slow pulse stays visible on transparent windows without changing layout.
            completionAlert.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.18, 1, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        void StopCompletionAlert() {
            completionAlertActive = false;
            completionAlert.BeginAnimation(UIElement.OpacityProperty, null);
            completionAlert.Visibility = Visibility.Hidden;
        }
        public void StopTimer() { timerStopped = true; pulse.Stop(); StopCompletionAlert(); }
    }

    public sealed class Editor : Window {
        readonly TimerData data;
        readonly AlarmAudio audio;
        readonly TextBox name, minutes, seconds, reminderTimes;
        readonly ComboBox mode, reminderStyle;
        readonly SoundLibrary soundLibrary;
        readonly SoundPicker reminderSound, completionSound;
        readonly StackPanel cueRows;
        readonly Expander cueExpander;
        readonly Dictionary<int, SoundPicker> cuePickers = new Dictionary<int, SoundPicker>();
        readonly Dictionary<int, SoundSelection> cueSelections = new Dictionary<int, SoundSelection>();
        readonly Slider backgroundAlpha, opacity;
        readonly CheckBox topmost, sound, remindersEnabled;
        readonly Border preview;
        readonly TextBlock previewText, error;
        readonly TextBlock bgValue, opacityValue;
        readonly Func<TimerAppearance, int> applyAll;
        readonly bool appearanceOnly;
        string bg, fg, accent;
        public Editor(TimerData data, Window owner, AlarmAudio audio, Func<TimerAppearance, int> applyAll = null, bool appearanceOnly = false) {
            this.data = data; this.audio = audio; this.applyAll = applyAll; this.appearanceOnly = appearanceOnly; bg = data.Background; fg = data.Foreground; accent = data.Accent;
            soundLibrary = new SoundLibrary(data.CustomSounds);
            foreach (var cue in data.ReminderCues ?? new List<ReminderCue>()) cueSelections[cue.Seconds] = new SoundSelection { Tone = cue.Tone, CustomSoundId = cue.CustomSoundId };
            Title = appearanceOnly ? "统一外观 · 浮光计时" : "计时器设置 · 浮光计时"; Width = 490; Height = appearanceOnly ? 620 : 765; MinWidth = 430; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner; Topmost = true; Background = Ui.Brush("#101925"); Foreground = Ui.Ink; FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false;
            Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            ScrollViewer scroll = new ScrollViewer { Name = "SettingsScroll", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly, CanContentScroll = false, Background = Brushes.Transparent }; root.Children.Add(scroll);
            StackPanel panel = new StackPanel { Margin = new Thickness(26, 20, 26, 8) }; scroll.Content = panel;
            panel.Children.Add(Ui.Text(appearanceOnly ? "统一当前分组外观" : "让时间适合你的情境", 21, Ui.Ink));
            StackPanel details = new StackPanel(); if (!appearanceOnly) panel.Children.Add(details);
            Ui.Label(details, "窗口名称"); name = Ui.Input(data.Name); name.MaxLength = 40; details.Children.Add(name);
            Ui.Label(details, "计时方式"); mode = new ComboBox { Name = "TimerMode", FontSize = 14, Padding = new Thickness(9, 7, 9, 7), ItemsSource = new[] { "倒计时 · 到时提醒", "正计时 · 从零累计" }, SelectedIndex = data.Countdown ? 0 : 1 }; details.Children.Add(mode);
            Ui.Label(details, "倒计时时长（分 : 秒，最高 99999:59）");
            StackPanel duration = Ui.Row(); minutes = Ui.Input((data.Duration / 60).ToString()); minutes.Width = 112; seconds = Ui.Input((data.Duration % 60).ToString("00")); seconds.Width = 80;
            duration.Children.Add(minutes); duration.Children.Add(new TextBlock { Text = " : ", Foreground = Ui.Muted, FontSize = 18, VerticalAlignment = VerticalAlignment.Center }); duration.Children.Add(seconds); details.Children.Add(duration);
            StackPanel presets = Ui.Row(); presets.Margin = new Thickness(0, 8, 0, 0);
            foreach (int m in new[] { 5, 15, 25, 45 }) { int value = m; var b = Ui.Button(m + " 分", false, delegate { minutes.Text = value.ToString(); seconds.Text = "00"; mode.SelectedIndex = 0; }); b.Margin = new Thickness(0, 0, 8, 0); presets.Children.Add(b); } details.Children.Add(presets);
            mode.SelectionChanged += delegate { minutes.IsEnabled = seconds.IsEnabled = mode.SelectedIndex == 0; }; minutes.IsEnabled = seconds.IsEnabled = data.Countdown;
            Ui.Label(details, "分段提醒（按已计时计算，不是剩余时间）");
            remindersEnabled = new CheckBox { Content = "启用分段提示，到点提醒并继续计时", IsChecked = data.RemindersEnabled, Foreground = Ui.Ink }; details.Children.Add(remindersEnabled);
            reminderStyle = new ComboBox { Name = "ReminderStyle", FontSize = 14, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 8, 0, 0), ItemsSource = new[] { "声音提示", "窗口小字（无分段声音）", "声音＋窗口小字" }, SelectedIndex = data.ReminderStyle, IsEnabled = data.RemindersEnabled }; details.Children.Add(reminderStyle);
            reminderTimes = Ui.Input(ReminderTimes.Format(data.ReminderSeconds)); reminderTimes.Name = "ReminderTimes"; reminderTimes.MaxLength = 1500; reminderTimes.Margin = new Thickness(0, 8, 0, 0); reminderTimes.IsEnabled = data.RemindersEnabled; details.Children.Add(reminderTimes);
            reminderTimes.TextChanged += delegate { if (error != null) error.Text = ""; if (cueRows != null) RefreshCueRows(); };
            details.Children.Add(new TextBlock { Text = "例如 5, 7, 15 表示已计时第 5、7、15 分钟；也可填 0:30。小字在数字下方显示约 6 秒，鼠标移开仍可见。终点若设为节点，按所选方式提醒，不叠加结束铃声；未设终点时，结束铃声开关仍独立生效。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 7) });
            Ui.Label(details, "默认分段提示音");
            reminderSound = new SoundPicker(soundLibrary, audio, false, false, "ReminderTone", "试听分段音", new SoundSelection { Tone = data.ReminderTone, CustomSoundId = data.ReminderCustomSoundId }); details.Children.Add(reminderSound);
            cueRows = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            cueExpander = new Expander { Name = "ReminderCueExpander", Header = "各分段独立声音（展开设置）", Foreground = Ui.Ink, Margin = new Thickness(0, 14, 0, 6), Content = cueRows, IsExpanded = cueSelections.Count > 0 }; details.Children.Add(cueExpander);
            RefreshCueRows();
            details.Children.Add(new TextBlock { Text = "每段可沿用默认声音，也可单独选择内置音效或本地 WAV / MP3。保存后音频随计时器保存，原文件移动或删除后仍可使用。单文件不超过 10 MB、120 秒。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });
            Action updateReminderControls = delegate { bool enabled = remindersEnabled.IsChecked == true; reminderTimes.IsEnabled = reminderStyle.IsEnabled = cueExpander.IsEnabled = enabled; reminderSound.EnableSound(enabled && reminderStyle.SelectedIndex != 1); cueRows.IsEnabled = enabled && reminderStyle.SelectedIndex != 1; };
            remindersEnabled.Checked += delegate { updateReminderControls(); cueExpander.IsExpanded = true; }; remindersEnabled.Unchecked += delegate { updateReminderControls(); }; reminderStyle.SelectionChanged += delegate { updateReminderControls(); }; updateReminderControls();
            StackPanel soundRow = Ui.Row(); soundRow.Margin = new Thickness(0, 16, 0, 0); details.Children.Add(soundRow);
            sound = new CheckBox { Content = "倒计时结束时播放提示铃声", IsChecked = data.Sound, Foreground = Ui.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }; soundRow.Children.Add(sound);
            completionSound = new SoundPicker(soundLibrary, audio, true, false, "CompletionTone", "试听提示音", new SoundSelection { Tone = data.CompletionTone, CustomSoundId = data.CompletionCustomSoundId }); completionSound.Margin = new Thickness(0, 8, 0, 0); details.Children.Add(completionSound);
            details.Children.Add(new TextBlock { Text = "结束铃声与分段提示音分别保存。终点已设为分段节点时，只按该节点提醒，不叠加结束铃声。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
            Ui.Label(panel, "配色主题"); StackPanel colors = Ui.Row();
            string[,] themes = { { "深海", "#152A32", "#F3F8F6", "#65DEBA" }, { "暮紫", "#29213D", "#F6EEFF", "#C8ADFF" }, { "暖杏", "#F3E7D5", "#3C3023", "#E2A257" }, { "极夜", "#161B22", "#EEF3FA", "#81BBFF" } };
            for (int i = 0; i < 4; i++) { int index = i; Button b = Ui.Button(themes[i, 0], false, delegate { bg = themes[index, 1]; fg = themes[index, 2]; accent = themes[index, 3]; RefreshPreview(); }); b.Background = Ui.Brush(themes[i, 1]); b.Foreground = Ui.Brush(themes[i, 2]); b.Margin = new Thickness(0, 0, 8, 0); colors.Children.Add(b); } panel.Children.Add(colors);
            StackPanel custom = Ui.Row(); custom.Margin = new Thickness(0, 8, 0, 0);
            custom.Children.Add(Ui.Button("背景色…", false, delegate { bg = ChooseColor(bg); RefreshPreview(); })); Ui.Gap(custom, 8);
            custom.Children.Add(Ui.Button("文字色…", false, delegate { fg = ChooseColor(fg); RefreshPreview(); })); Ui.Gap(custom, 8);
            custom.Children.Add(Ui.Button("强调色…", false, delegate { accent = ChooseColor(accent); RefreshPreview(); })); panel.Children.Add(custom);
            Ui.Label(panel, "背景不透明度 · 0% 时只保留文字与按钮"); bgValue = Ui.Text("", 12, Ui.Muted); backgroundAlpha = MakeSlider(data.BackgroundOpacity * 100); panel.Children.Add(backgroundAlpha); panel.Children.Add(bgValue);
            Ui.Label(panel, "整体不透明度 · 文字和按钮一起调整"); opacityValue = Ui.Text("", 12, Ui.Muted); opacity = MakeSlider(data.Opacity * 100); opacity.Minimum = 20; panel.Children.Add(opacity); panel.Children.Add(opacityValue);
            preview = new Border { Height = 70, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 15, 0, 0), BorderBrush = Ui.Brush("#465168"), BorderThickness = new Thickness(1) }; previewText = Ui.Text("25:00  预览", 28, Ui.Ink); previewText.HorizontalAlignment = HorizontalAlignment.Center; preview.Child = previewText; panel.Children.Add(preview);
            backgroundAlpha.ValueChanged += delegate { RefreshPreview(); }; opacity.ValueChanged += delegate { RefreshPreview(); };
            topmost = new CheckBox { Content = "悬浮置顶，显示在其他普通窗口上方", IsChecked = data.Topmost, Foreground = Ui.Ink, Margin = new Thickness(0, 16, 0, 8) }; if (!appearanceOnly) panel.Children.Add(topmost);
            if (!appearanceOnly) panel.Children.Add(new TextBlock { Text = "修改计时方式或时长会停止并重置计时；其他设置保留进度。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
            StackPanel footer = new StackPanel { Margin = new Thickness(26, 8, 26, 18) }; Grid.SetRow(footer, 1); root.Children.Add(footer);
            if (applyAll != null) footer.Children.Add(new TextBlock { Text = appearanceOnly ? "统一当前组的配色与透明度，并用于新窗口。各窗口的名称、时长和进度保留。" : "“外观应用全部”立即统一当前组窗口的配色与透明度。", FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            error = Ui.Text("", 12, Ui.Brush("#FFAE9B")); error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(0, 0, 0, 7); footer.Children.Add(error);
            StackPanel actions = Ui.Row(); actions.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(actions);
            if (applyAll != null && !appearanceOnly) { actions.Children.Add(Ui.Button("外观应用全部", false, ApplyAll)); Ui.Gap(actions, 10); }
            var cancel = Ui.Button("取消", false, delegate { DialogResult = false; }); cancel.IsCancel = true; actions.Children.Add(cancel); Ui.Gap(actions, 10);
            var save = Ui.Button(appearanceOnly ? "应用到所有窗口" : "保存设置", true, Save); save.IsDefault = true; actions.Children.Add(save); RefreshPreview();
            Loaded += delegate { MaxHeight = SystemParameters.WorkArea.Height - 30; Height = Math.Min(Height, MaxHeight); };
        }
        public void RefreshCueRows() {
            if (cueRows == null) return;
            List<int> points; string failure;
            if (!ReminderTimes.TryParse(reminderTimes.Text, false, data.Duration, out points, out failure)) return;
            if (cueRows.Children.Count > 0 && cuePickers.Keys.OrderBy(v => v).SequenceEqual(points)) return;
            foreach (var pair in cuePickers) cueSelections[pair.Key] = pair.Value.Selected;
            cueRows.Children.Clear(); cuePickers.Clear();
            foreach (int point in points) {
                SoundSelection selected; if (!cueSelections.TryGetValue(point, out selected)) selected = new SoundSelection { Inherit = true };
                var label = Ui.Text("已计时 " + (point / 60).ToString("00") + ":" + (point % 60).ToString("00"), 12, Ui.Muted); label.Margin = new Thickness(0, 9, 0, 5); cueRows.Children.Add(label);
                var picker = new SoundPicker(soundLibrary, audio, false, true, "ReminderCue" + point, "试听", selected, () => reminderSound.Selected);
                cueRows.Children.Add(picker); cuePickers.Add(point, picker);
            }
            if (points.Count == 0) cueRows.Children.Add(Ui.Text("先在上方填写时间点，例如 5, 7, 15。", 11, Ui.Muted));
        }
        static Slider MakeSlider(double value) { return new Slider { Minimum = 0, Maximum = 100, Value = value, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 3, 0, 3) }; }
        string ChooseColor(string initial) {
            using (var dialog = new Forms.ColorDialog()) {
                Color color = (Color)ColorConverter.ConvertFromString(initial); dialog.Color = Drawing.Color.FromArgb(color.R, color.G, color.B); dialog.FullOpen = true;
                if (dialog.ShowDialog(new NativeOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK) return "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
            } return initial;
        }
        void RefreshPreview() {
            if (preview == null || opacity == null) return;
            Color color = (Color)ColorConverter.ConvertFromString(bg); color.A = (byte)(255 * backgroundAlpha.Value / 100); preview.Background = new SolidColorBrush(color); preview.Opacity = opacity.Value / 100; previewText.Foreground = Ui.Brush(fg);
            bgValue.Text = ((int)backgroundAlpha.Value) + "%"; opacityValue.Text = ((int)opacity.Value) + "%";
        }
        void Save() {
            if (appearanceOnly) { ApplyAll(); DialogResult = true; return; }
            error.Foreground = Ui.Brush("#FFAE9B");
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "请填写窗口名称。"; name.Focus(); return; }
            int m, s; int duration = data.Duration;
            if (mode.SelectedIndex == 0) {
                if (!int.TryParse(minutes.Text, out m) || !int.TryParse(seconds.Text, out s) || m < 0 || m > 99999 || s < 0 || s > 59 || m * 60 + s < 1) { error.Text = "请输入有效时长：分 0–99999，秒 0–59，总时长至少 1 秒。"; return; }
                duration = m * 60 + s;
            }
            List<int> reminderPoints; string reminderError;
            if (!ReminderTimes.TryParse(reminderTimes.Text, mode.SelectedIndex == 0 && remindersEnabled.IsChecked == true, duration, out reminderPoints, out reminderError)) {
                if (remindersEnabled.IsChecked == true) { error.Text = reminderError; reminderTimes.Focus(); return; }
                reminderPoints = new List<int>(data.ReminderSeconds ?? new List<int>());
            }
            if (remindersEnabled.IsChecked == true && reminderPoints.Count == 0) { error.Text = "启用分段提示后，请至少填写一个时间点，例如 5, 7, 15。"; reminderTimes.Focus(); return; }
            RefreshCueRows();
            var defaultSound = reminderSound.Selected; var endSound = completionSound.Selected;
            var cues = new List<ReminderCue>();
            foreach (int point in reminderPoints) {
                SoundPicker picker; SoundSelection selected;
                if (cuePickers.TryGetValue(point, out picker)) selected = picker.Selected;
                else if (!cueSelections.TryGetValue(point, out selected)) selected = new SoundSelection { Inherit = true };
                if (!selected.Inherit) cues.Add(new ReminderCue { Seconds = point, Tone = selected.Tone, CustomSoundId = selected.CustomSoundId });
            }
            var soundIds = cues.Select(c => c.CustomSoundId).Concat(new[] { defaultSound.CustomSoundId, endSound.CustomSoundId }).Where(id => !string.IsNullOrEmpty(id)).Distinct();
            foreach (var id in soundIds) if (soundLibrary.Find(id) == null) { error.Text = "有自定义提示音不可用，请重新选择音频或改用内置音效。"; return; }
            data.Name = name.Text.Trim(); data.Countdown = mode.SelectedIndex == 0; data.Duration = duration;
            data.RemindersEnabled = remindersEnabled.IsChecked == true; data.ReminderSeconds = reminderPoints; data.ReminderStyle = reminderStyle.SelectedIndex;
            data.ReminderTone = AlarmAudio.ValidReminderTone(defaultSound.Tone); data.ReminderCustomSoundId = defaultSound.CustomSoundId;
            data.CompletionTone = AlarmAudio.ValidCompletionTone(endSound.Tone); data.CompletionCustomSoundId = endSound.CustomSoundId;
            data.ReminderCues = cues; data.CustomSounds = soundLibrary.Sounds.Select(clip => clip.Copy()).ToList();
            data.Background = bg; data.Foreground = fg; data.Accent = accent; data.BackgroundOpacity = backgroundAlpha.Value / 100; data.Opacity = opacity.Value / 100; data.Topmost = topmost.IsChecked == true; data.Sound = sound.IsChecked == true; DialogResult = true;
        }
        void ApplyAll() {
            if (applyAll == null) return;
            var appearance = new TimerAppearance { Background = bg, Foreground = fg, Accent = accent, BackgroundOpacity = backgroundAlpha.Value / 100, Opacity = opacity.Value / 100 };
            int count = applyAll(appearance); appearance.ApplyTo(data);
            error.Foreground = Ui.Green; error.Text = "已应用到 " + count + " 个窗口，新建窗口也会沿用。";
        }
        sealed class NativeOwner : Forms.IWin32Window { public IntPtr Handle { get; private set; } public NativeOwner(IntPtr handle) { Handle = handle; } }
    }

    public sealed class GroupNameDialog : Window {
        readonly TextBox input;
        readonly TextBlock error;
        public string GroupName { get; private set; }
        public GroupNameDialog(Window owner, string title, string initialName, string actionLabel, Func<string, string> validate) {
            Owner = owner; Title = title + " · 浮光计时"; Width = 440; Height = 290; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Topmost = true; ShowInTaskbar = false; Background = Ui.Brush("#101925"); FontFamily = new FontFamily("Microsoft YaHei UI");
            var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) }; Content = panel;
            panel.Children.Add(Ui.Text(title, 21, Ui.Ink)); Ui.Label(panel, "分组名称，例如：跨文化大赛");
            input = Ui.Input(initialName); input.MaxLength = 40; panel.Children.Add(input);
            error = new TextBlock { FontSize = 12, Foreground = Ui.Brush("#FFAE9B"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 10), MinHeight = 18 }; panel.Children.Add(error);
            var actions = Ui.Row(); actions.HorizontalAlignment = HorizontalAlignment.Right; panel.Children.Add(actions);
            var cancel = Ui.Button("取消", false, delegate { DialogResult = false; }); cancel.IsCancel = true; actions.Children.Add(cancel); Ui.Gap(actions, 10);
            var save = Ui.Button(actionLabel, true, delegate {
                string name = input.Text.Trim(); string failure = validate(name);
                if (failure != null) { error.Text = failure; input.Focus(); return; }
                GroupName = name; DialogResult = true;
            }); save.IsDefault = true; actions.Children.Add(save);
            Loaded += delegate { input.Focus(); input.SelectAll(); };
        }
    }

    public sealed class AboutWindow : Window {
        public AboutWindow(Window owner) {
            Owner = owner; Title = "关于浮光计时"; Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Ui.Brush("#101925"); FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false;
            var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) }; Content = panel;
            panel.Children.Add(Ui.Text("FLOAT / TIMER", 11, Ui.Green));
            var title = Ui.Text("浮光计时", 28, Ui.Ink); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 8, 0, 6); panel.Children.Add(title);
            panel.Children.Add(Ui.Text("多情境悬浮计时器 · 免安装版", 13, Ui.Muted));
            var details = new StackPanel { Margin = new Thickness(0, 20, 0, 20) }; panel.Children.Add(details);
            details.Children.Add(Ui.Text("当前版本：" + typeof(AboutWindow).Assembly.GetName().Version.ToString(3), 15, Ui.Ink));
            var maker = Ui.Text("制作者：金波", 15, Ui.Ink); maker.Margin = new Thickness(0, 8, 0, 0); details.Children.Add(maker);
            var credits = new TextBlock { FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
            credits.Text = "前台铃：定制音色，时长约 0.6 秒。"; details.Children.Add(credits);
            var close = Ui.Button("关闭", false, delegate { Close(); }); close.HorizontalAlignment = HorizontalAlignment.Right; close.MinWidth = 80; close.IsDefault = true; close.IsCancel = true; panel.Children.Add(close);
        }
    }

    public sealed class Manager : Window {
        readonly Host host;
        readonly StackPanel list;
        readonly TextBlock count, note;
        readonly List<Action> updateRows = new List<Action>();
        readonly ComboBox groupPicker;
        readonly TextBlock groupInfo;
        readonly CheckBox autoArrange;
        readonly Button resetAll;
        bool syncingArrangement;
        string lastActiveGroupId;
        public Manager(Host host) {
            this.host = host; Title = "浮光计时 · 多情境悬浮计时器";
            // Size before CenterScreen runs so the native caption never starts above the screen.
            double availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 24), availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 24);
            Width = Math.Min(560, availableWidth); Height = Math.Min(940, availableHeight); MinWidth = Math.Min(480, availableWidth); MinHeight = Math.Min(600, availableHeight);
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
            Background = Ui.Brush("#101925"); FontFamily = new FontFamily("Microsoft YaHei UI"); WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Grid root = new Grid { Margin = new Thickness(28, 25, 28, 20) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel header = new StackPanel(); root.Children.Add(header);
            header.Children.Add(Ui.Text("FLOAT / TIMER", 11, Ui.Green)); var heading = Ui.Text("浮光计时", 30, Ui.Ink); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 7, 0, 5); header.Children.Add(heading);
            header.Children.Add(Ui.Text("每一种情境，都有自己的时间。", 13, Ui.Muted));
            var groupCard = new Border { Background = Ui.Panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 18, 0, 0) }; header.Children.Add(groupCard);
            var groupBody = new StackPanel(); groupCard.Child = groupBody; groupBody.Children.Add(Ui.Text("情境分组", 12, Ui.Muted));
            var groupRow = new Grid { Margin = new Thickness(0, 8, 0, 8) }; groupRow.ColumnDefinitions.Add(new ColumnDefinition()); groupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); groupBody.Children.Add(groupRow);
            groupPicker = new ComboBox { FontSize = 14, Padding = new Thickness(8, 7, 8, 7), DisplayMemberPath = "DisplayName", Margin = new Thickness(0, 0, 10, 0) }; groupRow.Children.Add(groupPicker);
            var activate = Ui.Button("启用分组", true, delegate { var group = SelectedGroup(); if (group != null) host.ActivateGroup(group.Id); }); Grid.SetColumn(activate, 1); groupRow.Children.Add(activate);
            var groupActions = new WrapPanel(); groupBody.Children.Add(groupActions);
            var newGroup = Ui.Button("新建组", true, delegate { host.NewGroupWithDialog(); }); newGroup.ToolTip = "新建一个空组并切换到该组，再添加计时器"; groupActions.Children.Add(newGroup);
            var copyGroup = Ui.Button("复制当前组", false, delegate { host.SaveGroupWithDialog(); }); copyGroup.ToolTip = "以当前正在使用的组为基础，复制一组后单独修改"; groupActions.Children.Add(copyGroup);
            groupActions.Children.Add(Ui.Button("重命名", false, delegate { var group = SelectedGroup(); if (group != null) host.RenameGroupWithDialog(group.Id); }));
            groupActions.Children.Add(Ui.Button("删除组", false, delegate { var group = SelectedGroup(); if (group != null) host.DeleteGroupWithDialog(group.Id); }));
            foreach (FrameworkElement action in groupActions.Children) action.Margin = new Thickness(0, 0, 8, 4);
            groupInfo = new TextBlock { Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0), LineHeight = 17 }; groupBody.Children.Add(groupInfo);
            groupPicker.SelectionChanged += delegate { UpdateGroupInfo(); };
            var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 8) }; header.Children.Add(actions);
            actions.Children.Add(Ui.Button("＋ 添加计时器", true, delegate { host.Add(); })); actions.Children.Add(Ui.Button("显示全部", false, delegate { host.ShowAll(); })); actions.Children.Add(Ui.Button("全部暂停", false, delegate { host.PauseAll(); }));
            resetAll = Ui.Button("全部重置", false, delegate { host.ResetAll(); }); resetAll.ToolTip = "当前组全部恢复初始时间并停止，清除到时提示，包括已隐藏的计时器"; actions.Children.Add(resetAll);
            actions.Children.Add(Ui.Button("统一外观", false, delegate { host.EditAppearance(); }));
            foreach (FrameworkElement action in actions.Children) action.Margin = new Thickness(0, 0, 10, 8);
            var arrangement = Ui.Row(); arrangement.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(arrangement);
            arrangement.Children.Add(Ui.Button("右侧竖排", false, delegate { host.ArrangeCurrentGroup(); }));
            autoArrange = new CheckBox { Content = "按本组顺序自动排列", Foreground = Ui.Ink, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "启用分组、调整顺序或删除窗口后自动重排；新增只摆放新窗口。手动拖动或缩放会关闭自动排列。" }; arrangement.Children.Add(autoArrange);
            autoArrange.Checked += delegate { if (!syncingArrangement) host.ArrangeCurrentGroup(); };
            autoArrange.Unchecked += delegate { if (!syncingArrangement) host.SetAutoArrangement(false); };
            count = Ui.Text("", 12, Ui.Muted); count.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(count);
            ScrollViewer scroll = new ScrollViewer { Name = "TimerListScroll", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly, CanContentScroll = false, Background = Brushes.Transparent }; Grid.SetRow(scroll, 1); root.Children.Add(scroll); list = new StackPanel(); scroll.Content = list;
            StackPanel footer = new StackPanel(); Grid.SetRow(footer, 2); root.Children.Add(footer);
            note = new TextBlock { Text = "用 ↑ ↓ 调整环节顺序；右侧竖排使用本面板所在屏幕。\n窗口 × 仅隐藏；从本组移除请点击列表中的“删除”。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 17, 0, 12), LineHeight = 19 }; footer.Children.Add(note);
            StackPanel bottom = Ui.Row(); footer.Children.Add(bottom); bottom.Children.Add(Ui.Button("隐藏到托盘", false, delegate { Hide(); })); Ui.Gap(bottom, 10); bottom.Children.Add(Ui.Button("退出程序", false, delegate { host.Exit(); })); Ui.Gap(bottom, 10); bottom.Children.Add(Ui.Button("关于", false, delegate { new AboutWindow(this).ShowDialog(); }));
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!host.Exiting) { e.Cancel = true; Hide(); } };
            Loaded += delegate { EnsureOnScreen(); };
        }
        public void EnsureOnScreen() {
            if (WindowState != WindowState.Normal) return;
            Rect area = Ui.WorkAreaFor(this);
            double margin = Math.Min(12, Math.Min(area.Width, area.Height) / 4);
            double availableWidth = Math.Max(1, area.Width - 2 * margin), availableHeight = Math.Max(1, area.Height - 2 * margin);
            MinWidth = Math.Min(480, availableWidth); MinHeight = Math.Min(600, availableHeight);
            Width = Math.Max(MinWidth, Math.Min(double.IsNaN(Width) ? 560 : Width, availableWidth)); Height = Math.Max(MinHeight, Math.Min(double.IsNaN(Height) ? 940 : Height, availableHeight));
            double left = double.IsNaN(Left) ? area.Left + (area.Width - Width) / 2 : Left;
            double top = double.IsNaN(Top) ? area.Top + (area.Height - Height) / 2 : Top;
            Left = Math.Max(area.Left + margin, Math.Min(area.Right - margin - Width, left));
            Top = Math.Max(area.Top + margin, Math.Min(area.Bottom - margin - Height, top));
        }
        public void Refresh() {
            string selectedId = SelectedGroup() == null ? null : SelectedGroup().Id;
            bool activeChanged = lastActiveGroupId != host.Store.ActiveGroupId;
            groupPicker.ItemsSource = null; groupPicker.ItemsSource = host.Store.Groups;
            groupPicker.SelectedItem = host.Store.Groups.FirstOrDefault(g => g.Id == (activeChanged ? host.Store.ActiveGroupId : selectedId)) ?? host.Store.ActiveGroup;
            lastActiveGroupId = host.Store.ActiveGroupId; UpdateGroupInfo();
            syncingArrangement = true; autoArrange.IsChecked = host.Store.ActiveGroup.ArrangeRight; syncingArrangement = false;
            list.Children.Clear(); updateRows.Clear(); count.Text = "当前：" + host.Store.ActiveGroup.Name + " · " + host.Windows.Count + " 个计时器";
            resetAll.IsEnabled = host.Windows.Count > 0;
            if (!string.IsNullOrEmpty(host.Store.Warning)) note.Text = host.Store.Warning;
            if (host.Windows.Count == 0) { list.Children.Add(new TextBlock { Text = "“" + host.Store.ActiveGroup.Name + "”还没有计时器。\n点击上方“＋ 添加计时器”，设置本组的第一个环节。", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, Margin = new Thickness(0, 25, 0, 0), LineHeight = 22 }); }
            foreach (TimerWindow timer in host.Windows) {
                var item = timer;
                int index = host.Windows.IndexOf(timer);
                Border card = new Border { Background = Ui.Panel, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(16, 14, 16, 14) }; list.Children.Add(card);
                Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); card.Child = grid;
                var arrows = Ui.Row(); arrows.VerticalAlignment = VerticalAlignment.Center; arrows.Margin = new Thickness(0, 10, 8, 0); Grid.SetRow(arrows, 1); grid.Children.Add(arrows);
                var up = Ui.Button("↑", false, delegate { host.MoveTimer(item, -1); }); up.Padding = new Thickness(0); up.MinHeight = 30; up.Width = 28; up.IsEnabled = index > 0; up.ToolTip = "上移：" + item.Engine.Data.Name; up.Tag = item.Engine.Data.Id + ":up"; arrows.Children.Add(up);
                var down = Ui.Button("↓", false, delegate { host.MoveTimer(item, 1); }); down.Padding = new Thickness(0); down.MinHeight = 30; down.Width = 28; down.Margin = new Thickness(6, 0, 0, 0); down.IsEnabled = index < host.Windows.Count - 1; down.ToolTip = "下移：" + item.Engine.Data.Name; down.Tag = item.Engine.Data.Id + ":down"; arrows.Children.Add(down);
                StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumnSpan(text, 2); grid.Children.Add(text); TextBlock name = Ui.Text((index + 1).ToString("00") + " · " + timer.Engine.Data.Name, 16, Ui.Ink); name.FontWeight = FontWeights.SemiBold; name.ToolTip = timer.Engine.Data.Name; text.Children.Add(name);
                TextBlock detail = Ui.Text("", 12, Ui.Muted); detail.Margin = new Thickness(0, 7, 0, 0); text.Children.Add(detail);
                Action update = delegate { detail.Text = (item.Engine.Data.Countdown ? "倒计时" : "正计时") + " · " + item.Engine.Display + " · " + item.Engine.Status + (item.IsVisible ? "" : " · 已隐藏"); }; updateRows.Add(update); update();
                StackPanel buttons = Ui.Row(); buttons.VerticalAlignment = VerticalAlignment.Center; buttons.HorizontalAlignment = HorizontalAlignment.Right; buttons.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(buttons, 1); Grid.SetColumn(buttons, 1); grid.Children.Add(buttons);
                Button show = Ui.Button("显示", false, delegate { item.Engine.Data.Visible = true; item.Show(); Ui.KeepOnScreen(item); item.Activate(); host.Changed(); }); show.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(show);
                Button settings = Ui.Button("设置", false, delegate { host.Edit(item); }); settings.Margin = new Thickness(6, 0, 0, 0); buttons.Children.Add(settings);
                Button delete = Ui.Button("删除", false, delegate { host.Delete(item, this); }); delete.Margin = new Thickness(14, 0, 0, 0); delete.Foreground = Ui.Brush("#FFB4A8"); delete.Tag = item.Engine.Data.Id + ":delete"; delete.ToolTip = "从“" + host.Store.ActiveGroup.Name + "”移除此计时器，其他分组不受影响"; buttons.Children.Add(delete);
            }
        }
        public void ShowNotice(string text, bool error) { note.Text = text; note.Foreground = error ? Ui.Brush("#FFAE9B") : Ui.Muted; }
        TimerGroup SelectedGroup() { return groupPicker.SelectedItem as TimerGroup; }
        void UpdateGroupInfo() {
            var group = SelectedGroup(); if (group == null) { groupInfo.Text = ""; return; }
            int count = group.Id == host.Store.ActiveGroupId ? host.Windows.Count : group.Timers.Count;
            groupInfo.Text = group.Id == host.Store.ActiveGroupId
                ? count + " 个计时器 · 当前使用中，添加与修改自动保存。" + (count == 0 ? "点击下方“添加计时器”开始配置。" : "")
                : count + " 个计时器 · 点击“启用分组”后切换，恢复初始时间并保持停止。";
        }
        public void UpdateStatus() { foreach (var update in updateRows) update(); }
    }

    public sealed class Host {
        public readonly List<TimerWindow> Windows = new List<TimerWindow>();
        public readonly AlarmAudio Audio = new AlarmAudio();
        public readonly Store Store;
        public Manager Manager;
        public bool Exiting;
        readonly DispatcherTimer maintenance;
        Forms.NotifyIcon tray;
        bool dirty;
        DateTime lastChange, lastSnapshot;
        readonly EventWaitHandle wake;
        public Host(string statePath, bool testing, EventWaitHandle wake) {
            this.wake = wake; Store = new Store(statePath);
            var loaded = Store.Load();
            if (loaded == null) { var first = new TimerData(); Store.DefaultAppearance.ApplyTo(first); loaded = new List<TimerData> { first }; }
            Manager = new Manager(this);
            foreach (var data in loaded) { data.Normalize(); var window = new TimerWindow(this, data); Windows.Add(window); }
            Manager.Refresh();
            if (!testing) SetupTray();
            maintenance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; maintenance.Tick += delegate {
                if (wake != null && wake.WaitOne(0)) ShowManager();
                if (dirty && (DateTime.UtcNow - lastChange).TotalMilliseconds > 600) Save();
                if ((DateTime.UtcNow - lastSnapshot).TotalSeconds >= 5) { if (Windows.Any(w => w.Engine.Running)) Save(); lastSnapshot = DateTime.UtcNow; }
                if (Manager.IsVisible && !Application.Current.Windows.OfType<Editor>().Any()) Manager.UpdateStatus();
            }; maintenance.Start();
            foreach (var timer in Windows) if (timer.Engine.Data.Visible) timer.Show();
            Manager.Show();
            RestoreAutomaticLayoutIfNeeded();
        }
        void SetupTray() {
            tray = new Forms.NotifyIcon { Text = "浮光计时 · 双击打开管理面板", Visible = true };
            try { tray.Icon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = Drawing.SystemIcons.Application; }
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开管理面板", null, delegate { ShowManager(); }); menu.Items.Add("添加计时器", null, delegate { Add(); }); menu.Items.Add("显示全部计时器", null, delegate { ShowAll(); }); menu.Items.Add("全部暂停", null, delegate { PauseAll(); }); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出程序", null, delegate { Exit(); }); tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowManager(); };
        }
        public void ShowManager() {
            Manager.EnsureOnScreen(); Manager.Show();
            if (Manager.WindowState == WindowState.Minimized) Manager.WindowState = WindowState.Normal;
            Manager.EnsureOnScreen();
            Manager.Activate(); Manager.Topmost = true; Manager.Topmost = false; Manager.Refresh();
        }
        public void ShowAll() { foreach (var timer in Windows) { timer.Engine.Data.Visible = true; timer.Show(); Ui.KeepOnScreen(timer); } Changed(); }
        public void PauseAll() { foreach (var timer in Windows) { timer.Engine.Pause(); timer.Update(); } Changed(); }
        public void ResetAll() {
            if (Windows.Count == 0) { Manager.ShowNotice("当前组还没有计时器，请先添加。", false); return; }
            Audio.Stop();
            foreach (var timer in Windows) { timer.Engine.Reset(); timer.Update(); }
            Changed(); Save();
            Manager.ShowNotice("已重置“" + Store.ActiveGroup.Name + "”的 " + Windows.Count + " 个计时器并停止，点击各窗口的“开始”即可进行下一轮。", false);
        }
        public bool ArrangeCurrentGroup() { return ArrangeCurrentGroup(Ui.WorkAreaFor(Manager)); }
        void RestoreAutomaticLayoutIfNeeded() {
            if (!Store.ActiveGroup.ArrangeRight || Windows.Count == 0) return;
            Rect area = Ui.WorkAreaFor(Manager);
            if (Windows.Any(w => !area.Contains(new Rect(w.Left, w.Top, w.Width, w.Height)))) ArrangeCurrentGroup(area);
        }
        public bool ArrangeCurrentGroup(Rect workArea) {
            if (Windows.Count == 0) { Manager.ShowNotice("当前组还没有计时器，请先添加。", true); return false; }
            List<Rect> slots;
            try { slots = WindowArrangement.Plan(workArea, Windows.Count, Windows.Max(w => w.Width), Windows.Max(w => w.Height)); }
            catch (InvalidOperationException ex) { Manager.ShowNotice(ex.Message, true); return false; }
            for (int i = 0; i < Windows.Count; i++) {
                var window = Windows[i]; var slot = slots[i]; var data = window.Engine.Data;
                data.Left = slot.Left; data.Top = slot.Top; data.Width = slot.Width; data.Height = slot.Height; data.Visible = true;
                window.Width = slot.Width; window.Height = slot.Height; window.Left = slot.Left; window.Top = slot.Top; window.Show();
            }
            Store.ActiveGroup.ArrangeRight = true; Changed(); Save();
            int columns = slots.Select(r => r.Left).Distinct().Count();
            Manager.ShowNotice("已按本组顺序排列 " + Windows.Count + " 个窗口（" + columns + " 列）。" + (columns > 1 ? "单列放不下时会向左分列，避免超出屏幕。" : "可用 ↑ ↓ 调整先后顺序。"), false); return true;
        }
        public void SetAutoArrangement(bool enabled) {
            if (enabled) { ArrangeCurrentGroup(); return; }
            Store.ActiveGroup.ArrangeRight = false; Changed(); Save();
        }
        public void ManualLayoutChanged(TimerWindow window) {
            if (!Windows.Contains(window)) return;
            bool wasAutomatic = Store.ActiveGroup.ArrangeRight;
            Store.ActiveGroup.ArrangeRight = false; Changed(wasAutomatic); Save();
            if (wasAutomatic) Manager.ShowNotice("已保留手动布局并关闭自动排列。新增计时器不会移动已有窗口；需要重排时点击“右侧竖排”。", false);
        }
        public bool MoveTimer(TimerWindow window, int direction) {
            int index = Windows.IndexOf(window), target = index + direction;
            if ((direction != -1 && direction != 1) || index < 0 || target < 0 || target >= Windows.Count) return false;
            Windows.RemoveAt(index); Windows.Insert(target, window);
            if (Store.ActiveGroup.ArrangeRight && ArrangeCurrentGroup()) return true;
            Changed(); Save(); return true;
        }
        Window DialogOwner() { return Manager.IsVisible ? (Window)Manager : Windows.FirstOrDefault(w => w.IsVisible); }
        public string ValidateGroupName(string name, string exceptId) {
            if (string.IsNullOrWhiteSpace(name)) return "请填写分组名称。";
            if (name.Trim().Length > 40) return "分组名称最多 40 个字符。";
            if (Store.Groups.Any(g => g.Id != exceptId && string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) return "已有同名分组，请换一个名称。";
            return null;
        }
        public TimerGroup CreateGroupFromCurrent(string name) {
            string error = ValidateGroupName(name, null); if (error != null) throw new ArgumentException(error);
            Save();
            var group = new TimerGroup { Name = name.Trim(), Timers = Windows.Select(w => w.Engine.Data.Copy(false)).ToList(), ArrangeRight = Store.ActiveGroup.ArrangeRight };
            Store.Groups.Add(group); Store.ActiveGroupId = group.Id; Changed(); Save(); return group;
        }
        public TimerGroup CreateEmptyGroup(string name) {
            string error = ValidateGroupName(name, null); if (error != null) throw new ArgumentException(error);
            var group = new TimerGroup { Name = name.Trim() };
            Store.Groups.Add(group); ActivateGroup(group.Id);
            Manager.ShowNotice("已新建并切换到“" + group.Name + "”。点击“＋ 添加计时器”配置本组环节，修改会自动保存。", false);
            return group;
        }
        public void NewGroupWithDialog() {
            var dialog = new GroupNameDialog(DialogOwner(), "新建空白分组", "", "创建分组", name => ValidateGroupName(name, null));
            if (dialog.ShowDialog() == true) CreateEmptyGroup(dialog.GroupName);
        }
        public void SaveGroupWithDialog() {
            var dialog = new GroupNameDialog(DialogOwner(), "复制当前组", "", "复制分组", name => ValidateGroupName(name, null));
            if (dialog.ShowDialog() == true) CreateGroupFromCurrent(dialog.GroupName);
        }
        public void RenameGroup(string id, string name) {
            string error = ValidateGroupName(name, id); if (error != null) throw new ArgumentException(error);
            Store.Groups.First(g => g.Id == id).Name = name.Trim(); Changed(); Save();
        }
        public void RenameGroupWithDialog(string id) {
            var group = Store.Groups.First(g => g.Id == id);
            var dialog = new GroupNameDialog(DialogOwner(), "重命名分组", group.Name, "保存名称", name => ValidateGroupName(name, id));
            if (dialog.ShowDialog() == true) RenameGroup(id, dialog.GroupName);
        }
        public void ActivateGroup(string id) {
            var group = Store.Groups.First(g => g.Id == id);
            foreach (var window in Windows) window.Engine.Pause(); Save(); Audio.Stop();
            var prepared = group.Timers.Select(t => t.Copy(true)).ToList();
            Exiting = true;
            try { foreach (var window in Windows) { window.StopTimer(); window.Close(); } Windows.Clear(); }
            finally { Exiting = false; }
            Store.ActiveGroupId = id;
            foreach (var data in prepared) { var window = new TimerWindow(this, data); Windows.Add(window); window.Show(); Ui.KeepOnScreen(window); }
            RestoreAutomaticLayoutIfNeeded(); Changed(); Save();
        }
        public bool DeleteGroup(string id) {
            var group = Store.Groups.First(g => g.Id == id);
            if (Store.Groups.Count <= 1) return false;
            if (Store.ActiveGroupId == id) ActivateGroup(Store.Groups.First(g => g.Id != id).Id);
            Store.Groups.Remove(group); Changed(); Save(); return true;
        }
        public void DeleteGroupWithDialog(string id) {
            var group = Store.Groups.First(g => g.Id == id);
            if (Store.Groups.Count <= 1) { MessageBox.Show(DialogOwner(), "至少需要保留一个分组。你可以重命名当前组，或先新建另一个组。", "保留一个分组", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string text = "删除“" + group.Name + "”及其计时器配置？其他分组会保留。";
            if (Store.ActiveGroupId == id) text += "\n删除后会启用另一个分组，并保持暂停。";
            if (MessageBox.Show(DialogOwner(), text, "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) DeleteGroup(id);
        }
        public void Add() {
            var data = CreateTimerData();
            var editor = new Editor(data, Manager.IsVisible ? (Window)Manager : Windows.FirstOrDefault(w => w.IsVisible), Audio, ApplyAppearanceToAll);
            if (editor.ShowDialog() == true) {
                Rect slot; bool placed = WindowArrangement.TryPlaceNew(Ui.WorkAreaFor(Manager), Windows.Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)), data.Width, data.Height, out slot);
                if (placed) { data.Left = slot.Left; data.Top = slot.Top; }
                var window = new TimerWindow(this, data); Windows.Add(window); window.Show(); Changed(); Save();
                Manager.ShowNotice(placed ? "已添加新计时器，原有窗口位置和尺寸保持不变。" : "已添加新计时器，原有窗口保持不变。当前屏幕空位不足，可手动摆放新窗口或点击“右侧竖排”。", false);
            }
        }
        public TimerData CreateTimerData() {
            int n = Windows.Count;
            Rect area = Ui.WorkAreaFor(Manager);
            var data = new TimerData { Name = "计时器 " + (n + 1), Left = area.Left + 80 + (n % 6) * 32, Top = area.Top + 80 + (n % 6) * 32 };
            if (n > 0 && Store.ActiveGroup.ArrangeRight) { data.Width = Windows[n - 1].Width; data.Height = Windows[n - 1].Height; }
            Store.DefaultAppearance.ApplyTo(data); return data;
        }
        public int ApplyAppearanceToAll(TimerAppearance appearance) {
            appearance.Normalize(); Store.DefaultAppearance = appearance.Copy();
            foreach (var window in Windows) { appearance.ApplyTo(window.Engine.Data); window.ApplyStyle(); }
            Changed(); Save(); return Windows.Count;
        }
        public void EditAppearance() {
            var data = new TimerData(); Store.DefaultAppearance.ApplyTo(data);
            new Editor(data, Manager.IsVisible ? (Window)Manager : Windows.FirstOrDefault(w => w.IsVisible), Audio, ApplyAppearanceToAll, true).ShowDialog();
        }
        public void Edit(TimerWindow timer) {
            int oldDuration = timer.Engine.Data.Duration; bool oldMode = timer.Engine.Data.Countdown;
            bool oldRemindersEnabled = timer.Engine.Data.RemindersEnabled; int[] oldReminderPoints = (timer.Engine.Data.ReminderSeconds ?? new List<int>()).ToArray();
            var dialog = new Editor(timer.Engine.Data, timer.IsVisible ? (Window)timer : Manager, Audio, ApplyAppearanceToAll);
            if (dialog.ShowDialog() == true) {
                if (oldDuration != timer.Engine.Data.Duration || oldMode != timer.Engine.Data.Countdown) timer.Engine.Reset();
                else if (oldRemindersEnabled != timer.Engine.Data.RemindersEnabled || !oldReminderPoints.SequenceEqual(timer.Engine.Data.ReminderSeconds)) timer.Engine.RefreshReminderSchedule();
                timer.ClearReminderNotice(); timer.ApplyStyle(); Changed(); Save();
            }
        }
        public void Delete(TimerWindow timer, Window owner = null) {
            if (!Windows.Contains(timer)) return;
            if (MessageBox.Show(owner ?? (timer.IsVisible ? (Window)timer : Manager), "从“" + Store.ActiveGroup.Name + "”中删除“" + timer.Engine.Data.Name + "”？\n此操作会移除此计时器及其进度，其他分组不受影响。", "删除计时器", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            timer.Engine.Pause(); timer.StopTimer(); Windows.Remove(timer); timer.Hide();
            bool wasExiting = Exiting; Exiting = true; try { timer.Close(); } finally { Exiting = wasExiting; }
            if (Store.ActiveGroup.ArrangeRight && Windows.Count > 0 && ArrangeCurrentGroup()) return; Changed(); Save();
        }
        public void Changed() { Changed(true); }
        public void Changed(bool refresh) { dirty = true; lastChange = DateTime.UtcNow; if (refresh && Manager != null && Manager.IsVisible) Manager.Refresh(); }
        public void Save() { foreach (var timer in Windows) timer.Engine.Snapshot(); if (Store.Save(Windows.Select(w => w.Engine.Data))) dirty = false; }
        public void Exit() {
            if (Exiting) return; Exiting = true; Save(); maintenance.Stop();
            foreach (var timer in Windows) { timer.StopTimer(); timer.Close(); }
            Manager.Close(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Audio.Dispose(); Application.Current.Shutdown();
        }
    }

    public static class Program {
        [STAThread] public static int Main(string[] args) {
            if (args.Length > 0 && args[0] == "--self-test") return Tests.Run(args.Length > 1 ? args[1] : "self-test.txt");
            bool smoke = args.Length > 0 && args[0] == "--smoke-test";
            string smokeDir = smoke && args.Length > 1 ? args[1] : null;
            Mutex mutex = null; EventWaitHandle wake = null;
            if (!smoke) {
                string id = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                bool created; mutex = new Mutex(true, "Local\\FloatTimer-v1-" + id, out created);
                wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FloatTimer-wake-v1-" + id);
                if (!created) { wake.Set(); wake.Dispose(); mutex.Dispose(); return 0; }
            }
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Host host = null;
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FloatTimer.error.log"), e.Exception.ToString()); } catch { }
                MessageBox.Show("运行出现问题。错误详情已尝试保存到 FloatTimer.error.log。\n" + e.Exception.Message, "浮光计时"); e.Handled = true; if (host != null) host.Exit(); else app.Shutdown(1);
            };
            app.Startup += delegate {
                host = new Host(smoke ? Path.Combine(smokeDir, "smoke.settings.json") : null, smoke, wake);
                if (smoke) Tests.Smoke(host, smokeDir);
            };
            app.SessionEnding += delegate { if (host != null) host.Save(); };
            int code = app.Run(); if (wake != null) wake.Dispose(); if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } return code;
        }
    }

    public static class Tests {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static int Run(string path) {
            List<string> lines = new List<string>();
            try {
                double now = 0; var d = new TimerData { Duration = 65 }; var e = new TimerEngine(d, () => now);
                Assert(e.Display == "01:05", "initial countdown"); e.Start(); now = 1.2; Assert(e.Display == "01:04", "ceil remaining"); e.Pause(); now = 30; Assert(e.Display == "01:04", "pause freezes"); e.Start(); now = 31.3; Assert(e.Display == "01:03", "resume offset"); e.Reset(); Assert(e.Display == "01:05" && !e.Running, "reset stops"); now = 40; Assert(e.Display == "01:05" && !e.Running, "reset stays stopped"); e.Start(); now = 110; Assert(e.Tick() && e.Completed && !e.Running && e.Display == "00:00", "countdown completes once"); Assert(!e.Tick(), "completion is unique"); e.Start(); Assert(!e.Running, "completed start disabled"); e.Reset(); Assert(!e.Running && !e.Completed && e.Display == "01:05", "reset completed timer"); lines.Add("PASS countdown: start, pause, resume, completion, reset and no automatic restart");
                now = 0; e = new TimerEngine(new TimerData { Countdown = false }, () => now); e.Start(); now = 3601.8; Assert(e.Display == "60:01", "unbounded minutes"); e.Pause(); now += 900; Assert(e.Display == "60:01", "stopwatch pause"); e.Snapshot(); var restored = new TimerEngine(e.Data, () => now); Assert(!restored.Running && restored.Display == "60:01", "restore paused"); lines.Add("PASS stopwatch: total minutes, pause, persisted elapsed");
                e.Reset(); Assert(!e.Running && e.Display == "00:00", "stopwatch reset stops"); e.Start(); now += 86400; Assert(e.Display == "1440:00", "long clock jump"); lines.Add("PASS long elapsed interval and stopwatch reset");
                string statePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "test-state.json"); var store = new Store(statePath);
                var saved = new TimerData { Name = "课堂问答 · 中文", Duration = 1, BackgroundOpacity = 0, Opacity = .2, Left = -500, Width = 700, Visible = false };
                Assert(store.Save(new[] { saved }), "save"); var loaded = store.Load(); Assert(loaded.Count == 1 && loaded[0].Name == saved.Name && loaded[0].BackgroundOpacity == 0 && !loaded[0].Visible && loaded[0].Width == 700, "roundtrip"); Assert(store.Save(new TimerData[0]) && store.Load().Count == 0, "empty collection persists"); File.Delete(statePath); if (File.Exists(statePath + ".bak")) File.Delete(statePath + ".bak"); lines.Add("PASS persistence: Chinese, transparency, geometry, empty collection");
                var invalid = new TimerData { Duration = -3, Width = double.NaN, Background = "invalid", Opacity = 0, Elapsed = double.PositiveInfinity }; invalid.Normalize(); Assert(invalid.Duration == 1 && invalid.Width == 390 && invalid.Background == "#152A32" && invalid.Opacity == .2 && invalid.Elapsed == 0, "validation"); lines.Add("PASS invalid settings normalization");
                File.WriteAllLines(path, lines); return 0;
            } catch (Exception ex) { lines.Add("FAIL " + ex); File.WriteAllLines(path, lines); return 1; }
        }
        public static void Capture(Window window, string path) {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) png.Save(stream);
        }
        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T) yield return (T)child;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }
        static void Click(Window window, string text) {
            if (window is TimerWindow) window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Button button = Descendants<Button>(window).First(b => Convert.ToString(b.Content) == text);
            Assert(button.IsEnabled, "button enabled: " + text); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        public static void Smoke(Host host, string dir) {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) }; int step = 0;
            timer.Tick += delegate {
                try {
                    if (step == 0) {
                        var first = host.Windows[0]; first.Engine.Data.Name = "阅读专注"; first.Engine.Data.Duration = 2; first.Engine.Data.Sound = false; first.ApplyStyle(); first.Engine.Reset(); first.Engine.Start();
                        var second = new TimerWindow(host, new TimerData { Name = "课堂讨论", Countdown = false, Background = "#29213D", Accent = "#C8ADFF", Left = 550, Top = 120 }); host.Windows.Add(second); second.Show(); second.Engine.Start();
                        host.Manager.Refresh(); Capture(second, Path.Combine(dir, "timer-stopwatch.png"));
                        var editor = new Editor(first.Engine.Data, host.Manager, host.Audio); editor.Show(); Capture(editor, Path.Combine(dir, "settings.png")); editor.Close();
                    } else if (step == 1) {
                        var second = host.Windows[1]; second.Engine.Pause(); Assert(!second.Engine.Running && second.Engine.Elapsed >= 1, "UI stopwatch pause");
                        second.Width = 640; second.Height = 360; second.Engine.Data.BackgroundOpacity = .35; second.Engine.Data.Opacity = .75; second.ApplyStyle(); Assert(second.Topmost && second.Opacity == .75, "topmost and opacity"); Capture(second, Path.Combine(dir, "timer-transparent.png"));
                    } else if (step == 2) {
                        var first = host.Windows[0]; first.Engine.Tick(); first.Update(); Assert(first.Engine.Completed && first.Engine.Display == "00:00", "UI countdown completion"); Capture(first, Path.Combine(dir, "timer-completed.png"));
                        first.Engine.Data.Duration = 1500; first.Engine.Reset(); first.ApplyStyle(); first.Update(); Capture(first, Path.Combine(dir, "timer-countdown.png"));
                        host.Manager.Refresh(); Capture(host.Manager, Path.Combine(dir, "manager.png"));
                        host.Save(); Assert(host.Store.Load().Count == 2, "multi timer save");
                    } else if (step == 3) {
                        var first = host.Windows[0]; Click(first, "开始"); Assert(first.Engine.Running, "start button"); Click(first, "暂停"); Assert(!first.Engine.Running, "pause button"); Click(first, "重置"); Assert(!first.Engine.Running && first.Engine.Display == "25:00", "reset button stops and restores");
                        Click(first, "×"); Assert(!first.IsVisible && !first.Engine.Data.Visible, "hide button"); host.ShowAll(); Assert(first.IsVisible && first.Engine.Data.Visible, "show all restores");
                        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                            var editor = Application.Current.Windows.OfType<Editor>().Single(); editor.UpdateLayout();
                            var inputs = Descendants<TextBox>(editor).ToList(); inputs[0].Text = "新建测试情境"; inputs[1].Text = "0"; inputs[2].Text = "0"; Click(editor, "保存设置");
                            Assert(editor.IsVisible, "zero duration rejected"); inputs[1].Text = "3"; Click(editor, "保存设置");
                        }));
                        host.Add(); Assert(host.Windows.Count == 3 && host.Windows[2].Engine.Data.Duration == 180 && host.Windows[2].Engine.Data.Name == "新建测试情境", "add via modal editor");
                        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                            var editor = Application.Current.Windows.OfType<Editor>().Single(); editor.UpdateLayout();
                            Descendants<ComboBox>(editor).Single(c => c.Name == "TimerMode").SelectedIndex = 1; Click(editor, "保存设置");
                        }));
                        host.Edit(first); Assert(!first.Engine.Data.Countdown && !first.Engine.Running && first.Engine.Display == "00:00", "mode switch resets");
                        host.PauseAll(); Assert(host.Windows.All(w => !w.Engine.Running), "pause all");
                        host.Save(); Assert(host.Store.Load().Count == 3 && host.Store.Warning == null, "repeated multi timer save");
                        File.WriteAllText(Path.Combine(dir, "smoke-test.txt"), "PASS WPF windows, independent timers, countdown completion, resize, topmost, transparency, settings render, start/pause/reset button events, hide/show all, modal add and validation, mode-switch reset, pause all, repeated persistence"); timer.Stop(); host.Exit();
                    }
                    step++;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "smoke-test.txt"), "FAIL " + ex); timer.Stop(); host.Exit(); }
            }; timer.Start();
        }
    }
}

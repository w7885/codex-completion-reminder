using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexCompletionReminder
{
    internal static class ReminderKinds
    {
        public const string Completion = "completion";
        public const string Permission = "permission";
        public const string Choice = "choice";
    }

    internal static class ReminderChannels
    {
        public const string Custom = "custom";
        public const string Native = "native";
        public const string Both = "both";
        public const string Off = "off";
    }

    [Serializable]
    internal sealed class ReminderSettings
    {
        public bool Enabled = true;
        public bool ExperimentalChoiceMonitoring = false;
        public string CompletionChannel = ReminderChannels.Custom;
        public string PermissionChannel = ReminderChannels.Custom;
        public string ChoiceChannel = ReminderChannels.Custom;

        public string CompletionTitle = "任务已完成";
        public string CompletionDescription = "按任意键或点击鼠标关闭提醒";
        public string PermissionTitle = "任务需确认";
        public string PermissionDescription = "Codex 正在等待你的选择";
        public string ChoiceTitle = "任务需确认";
        public string ChoiceDescription = "Codex 正在等待你的选择";

        public int Width = 370;
        public int Height = 108;
        public string BackgroundColor = "#18212B";
        public string AccentColor = "#54D7C0";
        public string TitleColor = "#FFFFFF";
        public string DescriptionColor = "#CFD7E0";

        public static ReminderSettings Defaults()
        {
            return new ReminderSettings();
        }

        public ReminderSettings Clone()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Deserialize<ReminderSettings>(serializer.Serialize(this));
        }

        public void Normalize()
        {
            CompletionChannel = NormalizeChannel(CompletionChannel, ReminderChannels.Custom);
            PermissionChannel = NormalizeChannel(PermissionChannel, ReminderChannels.Custom);
            ChoiceChannel = NormalizeChannel(ChoiceChannel, ReminderChannels.Custom);
            CompletionTitle = NormalizeText(CompletionTitle, "任务已完成", 100);
            CompletionDescription = NormalizeText(CompletionDescription, "按任意键或点击鼠标关闭提醒", 180);
            PermissionTitle = NormalizeText(PermissionTitle, "任务需确认", 100);
            PermissionDescription = NormalizeText(PermissionDescription, "Codex 正在等待你的选择", 180);
            ChoiceTitle = NormalizeText(ChoiceTitle, "任务需确认", 100);
            ChoiceDescription = NormalizeText(ChoiceDescription, "Codex 正在等待你的选择", 180);
            Width = Math.Max(300, Math.Min(720, Width));
            Height = Math.Max(96, Math.Min(360, Height));
            BackgroundColor = NormalizeColor(BackgroundColor, "#18212B");
            AccentColor = NormalizeColor(AccentColor, "#54D7C0");
            TitleColor = NormalizeColor(TitleColor, "#FFFFFF");
            DescriptionColor = NormalizeColor(DescriptionColor, "#CFD7E0");
        }

        public string GetChannel(string kind)
        {
            if (kind == ReminderKinds.Completion) return CompletionChannel;
            if (kind == ReminderKinds.Permission) return PermissionChannel;
            if (kind == ReminderKinds.Choice) return ChoiceChannel;
            return ReminderChannels.Off;
        }

        public string GetTitle(string kind)
        {
            if (kind == ReminderKinds.Completion) return CompletionTitle;
            if (kind == ReminderKinds.Permission) return PermissionTitle;
            if (kind == ReminderKinds.Choice) return ChoiceTitle;
            return "Codex";
        }

        public string GetDescription(string kind)
        {
            if (kind == ReminderKinds.Completion) return CompletionDescription;
            if (kind == ReminderKinds.Permission) return PermissionDescription;
            if (kind == ReminderKinds.Choice) return ChoiceDescription;
            return "";
        }

        public bool WantsCustomPopup(string kind, bool choiceSourceAvailable)
        {
            if (!Enabled) return false;
            string channel = GetChannel(kind);
            bool selected = channel == ReminderChannels.Custom || channel == ReminderChannels.Both;
            if (!selected) return false;
            // Preview requires an explicitly enabled read-only source; no implicit native fallback.
            if (kind == ReminderKinds.Choice && !choiceSourceAvailable) return false;
            return true;
        }

        private static string NormalizeChannel(string value, string fallback)
        {
            string channel = (value ?? "").Trim().ToLowerInvariant();
            if (channel == ReminderChannels.Custom || channel == ReminderChannels.Native ||
                channel == ReminderChannels.Both || channel == ReminderChannels.Off)
                return channel;
            return fallback;
        }

        private static string NormalizeText(string value, string fallback, int maximumLength)
        {
            string text = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0) text = fallback;
            if (text.Length > maximumLength) text = text.Substring(0, maximumLength);
            return text;
        }

        private static string NormalizeColor(string value, string fallback)
        {
            string color = (value ?? "").Trim();
            if (color.Length == 7 && color[0] == '#')
            {
                for (int i = 1; i < color.Length; i++)
                {
                    if (!Uri.IsHexDigit(color[i])) return fallback;
                }
                return color.ToUpperInvariant();
            }
            return fallback;
        }
    }

    internal static class SettingsStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly string DefaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexCompletionReminder");
        private static string directoryPath = DefaultDirectory;
        public static string DirectoryPath { get { return directoryPath; } private set { directoryPath = value; } }
        public static string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }
        public static bool IsPreviewProfile { get; private set; }
        public static void UsePreviewDirectory(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("Preview requires an absolute profile directory.");
            string full = Path.GetFullPath(directory);
            if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(DefaultDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Preview cannot use production settings.");
            DirectoryPath = full;
            IsPreviewProfile = true;
        }

        public static ReminderSettings Load()
        {
            return LoadFrom(FilePath);
        }

        internal static ReminderSettings LoadFrom(string path)
        {
            if (!File.Exists(path)) return ReminderSettings.Defaults();
            try
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    ReminderSettings settings = Serializer.Deserialize<ReminderSettings>(reader.ReadToEnd());
                    if (settings == null) return ReminderSettings.Defaults();
                    settings.Normalize();
                    return settings;
                }
            }
            catch
            {
                try
                {
                    string backup = path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(path, backup, false);
                }
                catch { }
                return ReminderSettings.Defaults();
            }
        }

        public static void Save(ReminderSettings settings)
        {
            SaveTo(FilePath, settings);
        }

        internal static void SaveTo(string path, ReminderSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            settings.Normalize();
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            string json = Serializer.Serialize(settings);
            using (StreamWriter writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(json);
                writer.Flush();
            }
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                File.Replace(temporary, path, backup, true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }

    internal sealed class ReminderEvent
    {
        public string Kind;
        public string EventId;

        public static bool TryParse(string line, out ReminderEvent reminderEvent)
        {
            reminderEvent = null;
            if (String.IsNullOrWhiteSpace(line) || Encoding.UTF8.GetByteCount(line) >= SecureTransport.MaxMessageBytes) return false;
            string[] parts = line.Split(new char[] { '\t' });
            if (parts.Length != 2 || !SecureTransport.ValidId(parts[1])) return false;
            string kind = parts[0];
            if (kind != ReminderKinds.Completion && kind != ReminderKinds.Permission && kind != ReminderKinds.Choice)
                return false;
            reminderEvent = new ReminderEvent { Kind = kind, EventId = parts[1].Trim() };
            return true;
        }
    }

    internal sealed class PendingNotificationQueue
    {
        private int completions;
        private int confirmations;

        public string CurrentKind
        {
            get
            {
                if (confirmations > 0) return ReminderKinds.Permission;
                if (completions > 0) return ReminderKinds.Completion;
                return null;
            }
        }

        public int Count(string kind)
        {
            if (kind == ReminderKinds.Permission) return confirmations;
            if (kind == ReminderKinds.Completion) return completions;
            return 0;
        }

        public void Add(string kind)
        {
            if (kind == ReminderKinds.Permission) confirmations++;
            else if (kind == ReminderKinds.Completion) completions++;
        }

        public void Dismiss(string kind)
        {
            if (kind == ReminderKinds.Permission) confirmations = 0;
            else if (kind == ReminderKinds.Completion) completions = 0;
        }

        public void Clear()
        {
            confirmations = 0;
            completions = 0;
        }
    }
}

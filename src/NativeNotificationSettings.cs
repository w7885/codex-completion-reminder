using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexCompletionReminder
{
    internal static class NativeNotificationSettings
    {
        public static string ConfigPath
        {
            get
            {
                if (SettingsStore.IsPreviewProfile) return Path.Combine(SettingsStore.DirectoryPath, "codex", "config.toml");
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                return Path.Combine(home, "config.toml");
            }
        }
        public static void Save(ReminderSettings settings) { SaveTo(settings, SettingsStore.FilePath, ConfigPath); }

        internal static void SaveTo(ReminderSettings settings, string settingsPath, string configPath)
        {
            settings.Normalize();
            byte[] original = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            string text = original == null ? "" : File.ReadAllText(configPath, new UTF8Encoding(false, true));
            string next = Edit(text, settings);
            bool bom = original != null && original.Length >= 3 && original[0] == 239 && original[1] == 187 && original[2] == 191;
            byte[] prefix = new UTF8Encoding(bom).GetPreamble(), body = new UTF8Encoding(false).GetBytes(next);
            byte[] content = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, content, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, content, prefix.Length, body.Length);
            bool changed = !Equal(original, content);
            if (changed)
            {
                byte[] current = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
                if (!Equal(original, current)) throw new IOException("Codex 配置同时被修改，请重试。");
                if (original != null) File.WriteAllBytes(configPath + ".codex-reminder-notifications.bak", original);
                AtomicWrite(configPath, content);
            }
            try { SettingsStore.SaveTo(settingsPath, settings); }
            catch
            {
                // Roll back our own update only, never another editor's changes.
                if (changed && File.Exists(configPath) && Equal(File.ReadAllBytes(configPath), content))
                {
                    if (original == null) File.Delete(configPath);
                    else AtomicWrite(configPath, original);
                }
                throw;
            }
        }
        internal static string Edit(string text, ReminderSettings settings)
        {
            settings.Normalize();
            if (text.Contains("\"\"\"") || text.Contains("'''") ||
                Regex.IsMatch(text, @"(?m)^\s*desktop\s*[.=]") ||
                Regex.IsMatch(text, @"(?m)^\s*\[\s*[""']desktop"))
                throw new InvalidDataException("复杂 TOML 格式无法安全编辑，配置未修改。");
            Dictionary<string, string> values = new Dictionary<string, string>();
            values.Add("notifications-turn-mode", NativeSelected(settings.CompletionChannel) ? "\"always\"" : "\"off\"");
            values.Add("notifications-permissions-enabled", NativeSelected(settings.PermissionChannel) ? "true" : "false");
            values.Add("notifications-questions-enabled", NativeSelected(settings.ChoiceChannel) ? "true" : "false");
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            int start = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                if (Regex.IsMatch(lines[i], @"^\s*\[\s*desktop\s*\]\s*(?:#.*)?$"))
                {
                    if (start >= 0) throw new InvalidDataException("重复的 [desktop] 配置，未修改通知设置。");
                    start = i;
                }
                else if (start >= 0 && end == lines.Count && Regex.IsMatch(lines[i], @"^\s*\[")) end = i;
            }
            if (start < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length != 0) lines.Add("");
                lines.Add("[desktop]");
                foreach (KeyValuePair<string, string> pair in values) lines.Add(pair.Key + " = " + pair.Value);
                lines.Add("");
            }
            else
            {
                HashSet<string> found = new HashSet<string>();
                for (int i = start + 1; i < end; i++)
                    foreach (KeyValuePair<string, string> pair in values)
                    {
                        string key = Regex.Escape(pair.Key);
                        if (!Regex.IsMatch(lines[i], "^\\s*(?:" + key + "|\"" + key + "\"|'" + key + "')\\s*=")) continue;
                        if (!found.Add(pair.Key)) throw new InvalidDataException("重复的通知配置键，未修改设置。");
                        Match match = Regex.Match(lines[i], "^(\\s*(?:" + key + "|\"" + key + "\"|'" + key + "')\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|true|false)(\\s*(?:#.*)?)$");
                        if (!match.Success) throw new InvalidDataException("通知配置格式无法安全修改，请先检查 Codex 配置。");
                        lines[i] = match.Groups[1].Value + pair.Value + match.Groups[2].Value;
                    }
                foreach (KeyValuePair<string, string> pair in values)
                    if (!found.Contains(pair.Key)) lines.Insert(end++, pair.Key + " = " + pair.Value);
            }
            return String.Join(newline, lines);
        }
        private static bool NativeSelected(string channel) { return channel == ReminderChannels.Native || channel == ReminderChannels.Both; }
        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".reminder-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

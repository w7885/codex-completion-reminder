using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexCompletionReminder
{
    internal static class CodexReminderHook
    {
        private const string PipeName = "CodexCompletionReminderV4";
        private const int ConnectTimeoutMs = 120;
        private const int TotalWaitMs = 1400;

        private static int Main()
        {
            string json = Console.In.ReadToEnd();
            string eventName = ReadJsonString(json, "hook_event_name");
            string kind;
            string eventId;
            if (eventName == "Stop")
            {
                kind = ReminderKinds.Completion;
                eventId = ReadJsonString(json, "turn_id");
                if (String.IsNullOrWhiteSpace(eventId)) eventId = Guid.NewGuid().ToString("N");
            }
            else if (eventName == "PermissionRequest")
            {
                kind = ReminderKinds.Permission;
                // PermissionRequest can fire more than once during a turn; don't collapse distinct prompts.
                eventId = Guid.NewGuid().ToString("N");
            }
            else
            {
                if (eventName == "Stop") Console.Out.WriteLine("{\"continue\":true}");
                return 0;
            }

            try
            {
                ReminderSettings settings = SettingsStore.Load();
                string channel = settings.GetChannel(kind);
                if (settings.Enabled && (channel == ReminderChannels.Custom || channel == ReminderChannels.Both))
                    Send(kind, eventId);
            }
            catch { }

            // Stop requires a continuation response. PermissionRequest must remain undecided so
            // Codex displays its normal approval prompt and applies its normal policy.
            if (eventName == "Stop") Console.Out.WriteLine("{\"continue\":true}");
            return 0;
        }

        private static string ReadJsonString(string json, string name)
        {
            string pattern = "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"";
            Match match = Regex.Match(json ?? "", pattern);
            if (!match.Success) return "";
            try { return Regex.Unescape(match.Groups[1].Value); }
            catch { return match.Groups[1].Value; }
        }

        private static void Send(string kind, string eventId)
        {
            string line = kind + "\t" + eventId.Replace("\r", "").Replace("\n", "") + "\n";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TotalWaitMs);
            bool started = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        pipe.Connect(ConnectTimeoutMs);
                        byte[] data = Encoding.UTF8.GetBytes(line);
                        pipe.Write(data, 0, data.Length);
                        pipe.Flush();
                        return;
                    }
                }
                catch
                {
                    if (!started)
                    {
                        started = true;
                        StartHelper();
                    }
                    Thread.Sleep(70);
                }
            }
        }

        private static void StartHelper()
        {
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string appPath = Path.Combine(codexHome, "codex-reminder", "CodexReminder.exe");
            if (!File.Exists(appPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appPath,
                    WorkingDirectory = Path.GetDirectoryName(appPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }
    }
}

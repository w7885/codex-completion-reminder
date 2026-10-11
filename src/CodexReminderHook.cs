using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;

namespace CodexCompletionReminder
{
    internal static class CodexReminderHook
    {
        private const string PipeName = "CodexCompletionReminderV4";
        private const int ConnectTimeoutMs = 120;
        private const int TotalWaitMs = 1400;

        private static int Main(string[] args)
        {
            Record("bridge-started");
            bool diagnostic = Array.IndexOf(args, "--diagnostic") >= 0;
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
                Record("unrecognized-event");
                if (eventName == "Stop") Console.Out.WriteLine("{\"continue\":true}");
                return 0;
            }

            Record((diagnostic ? "manual-" : "") + "received-" + eventName);
            bool attempted = false;
            bool written = false;
            try
            {
                ReminderSettings settings = SettingsStore.Load();
                string channel = settings.GetChannel(kind);
                if (settings.Enabled && (channel == ReminderChannels.Custom || channel == ReminderChannels.Both))
                {
                    attempted = true;
                    written = Send(kind, eventId);
                }
                else Record("skipped-channel-or-disabled");
            }
            catch (Exception ex) { Record("settings-error-" + ex.GetType().Name); }

            // Stop requires a continuation response. PermissionRequest must remain undecided so
            // Codex displays its normal approval prompt and applies its normal policy.
            if (eventName == "Stop") Console.Out.WriteLine("{\"continue\":true}");
            // Lifecycle hooks must not affect approval or completion. Explicit manual
            // diagnostics additionally report transport failure via the exit code.
            return diagnostic && attempted && !written ? 1 : 0;
        }

        private static string ReadJsonString(string json, string name)
        {
            string pattern = "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"";
            Match match = Regex.Match(json ?? "", pattern);
            if (!match.Success) return "";
            try { return Regex.Unescape(match.Groups[1].Value); }
            catch { return match.Groups[1].Value; }
        }

        private static bool Send(string kind, string eventId)
        {
            string line = kind + "\t" + eventId.Replace("\r", "").Replace("\n", "") + "\n";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TotalWaitMs);
            bool started = false;
            string lastError = "unknown";
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
                        Record("pipe-written");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name;
                    if (!started)
                    {
                        started = true;
                        StartHelper();
                    }
                    Thread.Sleep(70);
                }
            }
            Record("pipe-error-" + lastError);
            Console.Error.WriteLine("Codex reminder event could not be sent (" + lastError + "). See hook-status.log.");
            return false;
        }

        private static void StartHelper()
        {
            if (IsIsolatedDesktop())
            {
                Record("helper-start-skipped-isolated-desktop");
                return;
            }
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
            catch (Exception ex) { Record("helper-start-error-" + ex.GetType().Name); }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);

        private static bool IsIsolatedDesktop()
        {
            StringBuilder name = new StringBuilder(256);
            int needed;
            return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out needed)
                && name.ToString().StartsWith("CodexSandboxDesktop", StringComparison.OrdinalIgnoreCase);
        }

        private static void Record(string status)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                string path = Path.Combine(SettingsStore.DirectoryPath, "hook-status.log");
                if (File.Exists(path) && new FileInfo(path).Length > 262144)
                    File.WriteAllText(path, "", new UTF8Encoding(false));
                // Only event type and transport status; no message, command, or input.
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + status + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}

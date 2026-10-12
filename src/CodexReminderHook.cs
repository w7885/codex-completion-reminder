using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;

namespace CodexCompletionReminder
{
    internal static class CodexReminderHook
    {
        private static string PipeName { get { return SecureTransport.PipeName; } }
        private const int ConnectTimeoutMs = 120;
        private const int TotalWaitMs = 1400;

        private static int Main(string[] args)
        {
            Record("bridge-started");
            bool diagnostic = Array.IndexOf(args, "--diagnostic") >= 0;
            string json;
            try { Console.InputEncoding = new UTF8Encoding(false, true); json = SecureTransport.ReadBounded(Console.In, SecureTransport.MaxHookChars).TrimStart('\uFEFF'); }
            catch { Record("rejected-input"); return 0; }
            string eventName;
            string turnId;
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer parser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength=SecureTransport.MaxHookChars, RecursionLimit=32 };
                System.Collections.Generic.Dictionary<string, object> payload = parser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                object value;
                if (payload == null || !payload.TryGetValue("hook_event_name", out value) || !(value is string)) throw new InvalidDataException();
                eventName = (string)value;
                turnId = payload.TryGetValue("turn_id", out value) && value is string ? (string)value : "";
            }
            catch (Exception ex) { Record("rejected-json-" + ex.GetType().Name); return 0; }
            string kind;
            string eventId;
            if (eventName == "Stop")
            {
                kind = ReminderKinds.Completion;
                eventId = turnId;
                if (String.IsNullOrWhiteSpace(eventId)) eventId = Guid.NewGuid().ToString("N");
            }
            else if (eventName == "PermissionRequest")
            {
                // Automatic review is not a real human wait. Never notify from this hook.
                Record("permission-signal-ignored");
                return 0;
            }
            else
            {
                Record("unrecognized-event");
                if (eventName == "Stop") Console.Out.WriteLine("{\"continue\":true}");
                return 0;
            }

            if (!SecureTransport.ValidId(eventId)) { Record("rejected-id"); Console.Out.WriteLine("{\"continue\":true}"); return 0; }
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
            if (Environment.GetEnvironmentVariable("CODEX_REMINDER_TEST_NO_START") == "1") return;
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

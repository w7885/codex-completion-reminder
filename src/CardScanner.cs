// Isolated read-only preview source. Does not read questions, options or editor values.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace CodexCompletionReminder
{
    internal static class CardScanner
    {
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder name, int length);
        private delegate bool EnumCallback(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private sealed class Output
        {
            public bool EnumerationComplete = true;
            public bool Unknown;
            public int MinimizedWindows;
            public List<string> WindowScopes = new List<string>();
            public List<CardRequest> Requests = new List<CardRequest>();
        }
        [MTAThread]
        private static int Main()
        {
            Output result = new Output();
            Dictionary<int, Process> processes = new Dictionary<int, Process>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("ChatGPT"))
                {
                    try
                    {
                        if (p.SessionId == Process.GetCurrentProcess().SessionId &&
                            p.MainModule.FileName.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            SameUser(p)) processes.Add(p.Id, p);
                        else p.Dispose();
                    }
                    catch { result.EnumerationComplete = false; p.Dispose(); }
                }
                bool ok = EnumWindows(delegate(IntPtr h, IntPtr unused)
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!processes.ContainsKey((int)pid)) return true;
                    StringBuilder className = new StringBuilder(128);
                    GetClassName(h,className,className.Capacity);
                    if (className.ToString() != "Chrome_WidgetWin_1") return true;
                    string scope = Hash(pid + ":" + h.ToInt64());
                    result.WindowScopes.Add(scope);
                    if (IsIconic(h)) { result.MinimizedWindows++; result.Unknown = true; return true; }
                    if (!IsWindowVisible(h)) { result.Unknown = true; return true; }
                    try
                    {
                        AutomationElement root = AutomationElement.FromHandle(h);
                        Scan(root, scope, result);
                    }
                    catch { result.Unknown = true; }
                    return true;
                }, IntPtr.Zero);
                if (!ok) result.EnumerationComplete = false;
            }
            catch { result.EnumerationComplete = false; result.Unknown = true; }
            finally { foreach (Process p in processes.Values) p.Dispose(); }
            // No complete request scopes: empty UIA content cannot distinguish a resolved card
            // from chat navigation. Closed windows alone are resolved by the supervisor.
            Console.WriteLine(new JavaScriptSerializer().Serialize(result));
            return 0;
        }
        private static bool SameUser(Process process)
        {
            IntPtr token;
            if (!OpenProcessToken(process.Handle, 8, out token)) return false;
            try { using (WindowsIdentity identity = new WindowsIdentity(token)) return identity.User.Equals(WindowsIdentity.GetCurrent().User); }
            finally { CloseHandle(token); }
        }
        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(value))).Replace("-", "").Substring(0,24);
        }
        private static Condition Buttons(params string[] names)
        {
            List<Condition> match = new List<Condition>();
            foreach (string name in names) match.Add(new PropertyCondition(AutomationElement.NameProperty, name));
            return new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true), new OrCondition(match.ToArray()));
        }
        private static readonly Condition Grant = Buttons("批准一次","允许一次","本次允许","批准","允许","Allow once","Approve once","Allow","Approve","Yes, proceed","是，继续");
        private static readonly Condition Deny = Buttons("拒绝","不允许","取消","Deny","Reject","Cancel","No, cancel","否，取消");
        // Send can be disabled while no option is selected. Do not inspect option labels.
        private static Condition ChoiceAction(string a, string b)
        {
            return new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty,a), new PropertyCondition(AutomationElement.NameProperty,b)));
        }
        private static void Scan(AutomationElement root, string scope, Output result)
        {
            HashSet<string> ids = new HashSet<string>();
            AutomationElementCollection grants = root.FindAll(TreeScope.Descendants, Grant);
            foreach (AutomationElement button in grants) AddAncestors(root, button, scope, ReminderKinds.Permission, ids, result);
            AutomationElementCollection radios = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            foreach (AutomationElement radio in radios) AddAncestors(root, radio, scope, ReminderKinds.Choice, ids, result);
            if (result.Requests.Count == 0) result.Unknown = true;
        }
        private static void AddAncestors(AutomationElement root, AutomationElement control, string scope, string kind, HashSet<string> ids, Output result)
        {
            AutomationElement group = control;
            for (int depth = 0; depth < 3 && result.Requests.Count < 128; depth++)
            {
                group = TreeWalker.ControlViewWalker.GetParent(group);
                if (group == null || group.Equals(root)) break;
                bool valid = kind == ReminderKinds.Permission
                    ? group.FindFirst(TreeScope.Descendants,Deny) != null
                    : group.FindFirst(TreeScope.Descendants,ChoiceAction("跳过","Skip")) != null &&
                      group.FindFirst(TreeScope.Descendants,ChoiceAction("发送","Send")) != null;
                if (!valid) continue;
                string id = Hash(scope + ":" + String.Join(".",group.GetRuntimeId()));
                if (ids.Add(kind + ":" + id)) result.Requests.Add(new CardRequest { Kind=kind, Id=id, Scope=scope });
                break;
            }
        }
    }
}

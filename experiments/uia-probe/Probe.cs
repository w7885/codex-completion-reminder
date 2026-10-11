// Read-only feasibility probe. Not included in the production build.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace CodexReminderUiaProbe
{
    internal static class Probe
    {
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [MTAThread]
        private static int Main(string[] args)
        {
            int seconds = args.Length > 0 ? Int32.Parse(args[0]) : 0;
            DateTime end = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(seconds, 60)));
            string previous = null;
            JavaScriptSerializer json = new JavaScriptSerializer();
            do
            {
                Snapshot snapshot = Read();
                string state = json.Serialize(snapshot);
                if (state != previous) { Console.WriteLine(state); previous = state; }
                if (DateTime.UtcNow >= end) break;
                Thread.Sleep(500);
            } while (true);
            return 0;
        }

        private static Snapshot Read()
        {
            Snapshot result = new Snapshot();
            HashSet<string> cards = new HashSet<string>();
            HashSet<string> approvals = new HashSet<string>();
            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle == IntPtr.Zero) continue;
                        string path = process.MainModule.FileName;
                        if (path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        result.Windows++;
                        if (IsIconic(process.MainWindowHandle)) result.MinimizedWindows++;
                        // Reacquire the provider every scan; no retained tree or text cache.
                        AutomationElement root = AutomationElement.FromHandle(process.MainWindowHandle);
                        // Filter on fixed approval action names in the provider. No
                        // complete window text or screenshot is ever requested.
                        AutomationElementCollection grants = root.FindAll(TreeScope.Descendants,
                            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                new PropertyCondition(AutomationElement.IsEnabledProperty, true), ApprovalNameCondition(true)));
                        foreach (AutomationElement grant in grants)
                        {
                            AutomationElement group = grant;
                            for (int depth = 0; depth < 3; depth++)
                            {
                                group = TreeWalker.ControlViewWalker.GetParent(group);
                                if (group == null || group.Equals(root)) break;
                                AutomationElement deny = group.FindFirst(TreeScope.Descendants,
                                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                        new PropertyCondition(AutomationElement.IsEnabledProperty, true), ApprovalNameCondition(false)));
                                if (deny == null) continue;
                                approvals.Add(Key(process.Id, group));
                                break;
                            }
                        }
                        AutomationElementCollection radios = root.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
                        result.RadioControls += radios.Count;
                        foreach (AutomationElement radio in radios)
                        {
                            AutomationElement ancestor = radio;
                            for (int depth = 0; depth < 3; depth++)
                            {
                                ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
                                if (ancestor == null || ancestor.Equals(root)) break;
                                if (!IsChoiceCard(ancestor)) continue;
                                // Runtime identifiers only. Never read question/option names.
                                cards.Add(Key(process.Id, ancestor));
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Unknown = true;
                        result.ErrorType = ex.GetType().Name;
                    }
                }
            }
            result.ChoiceCards = new List<string>(cards);
            result.ChoiceCards.Sort(StringComparer.Ordinal);
            result.ApprovalCards = new List<string>(approvals);
            result.ApprovalCards.Sort(StringComparer.Ordinal);
            if (result.MinimizedWindows > 0 && cards.Count == 0 && approvals.Count == 0)
            {
                result.Unknown = true;
                result.ErrorType = "MinimizedContentUnavailable";
            }
            return result;
        }

        private static string Key(int processId, AutomationElement element)
        {
            string raw = processId + ":" + String.Join(".", element.GetRuntimeId());
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(raw))).Replace("-", "").Substring(0, 16);
        }

        private static Condition ApprovalNameCondition(bool grant)
        {
            string[] labels = grant
                ? new string[] { "批准一次", "允许一次", "本次允许", "批准", "允许", "Allow once", "Approve once", "Allow", "Approve", "Yes, proceed", "是，继续" }
                : new string[] { "拒绝", "不允许", "取消", "Deny", "Reject", "Cancel", "No, cancel", "否，取消" };
            Condition[] conditions = new Condition[labels.Length];
            for (int i = 0; i < labels.Length; i++) conditions[i] = new PropertyCondition(AutomationElement.NameProperty, labels[i]);
            return new OrCondition(conditions);
        }

        private static bool IsChoiceCard(AutomationElement group)
        {
            bool skip = false, send = false;
            AutomationElementCollection buttons = group.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (buttons.Count > 20) return false;
            foreach (AutomationElement button in buttons)
            {
                string label = button.Current.Name;
                skip |= label == "跳过" || label == "Skip";
                send |= label == "发送" || label == "Send" || label == "Submit";
            }
            return skip && send;
        }

        private sealed class Snapshot
        {
            public int Windows;
            public int MinimizedWindows;
            public int RadioControls;
            public bool Unknown;
            public string ErrorType;
            public List<string> ChoiceCards;
            public List<string> ApprovalCards;
        }
    }
}

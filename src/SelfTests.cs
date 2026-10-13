using System;
using System.Web.Script.Serialization;

namespace CodexCompletionReminder
{
    internal static class SelfTests
    {
        public static int Run()
        {
            int passed = 0;
            try
            {
                ReminderSettings defaults = ReminderSettings.Defaults();
                Assert(defaults.Enabled, "default enabled");
                Assert(defaults.CompletionTitle == "任务已完成", "completion default title");
                Assert(defaults.ChoiceTitle == "任务需选择", "choice default title");
                passed++;

                using (ReminderSettingsForm form = new ReminderSettingsForm(defaults.Clone(), false))
                {
                    Assert(form.Text == "Codex 完成提醒器设置", "settings window initializes");
                    Assert(form.Controls.Find("experimentalChoiceMonitoring", true).Length == 1, "experimental source option is visible in settings");
                    Assert(form.Controls.Find("permissionChannel", true).Length == 0, "permission channel absent");
                    Assert(!ContainsApprovalUi(form), "approval controls and preview removed");
                    passed++;
                }

                ReminderSettings custom = ReminderSettings.Defaults();
                custom.Width = 900;
                custom.Height = 40;
                custom.BackgroundColor = "red";
                custom.ChoiceTitle = "  ";
                custom.Normalize();
                Assert(custom.Width == 720 && custom.Height == 96, "size bounds");
                Assert(custom.BackgroundColor == "#18212B", "invalid color fallback");
                Assert(custom.ChoiceTitle == "任务需选择", "empty title fallback");
                passed++;

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                custom.CompletionTitle = "Done";
                custom.CompletionChannel = ReminderChannels.Both;
                ReminderSettings restored = serializer.Deserialize<ReminderSettings>(serializer.Serialize(custom));
                restored.Normalize();
                Assert(restored.CompletionTitle == "Done" && restored.CompletionChannel == ReminderChannels.Both, "settings JSON roundtrip");
                passed++;

                string settingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexReminder-selftest-" + Guid.NewGuid().ToString("N"), "settings.json");
                try
                {
                    ReminderSettings toSave = ReminderSettings.Defaults();
                    toSave.Enabled = false;
                    toSave.ChoiceDescription = "Please review";
                    toSave.Width = 444;
                    SettingsStore.SaveTo(settingsPath, toSave);
                    ReminderSettings fromDisk = SettingsStore.LoadFrom(settingsPath);
                    Assert(!fromDisk.Enabled && fromDisk.ChoiceDescription == "Please review" && fromDisk.Width == 444,
                        "settings persist and reload");
                    passed++;
                }
                finally
                {
                    string settingsDirectory = System.IO.Path.GetDirectoryName(settingsPath);
                    if (System.IO.Directory.Exists(settingsDirectory)) System.IO.Directory.Delete(settingsDirectory, true);
                }

                ReminderEvent parsed;
                Assert(ReminderEvent.TryParse("choice\tturn-123", out parsed), "valid IPC event");
                Assert(parsed.Kind == ReminderKinds.Choice && parsed.EventId == "turn-123", "IPC event fields");
                Assert(!ReminderEvent.TryParse("unknown\tturn-123", out parsed), "unknown IPC event rejected");
                Assert(!ReminderEvent.TryParse("completion", out parsed), "malformed IPC event rejected");
                passed++;

                ReminderSettings routing = ReminderSettings.Defaults();
                Assert(routing.WantsCustomPopup(ReminderKinds.Completion, false), "completion custom route");
                routing.ChoiceChannel = ReminderChannels.Custom;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Choice, false), "unknown choice source suppresses custom without native fallback");
                Assert(routing.WantsCustomPopup(ReminderKinds.Choice, true), "choice custom route when source exists");
                routing.CompletionChannel = ReminderChannels.Native;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Completion, false), "native mode suppresses custom popup");
                routing.CompletionChannel = ReminderChannels.Off;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Completion, false), "off mode suppresses custom popup");
                routing.CompletionChannel = ReminderChannels.Both;
                Assert(routing.WantsCustomPopup(ReminderKinds.Completion, false), "both mode includes custom popup");
                routing.Enabled = false;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Completion, true), "global disable");
                passed++;

                PendingNotificationQueue queue = new PendingNotificationQueue();
                queue.Add(ReminderKinds.Completion);
                queue.Add(ReminderKinds.Completion);
                Assert(queue.CurrentKind == ReminderKinds.Completion && queue.Count(ReminderKinds.Completion) == 2,
                    "completion count is retained");
                queue.Add("permission");
                Assert(queue.Count("permission") == 0 && queue.Count(ReminderKinds.Completion) == 2,
                    "removed permission event cannot enter the queue");
                queue.Dismiss(ReminderKinds.Completion);
                Assert(queue.CurrentKind == null, "notification queue drains");
                passed++;

                Assert(!ReminderEvent.TryParse("permission\tturn-123", out parsed), "removed permission IPC event rejected");
                ReminderSettings legacy = serializer.Deserialize<ReminderSettings>("{\"PermissionChannel\":\"both\",\"PermissionTitle\":\"Old\",\"ChoiceTitle\":\"My choice\",\"CompletionChannel\":\"both\"}");
                Assert(legacy.ChoiceTitle == "My choice" && legacy.CompletionChannel == ReminderChannels.Both,
                    "legacy settings retain supported preferences");
                Assert(!serializer.Serialize(legacy).Contains("Permission"), "removed fields are not saved");
                passed += SafetyTests.Run();
                Console.WriteLine("Self-tests passed: {0} checks.", passed);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Self-test failed after {0} checks: {1}", passed, ex.Message);
                return 1;
            }
        }

        private static bool ContainsApprovalUi(System.Windows.Forms.Control control)
        {
            if (control.Text == "权限审批") return true;
            System.Windows.Forms.ComboBox combo = control as System.Windows.Forms.ComboBox;
            if (combo != null) foreach (object item in combo.Items)
                if (item is System.Collections.Generic.KeyValuePair<string,string> &&
                    ((System.Collections.Generic.KeyValuePair<string,string>)item).Key == "permission") return true;
            foreach (System.Windows.Forms.Control child in control.Controls) if (ContainsApprovalUi(child)) return true;
            return false;
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
        }
    }
}

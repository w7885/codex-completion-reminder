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
                Assert(defaults.PermissionTitle == "任务需确认", "permission default title");
                passed++;

                using (ReminderSettingsForm form = new ReminderSettingsForm(defaults.Clone(), false))
                {
                    Assert(form.Text == "Codex 完成提醒器设置", "settings window initializes");
                    passed++;
                }

                ReminderSettings custom = ReminderSettings.Defaults();
                custom.Width = 900;
                custom.Height = 40;
                custom.BackgroundColor = "red";
                custom.PermissionTitle = "  ";
                custom.Normalize();
                Assert(custom.Width == 720 && custom.Height == 96, "size bounds");
                Assert(custom.BackgroundColor == "#18212B", "invalid color fallback");
                Assert(custom.PermissionTitle == "任务需确认", "empty title fallback");
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
                    toSave.PermissionDescription = "Please review";
                    toSave.Width = 444;
                    SettingsStore.SaveTo(settingsPath, toSave);
                    ReminderSettings fromDisk = SettingsStore.LoadFrom(settingsPath);
                    Assert(!fromDisk.Enabled && fromDisk.PermissionDescription == "Please review" && fromDisk.Width == 444,
                        "settings persist and reload");
                    passed++;
                }
                finally
                {
                    string settingsDirectory = System.IO.Path.GetDirectoryName(settingsPath);
                    if (System.IO.Directory.Exists(settingsDirectory)) System.IO.Directory.Delete(settingsDirectory, true);
                }

                ReminderEvent parsed;
                Assert(ReminderEvent.TryParse("permission\tturn-123", out parsed), "valid IPC event");
                Assert(parsed.Kind == ReminderKinds.Permission && parsed.EventId == "turn-123", "IPC event fields");
                Assert(!ReminderEvent.TryParse("unknown\tturn-123", out parsed), "unknown IPC event rejected");
                Assert(!ReminderEvent.TryParse("completion", out parsed), "malformed IPC event rejected");
                passed++;

                ReminderSettings routing = ReminderSettings.Defaults();
                Assert(routing.WantsCustomPopup(ReminderKinds.Completion, false), "completion custom route");
                Assert(routing.WantsCustomPopup(ReminderKinds.Permission, false), "permission custom route");
                routing.ChoiceChannel = ReminderChannels.Custom;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Choice, false), "unknown choice source suppresses custom without native fallback");
                Assert(routing.WantsCustomPopup(ReminderKinds.Choice, true), "choice custom route when source exists");
                routing.PermissionChannel = ReminderChannels.Native;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Permission, false), "native mode suppresses custom popup");
                routing.PermissionChannel = ReminderChannels.Off;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Permission, false), "off mode suppresses custom popup");
                routing.PermissionChannel = ReminderChannels.Both;
                Assert(routing.WantsCustomPopup(ReminderKinds.Permission, false), "both mode includes custom popup");
                routing.Enabled = false;
                Assert(!routing.WantsCustomPopup(ReminderKinds.Completion, true), "global disable");
                passed++;

                PendingNotificationQueue queue = new PendingNotificationQueue();
                queue.Add(ReminderKinds.Completion);
                queue.Add(ReminderKinds.Completion);
                queue.Add(ReminderKinds.Permission);
                Assert(queue.CurrentKind == ReminderKinds.Permission && queue.Count(ReminderKinds.Completion) == 2,
                    "confirmation has priority while completion count is retained");
                queue.Dismiss(ReminderKinds.Permission);
                Assert(queue.CurrentKind == ReminderKinds.Completion && queue.Count(ReminderKinds.Completion) == 2,
                    "completion summary follows confirmation");
                queue.Dismiss(ReminderKinds.Completion);
                Assert(queue.CurrentKind == null, "notification queue drains");
                passed++;

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

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
        }
    }
}

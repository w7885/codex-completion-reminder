using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CodexCompletionReminder
{
    internal static class SafetyTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static int Run()
        {
            int groups=0;
            ReminderEvent message;
            Check(!ReminderEvent.TryParse("completion\tbad\tid",out message),"extra IPC field");
            Check(!ReminderEvent.TryParse("permission\tbad\rid",out message),"control characters");
            Check(!ReminderEvent.TryParse("completion\t"+new string('x',129),out message),"long IPC ID");
            Check(!ReminderEvent.TryParse(new string('x',513),out message),"oversized IPC");
            Check(!ReminderEvent.TryParse("Completion\tvalid",out message),"strict known kinds");
            Check(SecureTransport.ReadBounded(new StringReader(new string('a',65536)),65536).Length==65536,"bounded input limit");
            bool rejected=false;
            try { SecureTransport.ReadBounded(new StringReader(new string('a',65537)),65536); } catch(InvalidDataException) { rejected=true; }
            Check(rejected,"oversized hook");
            groups++;

            PipeSecurity acl=SecureTransport.Security();
            AuthorizationRuleCollection rules=acl.GetAccessRules(true,true,typeof(SecurityIdentifier));
            Check(acl.AreAccessRulesProtected && rules.Count==1,"exclusive pipe DACL");
            PipeAccessRule rule=(PipeAccessRule)rules[0];
            Check(rule.IdentityReference.Equals(WindowsIdentity.GetCurrent().User) && rule.AccessControlType==AccessControlType.Allow,"current user pipe");
            Check(SecureTransport.PipeName.EndsWith(WindowsIdentity.GetCurrent().User.Value),"user pipe namespace");
            // Exercise real asynchronous framing under the current user ACL, without a GUI.
            using(NamedPipeServerStream server=SecureTransport.CreateServer())
            using(NamedPipeClientStream client=new NamedPipeClientStream(".",SecureTransport.PipeName,PipeDirection.Out))
            {
                IAsyncResult connection=server.BeginWaitForConnection(null,null);
                client.Connect(500);
                server.EndWaitForConnection(connection);
                byte[] bytes=Encoding.UTF8.GetBytes("completion\ttest\n");
                client.Write(bytes,0,bytes.Length); client.Flush();
                Check(SecureTransport.ReadMessage(server)=="completion\ttest","real bounded framing");
            }
            using(NamedPipeServerStream server=SecureTransport.CreateServer())
            using(NamedPipeClientStream client=new NamedPipeClientStream(".",SecureTransport.PipeName,PipeDirection.Out))
            {
                IAsyncResult connection=server.BeginWaitForConnection(null,null);
                client.Connect(500); server.EndWaitForConnection(connection);
                rejected=false;
                try { SecureTransport.ReadMessage(server); } catch(IOException) { rejected=true; }
                Check(rejected,"silent sender timeout");
            }
            groups++;

            RequestTracker tracker=new RequestTracker();
            CardSnapshot first=new CardSnapshot();
            first.Requests.Add(new CardRequest { Kind=ReminderKinds.Permission,Id="p1",Scope="w1" });
            first.Requests.Add(new CardRequest { Kind=ReminderKinds.Choice,Id="c1",Scope="w2" });
            tracker.Apply(first); tracker.Apply(first);
            Check(tracker.WaitingCount==2 && tracker.Count(ReminderKinds.Permission)==1,"request dedupe");
            tracker.Dismiss(ReminderKinds.Permission); tracker.Apply(first);
            Check(tracker.WaitingCount==2 && tracker.Count(ReminderKinds.Permission)==0,"dismiss is not resolved");
            tracker.Apply(null); tracker.Apply(new CardSnapshot());
            Check(tracker.WaitingCount==2,"unknown or uncovered missing requests retained");
            CardSnapshot complete=new CardSnapshot(); complete.CompleteScopes.Add("w1");
            tracker.Apply(complete);
            Check(tracker.WaitingCount==1 && tracker.Count(ReminderKinds.Choice)==1,"resolved scope only");
            complete.CompleteScopes.Add("w2"); tracker.Apply(complete);
            Check(tracker.WaitingCount==0,"cancel/retract");
            tracker.Apply(first);
            Check(tracker.Count(ReminderKinds.Permission)==1,"new occurrence after authoritative disappearance");
            groups++;

            ReminderSettings settings=ReminderSettings.Defaults();
            foreach(string kind in new string[]{ReminderKinds.Completion,ReminderKinds.Permission,ReminderKinds.Choice})
            foreach(string channel in new string[]{ReminderChannels.Custom,ReminderChannels.Native,ReminderChannels.Both,ReminderChannels.Off})
            {
                settings.CompletionChannel=settings.PermissionChannel=settings.ChoiceChannel=channel;
                settings.Enabled=true;
                Check(settings.WantsCustomPopup(kind,true)==(channel=="custom"||channel=="both"),"channel routing");
                string edited=NativeNotificationSettings.Edit("[desktop]\nother = 42\n[tools]\nx = 1\n",settings);
                bool native=channel=="native"||channel=="both";
                Check(edited.Contains("notifications-turn-mode = \""+(native?"always":"off")+"\""),"native completion mapping");
                Check(edited.Contains("notifications-permissions-enabled = "+(native?"true":"false")) &&
                    edited.Contains("notifications-questions-enabled = "+(native?"true":"false")),"native request mapping");
                Check(edited.Contains("other = 42") && edited.Contains("[tools]\nx = 1"),"unrelated TOML preserved");
                settings.Enabled=false;
                Check(!settings.WantsCustomPopup(kind,true),"global disabled");
            }
            groups++;

            string temp=Path.Combine(Path.GetTempPath(),"CodexReminder-safety-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string config=Path.Combine(temp,"config.toml"), prefs=Path.Combine(temp,"settings.json");
                byte[] original=new UTF8Encoding(true).GetBytes("# keep\r\n[desktop]\r\nnotifications-turn-mode = \"always\" # note\r\n");
                byte[] bom=new UTF8Encoding(true).GetPreamble();
                byte[] all=new byte[bom.Length+original.Length];
                Buffer.BlockCopy(bom,0,all,0,bom.Length); Buffer.BlockCopy(original,0,all,bom.Length,original.Length);
                File.WriteAllBytes(config,all);
                settings=ReminderSettings.Defaults();
                NativeNotificationSettings.SaveTo(settings,prefs,config);
                byte[] updated=File.ReadAllBytes(config);
                Check(updated[0]==239 && File.ReadAllText(config).Contains("# note\r\n"),"BOM CRLF comments retained");
                Check(Convert.ToBase64String(File.ReadAllBytes(config+".codex-reminder-notifications.bak"))==Convert.ToBase64String(all),"native backup");
                Check(SettingsStore.LoadFrom(prefs).ChoiceChannel==ReminderChannels.Custom,"settings saved without fallback");
                byte[] before=File.ReadAllBytes(config);
                settings.CompletionChannel=ReminderChannels.Native;
                string blocked=Path.Combine(temp,"blocked"); Directory.CreateDirectory(blocked);
                rejected=false;
                try { NativeNotificationSettings.SaveTo(settings,blocked,config); } catch { rejected=true; }
                Check(rejected && Convert.ToBase64String(File.ReadAllBytes(config))==Convert.ToBase64String(before),"JSON failure rolls back native update");
                string absent=Path.Combine(temp,"absent.toml");
                try { NativeNotificationSettings.SaveTo(settings,blocked,absent); } catch {}
                Check(!File.Exists(absent),"rollback removes newly created native file");
                rejected=false;
                try { NativeNotificationSettings.Edit("[desktop]\n[desktop]\n",settings); } catch(InvalidDataException) { rejected=true; }
                Check(rejected,"duplicate desktop rejected");
            }
            finally { Directory.Delete(temp,true); }
            groups++;
            return groups;
        }
    }
}

// Isolated read-only metadata scanner. Never queries chat text or renders controls.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexCompletionReminder
{
    internal static class CardScanner
    {
        private delegate bool EnumCallback(IntPtr h,IntPtr p);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h,StringBuilder name,int length);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        internal sealed class ScanResult
        {
            public bool EnumerationComplete;
            public bool Unknown;
            public int MinimizedWindows;
            // Compatibility field name: these now identify sessions, not UI windows.
            public List<string> WindowScopes=new List<string>();
            public List<string> CompleteScopes=new List<string>();
            public List<CardRequest> Requests=new List<CardRequest>();
            public int ScannedSessions;
            public int SkippedSessions;
            public int UnknownSessions;
            public long ElapsedMs;
            public long DesktopProbeMs;
        }
        [MTAThread]
        private static int Main()
        {
            ScanResult result;
            Stopwatch probe=Stopwatch.StartNew();
            bool complete; int minimized;
            bool desktop=DesktopPresent(out complete,out minimized);
            long probeMs=probe.ElapsedMilliseconds;
            if(!desktop)
                result=new ScanResult { EnumerationComplete=complete,Unknown=!complete,MinimizedWindows=minimized };
            else
            {
                string home=Environment.GetEnvironmentVariable("CODEX_HOME");
                if(String.IsNullOrWhiteSpace(home)) home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
                result=ScanDirectory(Path.Combine(home,"sessions"));
                result.MinimizedWindows=minimized;
            }
            result.DesktopProbeMs=probeMs;
            Console.WriteLine(new JavaScriptSerializer().Serialize(result));
            return 0;
        }
        internal static ScanResult ScanDirectory(string root)
        {
            // Each file supplies its own complete scope. A global partial scan must
            // never claim missing sessions were resolved.
            ScanResult result=new ScanResult();
            Stopwatch budget=Stopwatch.StartNew();
            List<FileInfo> candidates=new List<FileInfo>();
            try
            {
                string full=Path.GetFullPath(root);
                if(full.StartsWith(@"\\",StringComparison.Ordinal) || !Directory.Exists(full) ||
                    (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                { result.Unknown=true; return result; }
                Collect(full,candidates,result,budget,0);
                candidates.Sort(delegate(FileInfo a,FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                HashSet<string> scopes=new HashSet<string>();
                foreach(FileInfo file in candidates)
                {
                    if(result.ScannedSessions+result.SkippedSessions+result.UnknownSessions>=32 || budget.ElapsedMilliseconds>900)
                    { result.Unknown=true; break; }
                    ChoiceCompletionFilter.DesktopSnapshot state=ChoiceCompletionFilter.ReadDesktop(file.FullName);
                    if(state.IsDesktop && state.Scope!=null)
                    {
                        if(!scopes.Add(state.Scope)) { result.Unknown=true; continue; }
                        result.WindowScopes.Add(state.Scope);
                    }
                    if(state.State==ChoiceCompletionFilter.Result.Unavailable)
                    { result.Unknown=true; result.UnknownSessions++; continue; }
                    if(!state.IsDesktop) { result.SkippedSessions++; continue; }
                    if(result.Requests.Count+state.RequestIds.Count>128)
                    { result.Unknown=true; break; }
                    result.ScannedSessions++;
                    result.CompleteScopes.Add(state.Scope);
                    foreach(string id in state.RequestIds)
                        result.Requests.Add(new CardRequest {Kind=ReminderKinds.Choice,Id=id,Scope=state.Scope});
                }
            }
            catch { result.Unknown=true; }
            finally { result.ElapsedMs=budget.ElapsedMilliseconds; }
            return result;
        }
        private static void Collect(string path,List<FileInfo> files,ScanResult result,Stopwatch budget,int depth)
        {
            if(depth>8 || budget.ElapsedMilliseconds>250 || files.Count>=2048)
            { result.Unknown=true; return; }
            try
            {
                foreach(string file in Directory.EnumerateFiles(path,"rollout-*.jsonl",SearchOption.TopDirectoryOnly))
                {
                    if(files.Count>=2048 || budget.ElapsedMilliseconds>250) { result.Unknown=true; break; }
                    FileInfo info=new FileInfo(file);
                    if((info.Attributes&FileAttributes.ReparsePoint)!=0) { result.Unknown=true; continue; }
                    files.Add(info);
                }
                List<string> directories=new List<string>(Directory.EnumerateDirectories(path));
                directories.Sort(delegate(string a,string b) { return StringComparer.Ordinal.Compare(b,a); });
                foreach(string directory in directories)
                {
                    if(files.Count>=2048 || budget.ElapsedMilliseconds>250) { result.Unknown=true; break; }
                    if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) { result.Unknown=true; continue; }
                    Collect(directory,files,result,budget,depth+1);
                }
            }
            catch { result.Unknown=true; }
        }
        private static bool DesktopPresent(out bool complete,out int minimized)
        {
            bool enumerationComplete=true;
            int count=0,minimizedCount=0;
            List<IntPtr> windows=new List<IntPtr>();
            try
            {
                if(!EnumWindows(delegate(IntPtr window,IntPtr unused)
                {
                    StringBuilder name=new StringBuilder(128); GetClassName(window,name,name.Capacity);
                    if(name.ToString()!="Chrome_WidgetWin_1") return true;
                    if(windows.Count<256) windows.Add(window); else enumerationComplete=false;
                    return true;
                },IntPtr.Zero)) enumerationComplete=false;
                // Resolve only top-level window owners. Renderer/GPU utility processes
                // do not need module/token queries on every polling cycle.
                Dictionary<int,bool> owners=new Dictionary<int,bool>();
                foreach(IntPtr window in windows)
                {
                    uint rawPid; GetWindowThreadProcessId(window,out rawPid);
                    int pid=(int)rawPid; bool valid;
                    if(!owners.TryGetValue(pid,out valid))
                    {
                        valid=false;
                        try
                        {
                            using(Process process=Process.GetProcessById(pid))
                                valid=process.ProcessName=="ChatGPT" &&
                                    process.SessionId==Process.GetCurrentProcess().SessionId &&
                                    process.MainModule.FileName.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0 &&
                                    SameUser(process);
                        }
                        catch { enumerationComplete=false; }
                        owners[pid]=valid;
                    }
                    if(valid) { count++; if(IsIconic(window)) minimizedCount++; }
                }
            }
            catch { enumerationComplete=false; }
            complete=enumerationComplete; minimized=minimizedCount;
            return count>0;
        }
        private static bool SameUser(Process process)
        {
            IntPtr token;
            if(!OpenProcessToken(process.Handle,8,out token)) return false;
            try { using(WindowsIdentity identity=new WindowsIdentity(token)) return identity.User.Equals(WindowsIdentity.GetCurrent().User); }
            finally { CloseHandle(token); }
        }
    }
}

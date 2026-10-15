using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexCompletionReminder
{
    internal sealed class CardMonitor : IDisposable
    {
        private readonly Action<CardSnapshot> publish;
        private readonly Thread worker;
        private readonly object gate = new object();
        private volatile bool stopping;
        private Process child;
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private int disposed;
        private HashSet<string> previousWindows = new HashSet<string>();
        private sealed class ScanResult
        {
            public bool EnumerationComplete { get; set; }
            public bool Unknown { get; set; }
            public int MinimizedWindows { get; set; }
            public List<string> WindowScopes { get; set; }
            public List<CardRequest> Requests { get; set; }
            public List<string> CompleteScopes { get; set; }
        }
        public CardMonitor(Action<CardSnapshot> callback)
        {
            publish = callback;
            worker = new Thread(Run) { IsBackground=true, Name="Codex read-only cards" };
            worker.Start();
        }
        private void Run()
        {
            while (!stopping)
            {
                Stopwatch cycle=Stopwatch.StartNew();
                CardSnapshot snapshot = null;
                try
                {
                    string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CodexReminderScanner.exe");
                    if (!File.Exists(executable)) return;
                    using (Process process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo(executable) {
                            UseShellExecute=false, CreateNoWindow=true,
                            RedirectStandardOutput=true, RedirectStandardError=true,
                            StandardOutputEncoding=Encoding.UTF8 };
                        StringBuilder output = new StringBuilder();
                        bool tooLong = false;
                        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                            if (e.Data == null) return;
                            lock(output) {
                                if (output.Length + e.Data.Length > 65536) tooLong=true;
                                else output.Append(e.Data);
                            }
                        };
                        lock(gate) { if (stopping) return; child=process; process.Start(); }
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine(); // Drain without recording messages.
                        if (!process.WaitForExit(2500)) { try { process.Kill(); } catch {} }
                        else
                        {
                            process.WaitForExit();
                            lock(output) {
                                if (!tooLong && process.ExitCode == 0)
                                    snapshot=Decode(output.ToString());
                            }
                        }
                        lock(gate) { child=null; }
                    }
                }
                catch { snapshot=null; } // Unknown leaves unresolved state intact.
                if (!stopping) publish(snapshot);
                // One worker only: target one-second start cadence, with breathing
                // room after a slow scan instead of always adding two more seconds.
                if(stopSignal.WaitOne(DelayAfterScan(cycle.ElapsedMilliseconds))) break;
            }
        }
        internal static int DelayAfterScan(long elapsedMs)
        { return (int)Math.Max(250L, 1000L-Math.Max(0L,elapsedMs)); }
        private CardSnapshot Decode(string json)
        { return DecodeResult(json,previousWindows); }
        internal static CardSnapshot DecodeResult(string json,HashSet<string> previousWindows)
        {
            ScanResult result;
            try { result=new JavaScriptSerializer { MaxJsonLength=65536 }.Deserialize<ScanResult>(json); }
            catch { return null; }
            if (result == null || result.Requests == null || result.WindowScopes == null ||
                result.Requests.Count > 128 || result.WindowScopes.Count > 128 || previousWindows==null ||
                (result.CompleteScopes!=null && result.CompleteScopes.Count>128)) return null;
            HashSet<string> windows = new HashSet<string>();
            foreach (string scope in result.WindowScopes) {
                if (!SecureTransport.ValidId(scope)) return null;
                windows.Add(scope);
            }
            CardSnapshot snapshot = new CardSnapshot();
            foreach (CardRequest request in result.Requests)
            {
                if(request==null || request.Kind!=ReminderKinds.Choice || !SecureTransport.ValidId(request.Id) || !windows.Contains(request.Scope)) return null;
                snapshot.Requests.Add(request);
            }
            if(result.CompleteScopes!=null)
                foreach(string scope in result.CompleteScopes)
                {
                    if(!SecureTransport.ValidId(scope) || !windows.Contains(scope)) return null;
                    snapshot.CompleteScopes.Add(scope);
                }
            if (result.EnumerationComplete && !result.Unknown)
            {
                foreach (string old in previousWindows) if (!windows.Contains(old)) snapshot.CompleteScopes.Add(old);
                previousWindows.Clear();
                foreach(string scope in windows) previousWindows.Add(scope);
            }
            else
            {
                foreach(CardRequest request in snapshot.Requests)
                    if(previousWindows.Count<1024) previousWindows.Add(request.Scope);
                foreach(string scope in snapshot.CompleteScopes)
                {
                    bool hasRequests=false;
                    foreach(CardRequest request in snapshot.Requests) if(request.Scope==scope) { hasRequests=true; break; }
                    if(!hasRequests) previousWindows.Remove(scope);
                }
            }
            return snapshot;
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0) return;
            stopping=true;
            stopSignal.Set();
            lock(gate) { if (child != null) try { child.Kill(); } catch {} }
            if(worker.Join(3500)) stopSignal.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using CodexCompletionReminder;

internal static class BackgroundChoiceTests
{
    private static JavaScriptSerializer json=new JavaScriptSerializer();
    private static int passed;
    private static string Line(object value) { return json.Serialize(value)+"\n"; }
    private static string Begin(string session,string turn,string originator)
    {
        return Line(new {type="session_meta",payload=new {id=session,source="vscode",originator=originator}})+
            Line(new {type="event_msg",payload=new {type="task_started",turn_id=turn}});
    }
    private static string Call(string id,int count,bool async)
    {
        var questions=new List<object>();
        for(int i=0;i<count;i++) questions.Add(new {title="PRIVATE_QUESTION",options=new[]{"PRIVATE_OPTION"}});
        return Line(new {type="response_item",payload=new {type="function_call",name=async?"functions.request_user_input_async":"functions.request_user_input",call_id=id,arguments=json.Serialize(new {questions=questions})}});
    }
    private static string Ack(string id,bool accepted)
    {
        return Line(new {type="response_item",payload=new {type="function_call_output",call_id=id,output=json.Serialize(new {accepted=accepted})}});
    }
    private static string Reply(string id,int index)
    {
        string envelope="<send_user_message_question_reply>\n"+json.Serialize(new[]{new {questionItemId=json.Serialize(new object[]{"request_user_input_async",id,index}),question="PRIVATE_QUESTION",answer="PRIVATE_ANSWER"}})+"\n</send_user_message_question_reply>";
        return Line(new {type="response_item",payload=new {type="message",role="user",content=new[]{new {type="input_text",text=envelope}}}});
    }
    private static ChoiceCompletionFilter.DesktopSnapshot Parse(string text)
    { return ChoiceCompletionFilter.EvaluateDesktop(Encoding.UTF8.GetBytes(text)); }
    private static void Check(bool value,string name) { if(!value) throw new Exception(name); passed++; }
    private static CardSnapshot Snapshot(ChoiceCompletionFilter.DesktopSnapshot value)
    {
        if(value.State==ChoiceCompletionFilter.Result.Unavailable || !value.IsDesktop) return null;
        var snapshot=new CardSnapshot(); snapshot.CompleteScopes.Add(value.Scope);
        foreach(string id in value.RequestIds) snapshot.Requests.Add(new CardRequest {Kind=ReminderKinds.Choice,Id=id,Scope=value.Scope});
        return snapshot;
    }
    private static bool HasId(IEnumerable<CardRequest> values,string id)
    { foreach(var value in values) if(value.Id==id) return true; return false; }
    public static int Main()
    {
        try
        {
            string start=Begin("session-a","turn-a","Codex Desktop"),call=Call("call-a",2,true),ack=Ack("call-a",true);
            var waiting=Parse(start+call+ack);
            Check(waiting.State==ChoiceCompletionFilter.Result.AwaitingChoice && waiting.IsDesktop && waiting.RequestIds.Count==1,"metadata discovers choice without any UI tree or foreground dependency");
            Check(SecureTransport.ValidId(waiting.Scope) && SecureTransport.ValidId(waiting.RequestIds[0]),"opaque scope and request IDs");
            Check(!waiting.Scope.Contains("session-a") && !waiting.RequestIds[0].Contains("call-a"),"raw identifiers are not published");
            var repeat=Parse(start+call+ack);
            Check(repeat.Scope==waiting.Scope && repeat.RequestIds[0]==waiting.RequestIds[0],"stable IDs across scans and chat navigation");
            var partial=Parse(start+call+ack+Reply("call-a",0));
            Check(partial.RequestIds.Count==1 && partial.RequestIds[0]==waiting.RequestIds[0],"partial submission retains same request");
            var answered=Parse(start+call+ack+Reply("call-a",0)+Reply("call-a",1));
            Check(answered.State==ChoiceCompletionFilter.Result.NoPendingChoice && answered.RequestIds.Count==0 && answered.Scope==waiting.Scope,"full submission authoritatively clears same session");
            var other=Parse(Begin("session-b","turn-a","Codex Desktop")+call+ack);
            Check(other.Scope!=waiting.Scope && other.RequestIds[0]!=waiting.RequestIds[0],"same call ID in different sessions stays separate");
            var next=Parse(start+call+ack+Line(new {type="event_msg",payload=new {type="task_started",turn_id="turn-b"}}));
            Check(next.State==ChoiceCompletionFilter.Result.NoPendingChoice,"old-turn abandoned request does not replay in new turn");
            Check(Parse(start+call+Ack("call-a",false)).RequestIds.Count==0,"rejected async creation does not notify");
            Check(Parse(start+call).RequestIds.Count==0,"unacknowledged async request is not shown");
            Check(!Parse(Begin("session-cli","turn-a","codex_cli_rs")+call+ack).IsDesktop,"CLI origin excluded");
            Check(Parse(start+Call("sync-a",1,false)).RequestIds.Count==1,"blocking choice discovered while result is outstanding");
            Check(Parse(start+Call("sync-a",1,false)+Line(new {type="response_item",payload=new {type="function_call_output",call_id="sync-a",output="PRIVATE_ANSWER"}})).RequestIds.Count==0,"blocking output clears without reading answer");
            Check(Parse(start+call+ack+"{partial").State==ChoiceCompletionFilter.Result.Unavailable,"partial writes remain unknown");
            Check(Parse(start+call+ack+"{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"x\":invalid}}\n").State==ChoiceCompletionFilter.Result.Unavailable,"invalid primitive cannot yield an authoritative snapshot");
            Check(Parse(start+call+ack+"{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"x\":-0.25e+2}}\n").State==ChoiceCompletionFilter.Result.AwaitingChoice,"valid opaque numeric metadata is skipped");
            Check(Parse(start+call+ack+Line(new {type="event_msg",payload=new {type="turn_aborted",turn_id="turn-a"}})).RequestIds.Count==0,"explicit current-turn interruption clears");
            var tracker=new RequestTracker(); tracker.Apply(Snapshot(waiting)); tracker.Dismiss(ReminderKinds.Choice); tracker.Apply(Snapshot(repeat));
            Check(tracker.Count(ReminderKinds.Choice)==0 && tracker.WaitingCount==1,"closing popup then rescanning does not re-alert");
            tracker.Apply(null);
            Check(tracker.WaitingCount==1,"unknown preserves dismissed unresolved state");
            tracker.Apply(Snapshot(other)); tracker.Apply(Snapshot(answered));
            Check(tracker.WaitingCount==1 && HasId(tracker.Visible,other.RequestIds[0]),"one session answer does not clear another");
            var fresh=Parse(start+call+ack+Reply("call-a",0)+Reply("call-a",1)+Call("call-new",1,true)+Ack("call-new",true));
            tracker.Apply(Snapshot(fresh));
            Check(tracker.Count(ReminderKinds.Choice)==2,"new request after answer can notify");

            var prior=new HashSet<string>();
            string encoded=json.Serialize(new {EnumerationComplete=false,Unknown=true,WindowScopes=new[]{waiting.Scope},CompleteScopes=new[]{waiting.Scope},Requests=new[]{new CardRequest{Kind=ReminderKinds.Choice,Id=waiting.RequestIds[0],Scope=waiting.Scope}}});
            var decoded=CardMonitor.DecodeResult(encoded,prior);
            Check(decoded!=null && decoded.CompleteScopes.Contains(waiting.Scope) && decoded.Requests.Count==1,"one complete session remains authoritative when another read is unknown");
            Check(CardMonitor.DecodeResult(json.Serialize(new {EnumerationComplete=false,Unknown=false,WindowScopes=new[]{waiting.Scope},CompleteScopes=new[]{"foreign-scope"},Requests=new object[0]}),prior)==null,"reject foreign complete scope");

            string home=Path.Combine(Path.GetTempPath(),"CodexReminder-BackgroundTests-"+Guid.NewGuid().ToString("N"));
            string previous=Environment.GetEnvironmentVariable("CODEX_HOME");
            Directory.CreateDirectory(Path.Combine(home,"sessions"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME",home);
                string path=Path.Combine(home,"sessions","rollout-test-a.jsonl"); File.WriteAllText(path,start+call+ack,new UTF8Encoding(false));
                var result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Requests.Count==1 && result.CompleteScopes.Contains(waiting.Scope),"isolated filesystem scanner discovers background request");
                Check(!json.Serialize(result).Contains("PRIVATE_") && !json.Serialize(result).Contains(home) && !json.Serialize(result).Contains("session-a"),"scanner publishes no content or paths");
                File.WriteAllText(path,start+call+ack+Reply("call-a",0)+Reply("call-a",1),new UTF8Encoding(false));
                result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Requests.Count==0 && result.CompleteScopes.Contains(waiting.Scope),"filesystem answer emits complete empty session");
                File.WriteAllText(path,start+call+ack+"{partial",new UTF8Encoding(false));
                result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Unknown && !result.CompleteScopes.Contains(waiting.Scope),"filesystem partial write cannot clear requests");
                File.WriteAllText(path,Begin("session-cli","turn-a","codex_cli_rs")+call+ack,new UTF8Encoding(false));
                Check(CardScanner.ScanDirectory(Path.Combine(home,"sessions")).Requests.Count==0,"filesystem ignores non-desktop origin");
                File.Delete(path);
                File.WriteAllText(Path.Combine(home,"sessions","rollout-corrupt.jsonl"),"{bad}\n",new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(home,"sessions","rollout-good.jsonl"),start+call+ack,new UTF8Encoding(false));
                result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Unknown && result.Requests.Count==1,"bad session does not hide a different good session");
                File.Delete(Path.Combine(home,"sessions","rollout-corrupt.jsonl"));
                string newer=Path.Combine(home,"sessions","rollout-newer.jsonl");
                File.WriteAllText(newer,start+call+ack+Reply("call-a",0)+Reply("call-a",1),new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(newer,DateTime.UtcNow.AddMinutes(1));
                result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Requests.Count==0 && result.CompleteScopes.Contains(waiting.Scope),"newest duplicate session wins over older waiting copy");
                File.WriteAllText(newer,start+call+ack+"{partial",new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(newer,DateTime.UtcNow.AddMinutes(1));
                result=CardScanner.ScanDirectory(Path.Combine(home,"sessions"));
                Check(result.Unknown && result.Requests.Count==0 && !result.CompleteScopes.Contains(waiting.Scope),"unknown newest session cannot be replaced by stale older copy");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HOME",previous);
                string full=Path.GetFullPath(home),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe test cleanup");
                Directory.Delete(full,true);
            }
            Console.WriteLine("Background choice tests passed: "+passed); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}

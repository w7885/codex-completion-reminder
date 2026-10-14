using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using CodexCompletionReminder;
internal static class ChoiceFilterTests
{
    private static JavaScriptSerializer json=new JavaScriptSerializer();
    private static int count;
    private static string Line(object data) { return json.Serialize(data)+"\n"; }
    private static string Begin(string session,string turn)
    {
        return Line(new {type="session_meta",payload=new {id=session}})+Line(new {type="event_msg",payload=new {type="task_started",turn_id=turn}});
    }
    private static string Choice(string call,int questions,bool accepted)
    {
        List<object> items=new List<object>();
        for (int i=0;i<questions;i++) items.Add(new {title="PRIVATE_问题_SENTINEL",options=new[]{"PRIVATE_选项_SENTINEL"}});
        return Line(new {type="response_item",payload=new {type="function_call",call_id=call,name="functions.request_user_input_async",arguments=json.Serialize(new {questions=items})}})+
            Line(new {type="response_item",payload=new {type="function_call_output",call_id=call,output=json.Serialize(new {accepted=accepted})}});
    }
    private static string Reply(string call,int index,string role)
    {
        string text="<send_user_message_question_reply>\n"+json.Serialize(new[]{new {questionItemId=json.Serialize(new object[]{"request_user_input_async",call,index}),question="PRIVATE_SENTINEL",answer="PRIVATE_答案_SENTINEL"}})+"\n</send_user_message_question_reply>";
        return Line(new {type="response_item",payload=new {type="message",role=role,content=new[]{new {type="input_text",text=text}}}});
    }
    private static void Check(string source,string session,string turn,ChoiceCompletionFilter.Result expected,string label)
    {
        var result=ChoiceCompletionFilter.Evaluate(Encoding.UTF8.GetBytes(source),session,turn);
        if (result!=expected) throw new Exception(label+": "+result+" != "+expected);
        count++;
    }
    public static int Main(string[] args)
    {
        if (args.Length==2 && args[0]=="--hook") return HookIntegration(args[1]);
        if (args.Length==3)
        {
            var timer=Stopwatch.StartNew(); var result=ChoiceCompletionFilter.Read(args[0],args[1],args[2]);
            Console.WriteLine("LiveMetadataResult="+result+";ElapsedMs="+timer.ElapsedMilliseconds+";Diagnostic="+ChoiceCompletionFilter.Diagnostic); return 0;
        }
        try
        {
            const string s="session-test",t="turn-test";
            string begin=Begin(s,t),choice=Choice("call-a",1,true);
            Check(begin,s,t,ChoiceCompletionFilter.Result.NoPendingChoice,"ordinary completion");
            Check(begin+choice,s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"choice suppresses same turn");
            Check("\uFEFF"+begin+choice,s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"BOM transcript");
            Check(begin+choice+Reply("call-a",0,"user"),s,t,ChoiceCompletionFilter.Result.NoPendingChoice,"same turn answer restores completion");
            Check(begin+choice+Reply("call-a",0,"assistant"),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"assistant cannot submit reply");
            Check(begin+choice+Reply("other-call",0,"user"),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"unrelated reply ignored");
            Check(begin+Choice("call-a",2,true)+Reply("call-a",0,"user"),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"partial multi-question reply");
            Check(begin+Choice("call-a",2,true)+Reply("call-a",0,"user")+Reply("call-a",0,"user"),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"duplicate reply not completion");
            Check(begin+Choice("call-a",2,true)+Reply("call-a",1,"user")+Reply("call-a",0,"user"),s,t,ChoiceCompletionFilter.Result.NoPendingChoice,"all question replies");
            Check(begin+Choice("call-a",1,false),s,t,ChoiceCompletionFilter.Result.NoPendingChoice,"rejected creation");
            Check(begin+choice+Line(new {type="event_msg",payload=new {type="task_started",turn_id="next-turn"}}),s,"next-turn",ChoiceCompletionFilter.Result.NoPendingChoice,"old request does not block new turn");
            Check(begin+choice,"other-session",t,ChoiceCompletionFilter.Result.Unavailable,"foreign session");
            Check(begin+choice,s,"unknown-turn",ChoiceCompletionFilter.Result.Unavailable,"foreign turn");
            Check(begin+choice+"{partial",s,t,ChoiceCompletionFilter.Result.Unavailable,"partial append");
            Check(begin+choice+Reply("call-a",5,"user"),s,t,ChoiceCompletionFilter.Result.Unavailable,"invalid reply index");
            Check(begin+choice+Reply("call-a",0,"user")+Choice("call-b",1,true),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"successive choices");
            string reordered=Line(new {type="response_item",payload=new {arguments="{\"questions\":[{}]}",call_id="call-a",name="functions.request_user_input_async",type="function_call"}})+Line(new {type="response_item",payload=new {output="{\"accepted\":true}",call_id="call-a",type="function_call_output"}});
            Check(begin+reordered,s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"property order independent");
            Check(begin+Line(new {type="response_item",payload=new {type="function_call",name="functions.request_user_input",call_id="sync-call",arguments="{\"questions\":[{}]}"}}),s,t,ChoiceCompletionFilter.Result.AwaitingChoice,"blocking choice pending");
            Check(begin+Line(new {type="response_item",payload=new {type="function_call",name="functions.request_user_input",call_id="sync-call",arguments="{\"questions\":[{}]}"}})+Line(new {type="response_item",payload=new {type="function_call_output",call_id="sync-call",output="PRIVATE_SENTINEL"}}),s,t,ChoiceCompletionFilter.Result.NoPendingChoice,"blocking output resolves without reading answer");
            string temp=Path.Combine(Path.GetTempPath(),"CodexReminder-ChoicePath-"+Guid.NewGuid().ToString("N"));
            string previous=Environment.GetEnvironmentVariable("CODEX_HOME");
            Directory.CreateDirectory(Path.Combine(temp,"sessions"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME",temp);
                string allowed=Path.Combine(temp,"sessions","test.jsonl"); File.WriteAllText(allowed,begin+choice,new UTF8Encoding(false));
                if (ChoiceCompletionFilter.Read(allowed,s,t)!=ChoiceCompletionFilter.Result.AwaitingChoice) throw new Exception("allowed metadata path"); count++;
                if (ChoiceCompletionFilter.Read(@"\\?\"+allowed,s,t)!=ChoiceCompletionFilter.Result.AwaitingChoice) throw new Exception("canonical Windows path"); count++;
                if (ChoiceCompletionFilter.Read(new Uri(allowed).AbsoluteUri,s,t)!=ChoiceCompletionFilter.Result.AwaitingChoice) throw new Exception("local file URI"); count++;
                string outside=Path.Combine(temp,"other.jsonl"); File.WriteAllText(outside,begin+choice,new UTF8Encoding(false));
                if (ChoiceCompletionFilter.Read(outside,s,t)!=ChoiceCompletionFilter.Result.Unavailable) throw new Exception("foreign path"); count++;
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME",previous); Directory.Delete(temp,true); }
            Console.WriteLine("Choice filter tests passed: "+count); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    private static int HookIntegration(string executable)
    {
        string root=Path.Combine(Path.GetTempPath(),"CodexReminder-ChoiceHook-"+Guid.NewGuid().ToString("N"));
        string previousCodexHome=Environment.GetEnvironmentVariable("CODEX_HOME"),previousNoStart=Environment.GetEnvironmentVariable("CODEX_REMINDER_TEST_NO_START");
        Directory.CreateDirectory(Path.Combine(root,"sessions"));
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME",root);
            Environment.SetEnvironmentVariable("CODEX_REMINDER_TEST_NO_START","1");
            string start=Begin("session-test","turn-test"),choice=Choice("call-a",1,true);
            HookCase(executable,root,start+choice,"turn-test",false,"waiting choice");
            HookCase(executable,root,start+choice+Reply("call-a",0,"user"),"turn-test",true,"answered in same turn");
            HookCase(executable,root,start+choice+Line(new {type="event_msg",payload=new {type="task_started",turn_id="next-turn"}}),"next-turn",true,"new turn with old choice");
            HookCase(executable,root,start,"turn-test",true,"ordinary completion");
            HookCase(executable,root,start+choice+"{partial","turn-test",true,"unknown state preserves completion");
            HookCase(executable,root,start+Choice("call-a",2,true)+Reply("call-a",0,"user"),"turn-test",false,"partial answer");
            HookCase(executable,root,start+Choice("call-a",1,false),"turn-test",true,"rejected choice creation");
            Console.WriteLine("Choice Hook integration passed: 7"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME",previousCodexHome); Environment.SetEnvironmentVariable("CODEX_REMINDER_TEST_NO_START",previousNoStart); Directory.Delete(root,true); }
    }
    private static void HookCase(string executable,string root,string transcript,string turn,bool expectsEvent,string label)
    {
        string path=Path.Combine(root,"sessions","test.jsonl"),profile=Path.Combine(root,"profile");
        File.WriteAllText(path,transcript,new UTF8Encoding(false));
        using (var server=SecureTransport.CreateServer())
        {
            var connected=server.BeginWaitForConnection(null,null);
            var info=new ProcessStartInfo(executable,"--diagnostic --preview-profile \""+profile+"\"") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            using (var process=Process.Start(info))
            {
                process.StandardInput.Write(json.Serialize(new {hook_event_name="Stop",session_id="session-test",turn_id=turn,transcript_path=path}));
                process.StandardInput.Close();
                bool delivered=connected.AsyncWaitHandle.WaitOne(expectsEvent ? 2500 : 300);
                if (delivered) { server.EndWaitForConnection(connected); string message=SecureTransport.ReadMessage(server); if (message!="completion\t"+turn) throw new Exception(label+": wrong event"); }
                if (!process.WaitForExit(3000)) { process.Kill(); throw new Exception(label+": Hook timeout"); }
                string output=process.StandardOutput.ReadToEnd();
                if (delivered!=expectsEvent || process.ExitCode!=0 || output.Trim()!="{\"continue\":true}") throw new Exception(label+": wrong contract or routing");
            }
        }
    }
}

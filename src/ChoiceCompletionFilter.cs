using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexCompletionReminder
{
    // Reads only protocol metadata. Body/argument/answer strings are skipped without
    // decoding or retaining their values. This format adapter is intentionally bounded.
    internal static class ChoiceCompletionFilter
    {
        internal enum Result { Unavailable, NoPendingChoice, AwaitingChoice }
        internal static string Diagnostic;
        private const int MaxBytes = 33554432;
        internal sealed class DesktopSnapshot
        {
            public Result State=Result.Unavailable;
            public bool IsDesktop;
            public string Scope;
            public List<string> RequestIds=new List<string>();
        }
        private sealed class Call
        {
            public int Count;
            public bool Async, Accepted;
            public string Turn;
            public HashSet<int> Answers = new HashSet<int>();
        }
        private sealed class Metadata
        {
            public string Type, PayloadType, Turn, Id, Name, Role, CallId;
            public Range Arguments, Output, Source, Originator;
            public List<Range> Text = new List<Range>();
        }
        private sealed class Range { public int Start, Length; }
        private interface IChars { int Read(); }
        private sealed class Bytes : IChars
        {
            private readonly byte[] data;
            private readonly int end;
            public int Position;
            public Bytes(byte[] input, int start, int length) { data=input; Position=start; end=start+length; }
            public int Read() { return Position<end ? data[Position++] : -1; }
            public void SkipString(Stopwatch timer)
            {
                // Skip opaque UTF-8 bytes directly, without decoding private strings.
                int i=Position-1;
                while(i<end)
                {
                    if((i & 8191)==0 && timer.ElapsedMilliseconds>500) throw new TimeoutException();
                    int c=data[i++];
                    if(c==34) { Position=i; return; }
                    if(c<32) throw new InvalidDataException();
                    if(c==92)
                    {
                        if(i>=end) throw new InvalidDataException();
                        c=data[i++];
                        if(c==117)
                        {
                            if(i+4>end) throw new InvalidDataException();
                            for(int j=0;j<4;j++) Hex(data[i++]);
                        }
                        else if(c!=34 && c!=92 && c!=47 && c!=98 && c!=102 && c!=110 && c!=114 && c!=116) throw new InvalidDataException();
                    }
                }
                throw new InvalidDataException();
            }
        }
        // A JSON-encoded protocol envelope is parsed lazily. Non-ASCII characters
        // in private strings are never decoded into message/answer text.
        private sealed class Encoded : IChars
        {
            private readonly Bytes source;
            private bool done;
            public Encoded(byte[] data, Range range)
            {
                source=new Bytes(data,range.Start,range.Length);
                if (source.Read()!=34) throw new InvalidDataException();
            }
            public int Read()
            {
                if (done) return -1;
                int c=source.Read();
                if (c==34) { done=true; return -1; }
                if (c<0) throw new InvalidDataException();
                if (c==92)
                {
                    c=source.Read();
                    if (c==117)
                    {
                        int value=0;
                        for (int i=0;i<4;i++) value=value*16+Hex(source.Read());
                        c=value<128 ? value : 63;
                    }
                    else if (c==110) c=10;
                    else if (c==114) c=13;
                    else if (c==116) c=9;
                    else if (c==98) c=8;
                    else if (c==102) c=12;
                    else if (c!=34 && c!=92 && c!=47) throw new InvalidDataException();
                }
                return c<128 ? c : 63;
            }
        }
        private static int Hex(int c)
        {
            if (c>=48 && c<=57) return c-48;
            if (c>=65 && c<=70) return c-55;
            if (c>=97 && c<=102) return c-87;
            throw new InvalidDataException();
        }
        private sealed class Cursor
        {
            private readonly IChars source;
            private readonly Stopwatch timer;
            private int operations;
            public int Current;
            public Cursor(IChars input, Stopwatch watch) { source=input; timer=watch; Next(); }
            public int Position { get { return ((Bytes)source).Position-1; } }
            public void Next()
            {
                if ((++operations & 8191)==0 && timer.ElapsedMilliseconds>500) throw new TimeoutException();
                Current=source.Read();
            }
            public void White() { while (Current==32 || Current==10 || Current==13 || Current==9) Next(); }
            public void Expect(int c) { White(); if (Current!=c) throw new InvalidDataException(); Next(); }
            public string String(bool keep)
            {
                Expect(34);
                Bytes raw=source as Bytes;
                if(!keep && raw!=null) { raw.SkipString(timer); Next(); return null; }
                StringBuilder value=keep ? new StringBuilder() : null;
                bool overflow=false;
                while (Current!=34)
                {
                    int c=Current; if (c<32) throw new InvalidDataException(); Next();
                    if (c==92)
                    {
                        c=Current; Next();
                        if (c==117)
                        {
                            int code=0;
                            for (int i=0;i<4;i++) { code=code*16+Hex(Current); Next(); }
                            c=code<128 ? code : 63;
                        }
                        else if (c==110) c=10;
                        else if (c==114) c=13;
                        else if (c==116) c=9;
                        else if (c==98) c=8;
                        else if (c==102) c=12;
                        else if (c!=34 && c!=92 && c!=47) throw new InvalidDataException();
                    }
                    if (keep)
                    {
                        if (value.Length<512) value.Append((char)(c<128 ? c : 63));
                        else overflow=true;
                    }
                }
                Next(); return keep && !overflow ? value.ToString() : null;
            }
            public Range StringRange()
            {
                White(); int start=Position; String(false);
                return new Range { Start=start, Length=Position-start };
            }
            public void Skip(int depth)
            {
                if (depth>64) throw new InvalidDataException();
                White();
                if (Current==34) { String(false); return; }
                if (Current==123)
                {
                    Next(); White(); if (Current==125) { Next(); return; }
                    while (true)
                    {
                        String(false); Expect(58); Skip(depth+1); White();
                        if (Current==125) { Next(); return; } Expect(44);
                    }
                }
                if (Current==91)
                {
                    Next(); White(); if (Current==93) { Next(); return; }
                    while (true)
                    {
                        Skip(depth+1); White(); if (Current==93) { Next(); return; } Expect(44);
                    }
                }
                if(Current==116 || Current==102 || Current==110)
                {
                    string literal=Current==116 ? "true" : Current==102 ? "false" : "null";
                    foreach(char c in literal) { if(Current!=c) throw new InvalidDataException(); Next(); }
                }
                else
                {
                    if(Current==45) Next();
                    if(Current==48) Next();
                    else
                    {
                        if(Current<49 || Current>57) throw new InvalidDataException();
                        while(Current>=48 && Current<=57) Next();
                    }
                    if(Current==46)
                    {
                        Next(); if(Current<48 || Current>57) throw new InvalidDataException();
                        while(Current>=48 && Current<=57) Next();
                    }
                    if(Current==101 || Current==69)
                    {
                        Next(); if(Current==43 || Current==45) Next();
                        if(Current<48 || Current>57) throw new InvalidDataException();
                        while(Current>=48 && Current<=57) Next();
                    }
                }
                if(Current!=-1 && Current!=44 && Current!=93 && Current!=125 && Current!=32 && Current!=10 && Current!=13 && Current!=9)
                    throw new InvalidDataException();
            }
            public void ReadObject(Metadata row, int level)
            {
                Expect(123); White(); if (Current==125) { Next(); return; }
                while (true)
                {
                    string key=String(true); Expect(58); White();
                    if (level==0 && key=="payload") ReadObject(row,1);
                    else if (key=="type" && Current==34)
                    {
                        string value=String(true); if (level==0) row.Type=value; else row.PayloadType=value;
                    }
                    else if (level==1 && key=="turn_id" && Current==34) row.Turn=String(true);
                    else if (level==1 && key=="id" && Current==34) row.Id=String(true);
                    else if (level==1 && key=="call_id" && Current==34) row.CallId=String(true);
                    else if (level==1 && key=="name" && Current==34) row.Name=String(true);
                    else if (level==1 && key=="role" && Current==34) row.Role=String(true);
                    else if (level==1 && key=="source" && Current==34) row.Source=StringRange();
                    else if (level==1 && key=="originator" && Current==34) row.Originator=StringRange();
                    else if (level==1 && key=="arguments" && Current==34) row.Arguments=StringRange();
                    else if (level==1 && key=="output" && Current==34) row.Output=StringRange();
                    else if (level==1 && key=="content" && Current==91) ReadContent(row);
                    else Skip(0);
                    White(); if (Current==125) { Next(); return; } Expect(44);
                }
            }
            private void ReadContent(Metadata row)
            {
                Expect(91); White(); if (Current==93) { Next(); return; }
                while (true)
                {
                    White();
                    if (Current!=123) Skip(0);
                    else
                    {
                        Expect(123); White();
                        if (Current==125) Next();
                        else while (true)
                        {
                            string key=String(true); Expect(58); White();
                            if (key=="text" && Current==34 && row.Text.Count<128) row.Text.Add(StringRange());
                            else Skip(0);
                            White(); if (Current==125) { Next(); break; } Expect(44);
                        }
                    }
                    White(); if (Current==93) { Next(); return; } Expect(44);
                }
            }
            public int Questions()
            {
                Expect(123); White(); int count=-1;
                if (Current==125) return count;
                while (true)
                {
                    string key=String(true); Expect(58);
                    if (key=="questions")
                    {
                        Expect(91); White(); count=0;
                        if (Current!=93) while (true)
                        {
                            Skip(0); if (++count>128) throw new InvalidDataException();
                            White(); if (Current==93) break; Expect(44);
                        }
                        Expect(93);
                    }
                    else Skip(0);
                    White(); if (Current==125) { Next(); White(); if (Current!=-1) throw new InvalidDataException(); return count; } Expect(44);
                }
            }
            public bool? Accepted()
            {
                Expect(123); White(); bool? result=null;
                if (Current==125) return result;
                while (true)
                {
                    string key=String(true); Expect(58); White();
                    if (key=="accepted")
                    {
                        string expected=Current==116 ? "true" : Current==102 ? "false" : null;
                        if (expected==null) throw new InvalidDataException();
                        foreach (char c in expected) { if (Current!=c) throw new InvalidDataException(); Next(); }
                        result=expected=="true";
                    }
                    else Skip(0);
                    White(); if (Current==125) { Next(); return result; } Expect(44);
                }
            }
            public List<string> Replies()
            {
                White();
                foreach (char c in "<send_user_message_question_reply>")
                {
                    if (Current!=c) return null; Next();
                }
                Expect(91); White(); List<string> result=new List<string>();
                if (Current==93) { Next(); return result; }
                while (true)
                {
                    Expect(123); White();
                    if (Current==125) Next();
                    else while (true)
                    {
                        string key=String(true); Expect(58); White();
                        if (key=="questionItemId" && Current==34) result.Add(String(true));
                        else Skip(0);
                        White(); if (Current==125) { Next(); break; } Expect(44);
                    }
                    if (result.Count>128) throw new InvalidDataException();
                    White(); if (Current==93) { Next(); break; } Expect(44);
                }
                White();
                foreach (char c in "</send_user_message_question_reply>")
                {
                    if (Current!=c) throw new InvalidDataException(); Next();
                }
                White(); if (Current!=-1) throw new InvalidDataException();
                return result;
            }
        }
        private static bool ChoiceTool(string name)
        {
            return name=="request_user_input_async" || name=="functions.request_user_input_async" ||
                name=="request_user_input" || name=="functions.request_user_input";
        }
        internal static Result Evaluate(byte[] data, string sessionId, string turnId)
        {
            DesktopSnapshot ignored;
            return EvaluateCore(data,sessionId,turnId,false,out ignored);
        }
        internal static DesktopSnapshot EvaluateDesktop(byte[] data)
        {
            DesktopSnapshot snapshot;
            EvaluateCore(data,"desktop-session","desktop-turn",true,out snapshot);
            return snapshot ?? new DesktopSnapshot();
        }
        internal static string OpaqueId(string value)
        {
            using(SHA256 hash=SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,24);
        }
        private static string HeaderValue(byte[] data,Range range,Stopwatch timer)
        {
            return range==null ? null : new Cursor(new Bytes(data,range.Start,range.Length),timer).String(true);
        }
        private static Result EvaluateCore(byte[] data,string sessionId,string turnId,bool latest,out DesktopSnapshot snapshot)
        {
            snapshot=null;
            Diagnostic="metadata-input";
            if (data==null || data.Length>MaxBytes || !SecureTransport.ValidId(sessionId) || !SecureTransport.ValidId(turnId)) return Result.Unavailable;
            Stopwatch timer=Stopwatch.StartNew();
            try
            {
                string currentTurn=null, fileSession=null, source=null, originator=null; bool foundTurn=false;
                Dictionary<string,Call> pending=new Dictionary<string,Call>();
                int start=data.Length>=3 && data[0]==239 && data[1]==187 && data[2]==191 ? 3 : 0;
                for (int end=start;end<data.Length;end++)
                {
                    if ((end & 65535)==0 && timer.ElapsedMilliseconds>500) { Diagnostic="metadata-timeout"; return Result.Unavailable; }
                    if (data[end]!=10) continue;
                    int length=end-start;
                    if (length==0) { start=end+1; continue; }
                    Metadata row=new Metadata(); Cursor cursor=new Cursor(new Bytes(data,start,length),timer);
                    cursor.ReadObject(row,0); cursor.White(); if (cursor.Current!=-1) return Result.Unavailable;
                    start=end+1;
                    if (row.Type=="session_meta")
                    {
                        if(fileSession!=null && fileSession!=row.Id) throw new InvalidDataException();
                        fileSession=row.Id;
                        source=HeaderValue(data,row.Source,timer); originator=HeaderValue(data,row.Originator,timer);
                        if(latest) sessionId=fileSession;
                    }
                    if (row.Type=="event_msg" && row.PayloadType=="task_started")
                    {
                        if(latest) { pending.Clear(); turnId=row.Turn; }
                        currentTurn=row.Turn; if (currentTurn==turnId) foundTurn=true;
                    }
                    if(latest && row.Type=="event_msg" && row.PayloadType=="turn_aborted" &&
                        currentTurn==turnId && (row.Turn==null || row.Turn==turnId)) pending.Clear();
                    if (row.Type=="response_item" && row.PayloadType=="function_call" && currentTurn==turnId && ChoiceTool(row.Name))
                    {
                        if (!SecureTransport.ValidId(row.CallId) || row.Arguments==null || pending.Count>=128) return Result.Unavailable;
                        int count=new Cursor(new Encoded(data,row.Arguments),timer).Questions();
                        if (count<=0) return Result.Unavailable;
                        pending[row.CallId]=new Call { Count=count, Async=row.Name.EndsWith("_async",StringComparison.Ordinal), Turn=currentTurn };
                    }
                    if (row.Type=="response_item" && row.PayloadType=="function_call_output")
                    {
                        Call call;
                        if (row.CallId!=null && pending.TryGetValue(row.CallId,out call))
                        {
                            if (!call.Async) pending.Remove(row.CallId);
                            else
                            {
                                if (row.Output==null) return Result.Unavailable;
                                bool? accepted=new Cursor(new Encoded(data,row.Output),timer).Accepted();
                                if (!accepted.HasValue) return Result.Unavailable;
                                if (accepted.Value) call.Accepted=true; else pending.Remove(row.CallId);
                            }
                        }
                    }
                    if (row.Type=="response_item" && row.PayloadType=="message" && row.Role=="user")
                    {
                        foreach (Range text in row.Text)
                        {
                            List<string> replies=new Cursor(new Encoded(data,text),timer).Replies();
                            if (replies==null) continue;
                            foreach (string reply in replies)
                            {
                                if (reply==null) return Result.Unavailable;
                                object[] id=new JavaScriptSerializer().Deserialize<object[]>(reply);
                                if (id==null || id.Length!=3 || !(id[0] is string) || !ChoiceTool((string)id[0]) || !(id[1] is string) || !(id[2] is int)) return Result.Unavailable;
                                string callId=(string)id[1]; int index=(int)id[2]; Call call;
                                if (pending.TryGetValue(callId,out call))
                                {
                                    if (index<0 || index>=call.Count) return Result.Unavailable;
                                    call.Answers.Add(index);
                                    if (call.Answers.Count==call.Count) pending.Remove(callId);
                                }
                            }
                        }
                    }
                }
                // A partial append or foreign/unassociated file must never suppress completion.
                if (start!=data.Length) { Diagnostic="partial-record"; return Result.Unavailable; }
                if (fileSession!=sessionId) { Diagnostic="foreign-session"; return Result.Unavailable; }
                if (!foundTurn) { Diagnostic="turn-not-found"; return Result.Unavailable; }
                if(!SecureTransport.ValidId(sessionId) || !SecureTransport.ValidId(turnId)) return Result.Unavailable;
                if(latest && (source!="vscode" || originator!="Codex Desktop"))
                {
                    snapshot=new DesktopSnapshot { State=Result.NoPendingChoice, IsDesktop=false };
                    return Result.NoPendingChoice;
                }
                foreach (Call call in pending.Values)
                {
                    if (call.Async && !call.Accepted) return Result.Unavailable;
                }
                Diagnostic="metadata-ok";
                Result result=pending.Count>0 ? Result.AwaitingChoice : Result.NoPendingChoice;
                if(latest)
                {
                    snapshot=new DesktopSnapshot { State=result, IsDesktop=true, Scope="desktop-"+OpaqueId(sessionId) };
                    foreach(KeyValuePair<string,Call> entry in pending)
                        snapshot.RequestIds.Add("choice-"+OpaqueId(sessionId+":"+entry.Value.Turn+":"+entry.Key));
                }
                return result;
            }
            catch (Exception ex) { Diagnostic="metadata-error-"+ex.GetType().Name; return Result.Unavailable; }
        }
        internal static Result Read(string path, string sessionId, string turnId)
        {
            if(!SecureTransport.ValidId(sessionId) || !SecureTransport.ValidId(turnId)) { Diagnostic="read-invalid-metadata"; return Result.Unavailable; }
            bool ignored; string ignoredId; byte[] data=ReadBytes(path,false,out ignored,out ignoredId);
            return data==null ? Result.Unavailable : Evaluate(data,sessionId,turnId);
        }
        internal static DesktopSnapshot ReadDesktop(string path)
        {
            bool nonDesktop; string headerId; byte[] data=ReadBytes(path,true,out nonDesktop,out headerId);
            if(nonDesktop) return new DesktopSnapshot { State=Result.NoPendingChoice, IsDesktop=false };
            DesktopSnapshot state=data==null ? new DesktopSnapshot() : EvaluateDesktop(data);
            if(state.State==Result.Unavailable && headerId!=null)
            {
                state.IsDesktop=true; state.Scope="desktop-"+OpaqueId(headerId);
            }
            return state;
        }
        private static byte[] ReadBytes(string path,bool desktopOnly,out bool nonDesktop,out string headerId)
        {
            nonDesktop=false;
            headerId=null;
            Diagnostic="read-input";
            try
            {
                if (String.IsNullOrWhiteSpace(path)) { Diagnostic="read-invalid-metadata"; return null; }
                string home=Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(home)) home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
                Diagnostic="read-root-path";
                string root=Path.GetFullPath(Path.Combine(home,"sessions")).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                Diagnostic="read-transcript-path";
                // Rust canonical Windows paths may carry the extended-length prefix,
                // which .NET Framework's legacy GetFullPath rejects. Normalize only
                // local file paths before applying the same profile/reparse checks.
                if (path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) { Diagnostic="read-network-path"; return null; }
                if (path.StartsWith(@"\\?\",StringComparison.Ordinal)) path=path.Substring(4);
                Uri fileUri;
                if (Uri.TryCreate(path,UriKind.Absolute,out fileUri) && fileUri.IsFile) path=fileUri.LocalPath;
                string file=Path.GetFullPath(path);
                if (!Path.IsPathRooted(path) || file.StartsWith(@"\\",StringComparison.Ordinal) || !file.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)) { Diagnostic="read-path-outside-profile"; return null; }
                string check=file;
                while (check!=null && check.Length>=root.TrimEnd(Path.DirectorySeparatorChar).Length)
                {
                    Diagnostic="read-path-attributes";
                    if ((File.GetAttributes(check)&FileAttributes.ReparsePoint)!=0) { Diagnostic="read-reparse-path"; return null; }
                    Diagnostic="read-parent-path";
                    check=Path.GetDirectoryName(check);
                }
                Diagnostic="read-file-open";
                using (FileStream stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    if(desktopOnly)
                    {
                        // A bounded header projection excludes CLI/subagent files before
                        // any transcript body is scanned or large buffer allocated.
                        byte[] header=new byte[65536]; int count=0,c;
                        while(count<header.Length && (c=stream.ReadByte())>=0)
                        {
                            header[count++]=(byte)c;
                            if(c==10) break;
                        }
                        if(count==0 || header[count-1]!=10) { Diagnostic="read-header-incomplete"; return null; }
                        int first=count>=3 && header[0]==239 && header[1]==187 && header[2]==191 ? 3 : 0;
                        Metadata meta=new Metadata();
                        Cursor parser=new Cursor(new Bytes(header,first,count-first),Stopwatch.StartNew());
                        parser.ReadObject(meta,0); parser.White();
                        if(parser.Current!=-1 || meta.Type!="session_meta" || !SecureTransport.ValidId(meta.Id)) return null;
                        Stopwatch headerTimer=Stopwatch.StartNew();
                        if(HeaderValue(header,meta.Source,headerTimer)!="vscode" || HeaderValue(header,meta.Originator,headerTimer)!="Codex Desktop")
                        { nonDesktop=true; return null; }
                        headerId=meta.Id;
                        stream.Position=0;
                    }
                    long size=stream.Length; if (size<=0 || size>MaxBytes) return null;
                    byte[] data=new byte[(int)size]; int offset=0, read;
                    while (offset<data.Length && (read=stream.Read(data,offset,data.Length-offset))>0) offset+=read;
                    if (offset!=data.Length) return null;
                    return data;
                }
            }
            catch (Exception ex) { Diagnostic=Diagnostic+"-error-"+ex.GetType().Name; return null; }
        }
    }
}

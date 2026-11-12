using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Alfred {
    interface IDraftTarget {
        string Read();
        void Replace(string expected,string text);
        void Send(string expected);
    }
    sealed class DraftSession {
        IDraftTarget target;
        bool dictating;
        string previousText;
        public string Text {get;private set;}
        public bool HasDraft {get{return target!=null;}}
        public DraftSession(){Text="";}
        public void Start(IDraftTarget field,string text,bool adopt){
            string current=field.Read();
            if(!adopt&&current.Length>0)throw new Exception("বক্সে আগের লেখা আছে। সেটি নিতে বলো: এই লেখাটা নাও");
            if(!adopt)field.Replace(current,text);
            target=field;
            Text=adopt?current:text;
            previousText=adopt?null:current;
            dictating=false;
        }
        public void BeginDictation(IDraftTarget field){
            Start(field,"",true);
            dictating=true;
        }
        public void EndDictation(){dictating=false;}
        public void Dictate(string text){
            if(!dictating)throw new InvalidOperationException("আগে ডিক্টেশন শুরু করো");
            if(!String.IsNullOrWhiteSpace(text))Append(text.Trim());
        }
        static string ComparableText(string text){return text.Replace("\r\n","\n").TrimEnd('\r','\n');}
        void Require(){
            if(target==null)throw new Exception("আগে লেখার বক্সে ক্লিক করে বলো: এখানে লেখো");
            if(ComparableText(target.Read())!=ComparableText(Text))throw new Exception("লেখা বা লেখার বক্স বদলেছে। আবার বক্সটি বেছে বলো: এই লেখাটা নাও");
        }
        public void Rewrite(string text){
            Require();
            if(text==Text)return;
            target.Replace(Text,text);
            previousText=Text;
            Text=text;
        }
        public void Append(string text){Rewrite(Text+(Text.Length>0&&!Char.IsWhiteSpace(Text[Text.Length-1])?" ":"")+text);}
        public void NewLine(){Rewrite(Text+"\n");}
        public void Undo(){
            Require();
            if(previousText==null)throw new Exception("ফিরিয়ে দেওয়ার মতো আগের এডিট নেই");
            target.Replace(Text,previousText);
            Text=previousText;
            previousText=null;
        }
        static Regex Phrase(string oldText){
            string phrase=oldText.Trim(' ', '\"','\'','“','”');
            if(phrase.Length==0)throw new Exception("কোন কথাটা বদলাবে বলো");
            return new Regex(@"(?<![\p{L}\p{M}\p{N}])"+Regex.Escape(phrase)+@"(?![\p{L}\p{M}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        }
        public void ReplaceWords(string oldText,string newText){
            Regex match=Phrase(oldText);
            if(!match.IsMatch(Text))throw new Exception("এই কথাটা খসড়ায় নেই");
            Rewrite(match.Replace(Text,delegate{return newText.Trim(' ', '\"','\'','“','”');}));
        }
        public void RemoveWords(string oldText){
            Regex match=Phrase(oldText);
            if(!match.IsMatch(Text))throw new Exception("এই কথাটা খসড়ায় নেই");
            string edited=match.Replace(Text,"");
            edited=Regex.Replace(edited,@"[^\S\r\n]{2,}"," ");
            edited=Regex.Replace(edited,@"[^\S\r\n]+([।.,!?])","$1");
            Rewrite(edited.Trim(' ','\t'));
        }
        public void DeleteWord(){Require();Rewrite(Regex.Replace(Text.TrimEnd(),@"\s*\S+$","").TrimEnd());}
        public void DeleteSentence(){Require();string s=Regex.Replace(Text.TrimEnd(),@"[।.!?]+$","");int p=s.LastIndexOfAny(new[]{'।','.','!','?'});Rewrite(p<0?"":s.Substring(0,p+1).TrimEnd());}
        public void Clear(){Rewrite("");}
        public void Send(){
            if(dictating)throw new Exception("আগে ডিক্টেশন শেষ করে খসড়া দেখে নাও");
            Require();
            if(String.IsNullOrWhiteSpace(Text))throw new Exception("খসড়ায় লেখা নেই");
            target.Send(Text);
            target=null;
            Text="";
            previousText=null;
        }
        sealed class FakeTarget : IDraftTarget {
            public string Value="";public int Sends;public bool Current=true;
            public string Read(){if(!Current)throw new Exception("Wrong target");return Value;}
            public void Replace(string expected,string text){if(Read()!=expected)throw new Exception("Changed text");Value=text;}
            public void Send(string expected){if(Read()!=expected)throw new Exception("Changed text");Sends++;Value="";}
        }
        public static string SelfTest(){
            FakeTarget f=new FakeTarget();DraftSession d=new DraftSession();d.Start(f,"আমি বিকাল থেকে কীবোর্ডও টাচ করি নাই",false);
            if(f.Sends!=0)throw new Exception("Writing sent a message");d.ReplaceWords("বিকাল","সকাল");d.Append("পরে কথা হবে");d.DeleteWord();
            if(!f.Value.Contains("সকাল")||f.Value.EndsWith("হবে"))throw new Exception("Draft editing failed");
            string expected=d.Text;f.Value="বাইরের লেখা";bool blocked=false;try{d.Send();}catch{blocked=true;}if(!blocked||f.Sends!=0)throw new Exception("Changed draft was sent");
            f.Value=expected;f.Current=false;blocked=false;try{d.Send();}catch{blocked=true;}if(!blocked||f.Sends!=0)throw new Exception("Wrong target was sent");
            f.Current=true;d.Send();if(f.Sends!=1||d.HasDraft)throw new Exception("Explicit send failed");
            blocked=false;try{d.Send();}catch{blocked=true;}if(!blocked||f.Sends!=1)throw new Exception("Repeated send was allowed");
            f.Value="";d.BeginDictation(f);d.Dictate("আমি বিকালে ফোন করব।");d.Dictate("open Chrome and send it");
            if(!d.Text.Contains("open Chrome and send it")||f.Sends!=1)throw new Exception("Dictation text was not appended literally");
            blocked=false;try{d.Send();}catch{blocked=true;}if(!blocked||f.Sends!=1)throw new Exception("Dictation sent without review");
            d.Undo();if(d.Text.Contains("open Chrome"))throw new Exception("Dictation undo failed");
            d.NewLine();d.Dictate("দেখা হবে");d.EndDictation();d.Send();
            if(f.Sends!=2||d.HasDraft)throw new Exception("Reviewed dictation could not be explicitly sent");
            f.Value="";d.Start(f,"প্রথম বাক্য। দ্বিতীয় বাক্য।",false);d.DeleteSentence();if(d.Text!="প্রথম বাক্য।")throw new Exception("Sentence removal failed");d.Clear();
            bool name1=ChatContactSelector.NameMatchesForTest("Ayesha\nlast seen", "Ayesha"),name2=ChatContactSelector.NameMatchesForTest("রিমা, active now","রিমা"),name3=ChatContactSelector.NameMatchesForTest("Ayesha\nlast seen","আয়েশা"),name4=ChatContactSelector.NameMatchesForTest("আয়েশা, active now","Ayesha"),name5=ChatContactSelector.NameMatchesForTest("Ayesha Khan","Ayesha");
            if(!name1||!name2||!name3||!name4||name5)throw new Exception("Contact matching was not exact: "+name1+","+name2+","+name3+","+name4+","+name5);
            return "PASS draft review/edit state, exact Bengali/English chat-name matching, changed-target blocking, explicit single send and clearing"+Environment.NewLine;
        }
    }
    static class ChatPlatform {
        static readonly Regex Site=new Regex(@"facebook|messenger|instagram|whatsapp",RegexOptions.IgnoreCase|RegexOptions.Compiled);
        static readonly Regex ChatProcess=new Regex(@"^(?:whatsapp|instagram|messenger|facebook)(?:\.|$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static bool IsSupported(IntPtr window,string process,string title){
            if(ChatProcess.IsMatch(process??""))return true;
            bool browser=process=="chrome"||process=="msedge"||process=="firefox";
            if(!browser)return false;
            if(Site.IsMatch(title??""))return true;
            try{
                AutomationElement root=AutomationElement.FromHandle(window);
                AutomationElementCollection edits=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                foreach(AutomationElement e in edits){string name=e.Current.Name??"";if(!Regex.IsMatch(name,@"address and search bar|address bar",RegexOptions.IgnoreCase))continue;object p;if(e.TryGetCurrentPattern(ValuePattern.Pattern,out p)&&Site.IsMatch(((ValuePattern)p).Current.Value??""))return true;}
            }catch{}
            return false;
        }
    }
    static class ChatContactSelector {
        static readonly Dictionary<char,string> Roman=new Dictionary<char,string>{
            {'অ',"o"},{'আ',"a"},{'ই',"i"},{'ঈ',"i"},{'উ',"u"},{'ঊ',"u"},{'ঋ',"ri"},{'এ',"e"},{'ঐ',"oi"},{'ও',"o"},{'ঔ',"ou"},
            {'ক',"k"},{'খ',"kh"},{'গ',"g"},{'ঘ',"gh"},{'ঙ',"ng"},{'চ',"ch"},{'ছ',"chh"},{'জ',"j"},{'ঝ',"jh"},{'ঞ',"ny"},
            {'ট',"t"},{'ঠ',"th"},{'ড',"d"},{'ঢ',"dh"},{'ণ',"n"},{'ত',"t"},{'থ',"th"},{'দ',"d"},{'ধ',"dh"},{'ন',"n"},
            {'প',"p"},{'ফ',"f"},{'ব',"b"},{'ভ',"bh"},{'ম',"m"},{'য',"j"},{'র',"r"},{'ল',"l"},{'শ',"sh"},{'ষ',"sh"},{'স',"s"},{'হ',"h"},
            {'\u09dc',"r"},{'\u09dd',"rh"},{'\u09df',"y"},{'ং',"ng"},{'ঃ',"h"}
        };
        static string Canonical(string s){return Regex.Replace((s??"").Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"[\p{P}\p{Z}\s]+","");}
        static bool IsBengali(string s){foreach(char c in (s??""))if(c>='\u0980'&&c<='\u09ff')return true;return false;}
        static string Romanized(string text){
            StringBuilder result=new StringBuilder();string source=(text??"").Normalize(NormalizationForm.FormKC);
            for(int n=0;n<source.Length;n++){
                if(n+1<source.Length&&source[n+1]=='়'&&(source[n]=='য'||source[n]=='ড'||source[n]=='ঢ')){
                    result.Append(source[n]=='য'?"y":source[n]=='ড'?"r":"rh");n++;
                    if(n+1<source.Length){char mark=source[n+1];if(mark=='া'||mark=='ি'||mark=='ী'||mark=='ু'||mark=='ূ'||mark=='ৃ'||mark=='ে'||mark=='ৈ'||mark=='ো'||mark=='ৌ'){
                        result.Append(mark=='া'?"a":mark=='ি'||mark=='ী'?"i":mark=='ু'||mark=='ূ'?"u":mark=='ৃ'?"ri":mark=='ে'?"e":mark=='ৈ'?"oi":mark=='ো'?"o":"ou");n++;
                    }}continue;
                }
                char c=source[n];string part;if(!Roman.TryGetValue(c,out part))continue;result.Append(part);
                if("কখগঘঙচছজঝঞটঠডঢণতথদধনপফবভমযরলশষসহড়ঢ়য়".IndexOf(c)<0)continue;
                if(n+1<source.Length){char next=source[n+1];
                    if(next=='া'||next=='ি'||next=='ী'||next=='ু'||next=='ূ'||next=='ৃ'||next=='ে'||next=='ৈ'||next=='ো'||next=='ৌ'){
                        string vowel=next=='া'?"a":next=='ি'||next=='ী'?"i":next=='ু'||next=='ূ'?"u":next=='ৃ'?"ri":next=='ে'?"e":next=='ৈ'?"oi":next=='ো'?"o":"ou";result.Append(vowel);n++;
                    }else if(next=='্')n++;
                    else if(next>='\u0995'&&next<='\u09df')result.Append('o');
                }
            }
            return result.ToString();
        }
        static bool NameMatches(string accessible,string requested){
            string first=Regex.Split((accessible??"").Trim(),@"[\r\n|]")[0].Trim();
            int comma=first.IndexOf(',');if(comma>=0)first=first.Substring(0,comma).Trim();
            string a=Canonical(first),r=Canonical(requested);if(a.Length==0||r.Length==0)return false;if(a==r)return true;
            string ar=Canonical(Romanized(first)),rr=Canonical(Romanized(requested));
            if(IsBengali(first)&&!IsBengali(requested))return ar==r;
            if(!IsBengali(first)&&IsBengali(requested))return a==rr;
            return ar.Length>0&&ar==rr;
        }
        public static bool NameMatchesForTest(string accessible,string requested){return NameMatches(accessible,requested);}
        static int Rank(ControlType type){if(type==ControlType.ListItem)return 0;if(type==ControlType.DataItem)return 1;if(type==ControlType.TreeItem)return 2;if(type==ControlType.Button)return 3;return 10;}
        public static string Select(IntPtr window,string requested,Func<bool> cancelled){
            if(String.IsNullOrWhiteSpace(requested))throw new Exception("বন্ধুর নামটা স্পষ্ট করে বলো");
            if(Native.GetForegroundWindow()!=window)throw new Exception("সামনের অ্যাপ বদলেছে—চ্যাটটি আবার সামনে এনে নাম বলো");
            uint pid;Native.GetWindowThreadProcessId(window,out pid);if(pid==0||(int)pid==Program.ProcessId)throw new Exception("চ্যাট অ্যাপটি সামনে আনো");
            string process;using(Process p=Process.GetProcessById((int)pid))process=p.ProcessName.ToLowerInvariant();
            string title=FocusedTextTarget.WindowTitleForSelection(window);
            if(!ChatPlatform.IsSupported(window,process,title))throw new Exception("এখনকার উইন্ডোটি চেনা চ্যাট নয়। Instagram, Facebook/Messenger বা WhatsApp-এর চ্যাট সামনে আনো");
            AutomationElement root=AutomationElement.FromHandle(window);AutomationElementCollection all=root.FindAll(TreeScope.Descendants,Condition.TrueCondition);
            List<AutomationElement> matches=new List<AutomationElement>();int best=10;
            foreach(AutomationElement e in all){
                try{
                    ControlType type=e.Current.ControlType;int rank=Rank(type);if(rank>3||!e.Current.IsEnabled||e.Current.IsOffscreen||!NameMatches(e.Current.Name,requested))continue;
                    if(cancelled()!=false||Native.GetForegroundWindow()!=window)throw new OperationCanceledException();
                    if(rank<best){matches.Clear();best=rank;}if(rank==best)matches.Add(e);
                }catch(InvalidOperationException){}
            }
            if(matches.Count==0)throw new Exception("এই নামে দৃশ্যমান চ্যাট পাইনি। চ্যাট তালিকায় নামটি দেখা যাচ্ছে কি না দেখো");
            if(matches.Count!=1)throw new Exception("এই নামে একাধিক চ্যাট আছে। তালিকায় পুরো নামটি বলে আবার চেষ্টা করো");
            AutomationElement target=matches[0];object pattern;
            try{
                if(target.TryGetCurrentPattern(SelectionItemPattern.Pattern,out pattern))((SelectionItemPattern)pattern).Select();
                else if(target.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))((InvokePattern)pattern).Invoke();
                else throw new Exception("চ্যাটের নাম পাওয়া গেছে, কিন্তু এই অ্যাপ নিরাপদভাবে সেটি খুলতে দেয়নি");
            }catch(ElementNotAvailableException){throw new Exception("চ্যাট তালিকাটি বদলে গেছে—নামটি আবার বলো");}
            Thread.Sleep(300);
            if(cancelled()||Native.GetForegroundWindow()!=window)throw new Exception("চ্যাট বদলেছে—আবার নামটি বেছে নাও");
            FocusedTextTarget.Capture(window,cancelled,true);
            return requested.Trim();
        }
    }
    sealed class FocusedTextTarget : IDraftTarget {
        readonly AutomationElement field;
        readonly IntPtr window;
        readonly string title;
        readonly string context;
        readonly int[] runtime;
        readonly bool browser,messageField,supportedChat;
        readonly Func<bool> cancelled;
        FocusedTextTarget(AutomationElement f,IntPtr w,string t,bool b,bool m,bool chat,Func<bool> c){field=f;window=w;title=t;browser=b;messageField=m;supportedChat=chat;runtime=f.GetRuntimeId();cancelled=c;context=Context(f);}
        static string Context(AutomationElement element){StringBuilder value=new StringBuilder();AutomationElement parent=element;for(int n=0;n<6;n++){parent=TreeWalker.ControlViewWalker.GetParent(parent);if(parent==null||parent.Current.ControlType==ControlType.Window)break;string name=parent.Current.Name??"";if(name.Length>0&&parent.Current.ControlType==ControlType.Group)value.Append(name).Append('\n');}return value.ToString();}
        static string WindowTitle(IntPtr h){StringBuilder s=new StringBuilder(1024);GetWindowText(h,s,s.Capacity);return Regex.Replace(s.ToString(),@"^\(\d+\)\s*","");}
        public static string WindowTitleForSelection(IntPtr h){return WindowTitle(h);}
        public static FocusedTextTarget Capture(IntPtr expectedWindow,Func<bool> cancelled,bool chatOnly=false){
            IntPtr w=Native.GetForegroundWindow();if(w==IntPtr.Zero||w!=expectedWindow)throw new Exception("সামনের অ্যাপ বদলেছে। লেখার বক্সে ক্লিক করে আবার বলো");
            uint pid;Native.GetWindowThreadProcessId(w,out pid);if(pid==(uint)Program.ProcessId)throw new Exception("চ্যাট বা ডকুমেন্টের লেখার বক্স বেছে নাও");
            string process;using(Process p=Process.GetProcessById((int)pid))process=p.ProcessName.ToLowerInvariant();
            if(Regex.IsMatch(process,@"^(cmd|powershell|pwsh|windowsterminal|credentialuibroker|logonui|keepass|1password|bitwarden)$"))throw new Exception("এই অ্যাপে ভয়েস টাইপিং চালু করা হয়নি");
            bool browser=process=="chrome"||process=="msedge"||process=="firefox";
            bool supportedChat=ChatPlatform.IsSupported(w,process,WindowTitle(w));
            if(chatOnly&&!supportedChat)throw new Exception("Instagram, Facebook/Messenger বা WhatsApp-এর চ্যাটটি সামনে আনো");
            AutomationElement e=EditableFocus();
            if(chatOnly||e==null||(e!=null&&Regex.IsMatch(e.Current.Name??"",@"address and search bar|address bar",RegexOptions.IgnoreCase))){
                if(supportedChat){
                    AutomationElementCollection edits=AutomationElement.FromHandle(w).FindAll(TreeScope.Descendants,Condition.TrueCondition);
                    AutomationElement found=null;int count=0;
                    foreach(AutomationElement candidate in edits){ControlType type=candidate.Current.ControlType;if((type==ControlType.Edit||type==ControlType.Document)&&candidate.Current.IsEnabled&&!candidate.Current.IsOffscreen&&!candidate.Current.IsPassword&&Regex.IsMatch(candidate.Current.Name??"",@"message|type a message|send a message|reply|মেসেজ|বার্তা|চ্যাট",RegexOptions.IgnoreCase)){found=candidate;count++;}}
                    if(count!=1)throw new Exception(count>1?"একাধিক চ্যাট আছে। যে বন্ধুর চ্যাটে লিখবে, তার বক্সে একবার ক্লিক করো":"মেসেজ বক্স পাওয়া যায়নি। বক্সে একবার ক্লিক করো");
                    if(cancelled()||Native.GetForegroundWindow()!=w)throw new Exception("সামনের অ্যাপ বদলেছে—আবার বলো");
                    found.SetFocus();e=EditableFocus();
                }
            }
            if(e==null||e.Current.ProcessId!=(int)pid||e.Current.IsPassword||!e.Current.IsEnabled||e.Current.IsOffscreen)throw new Exception("আগে মেসেজ বা ডকুমেন্টের লেখার বক্সে একবার ক্লিক করো");
            string name=e.Current.Name??"";if(Regex.IsMatch(name,@"address and search bar|address bar|password",RegexOptions.IgnoreCase))throw new Exception("ঠিকানার বক্সে নয়, লেখার বক্সে ক্লিক করো");
            bool b=browser;
            bool m=Regex.IsMatch(name,@"message|type a message|send a message|reply|মেসেজ|বার্তা|চ্যাট",RegexOptions.IgnoreCase);
            FocusedTextTarget result=new FocusedTextTarget(e,w,WindowTitle(w),b,m,supportedChat,cancelled);result.Read();return result;
        }
        static AutomationElement EditableFocus(){
            AutomationElement e=AutomationElement.FocusedElement;
            for(int n=0;e!=null&&n<5;n++,e=TreeWalker.ControlViewWalker.GetParent(e)){
                if(e.Current.IsPassword)return null;
                object value;if(e.TryGetCurrentPattern(ValuePattern.Pattern,out value)&&!((ValuePattern)value).Current.IsReadOnly&&e.Current.ControlType==ControlType.Edit)return e;
                object text;if((e.Current.ControlType==ControlType.Edit||e.Current.ControlType==ControlType.Document)&&e.TryGetCurrentPattern(TextPattern.Pattern,out text)){
                    object readOnly=((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);if(readOnly is bool&&!(bool)readOnly)return e;
                }
            }return null;
        }
        void Verify(){
            if(cancelled())throw new OperationCanceledException();
            if(Native.GetForegroundWindow()!=window)throw new Exception("অ্যাপ বদলেছে—লেখার বক্সে ফিরে আবার বলো");
            AutomationElement current=EditableFocus();if(current==null||!Same(runtime,current.GetRuntimeId()))throw new Exception("লেখার বক্স বদলেছে—আবার বক্স বেছে নাও");
            if(browser&&WindowTitle(window)!=title)throw new Exception("ব্রাউজারের ট্যাব বা চ্যাট বদলেছে—আবার বক্স বেছে নাও");
            if(browser&&Context(field)!=context)throw new Exception("চ্যাটের প্রসঙ্গ বদলেছে—আবার বক্স বেছে নাও");
        }
        static bool Same(int[] a,int[] b){if(a.Length!=b.Length)return false;for(int n=0;n<a.Length;n++)if(a[n]!=b[n])return false;return true;}
        static string Canonical(string s){return (s??"").Replace("\r\n","\n").TrimEnd('\r','\n');}
        public string Read(){Verify();object pattern;if(field.TryGetCurrentPattern(ValuePattern.Pattern,out pattern))return Canonical(((ValuePattern)pattern).Current.Value);if(field.TryGetCurrentPattern(TextPattern.Pattern,out pattern))return Canonical(((TextPattern)pattern).DocumentRange.GetText(-1));throw new Exception("এই বক্সের লেখা যাচাই করা যায়নি");}
        public void Replace(string expected,string text){
            if(Read()!=Canonical(expected))throw new Exception("বক্সের লেখা বদলেছে—আগে এই লেখাটা নাও বলো");
            if(text.Length>32000)throw new Exception("একটি খসড়ায় সর্বোচ্চ বত্রিশ হাজার অক্ষর লেখো");
            InputKeys.CheckModifiers();Verify();if(expected.Length>0)InputKeys.SelectAll();if(cancelled())throw new OperationCanceledException();
            if(text.Length==0)InputKeys.Backspace();else InputKeys.Type(text);
            Thread.Sleep(120);if(Read()!=Canonical(text))throw new Exception("লেখা পুরোটা বসেছে কি না যাচাই হয়নি। চ্যাটে দেখে নাও; পাঠাইনি");
        }
        public void Send(string expected){
            if(!supportedChat||!messageField)throw new Exception("পাঠানো কেবল চেনা চ্যাট অ্যাপের মেসেজ বক্সে চালু; আগে সঠিক চ্যাটটি বেছে নাও");
            if(Read()!=Canonical(expected))throw new Exception("খসড়া বদলেছে—আগে আবার যাচাই করো");
            InputKeys.CheckModifiers();Verify();if(cancelled())throw new OperationCanceledException();InputKeys.Enter();
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder text,int length);
    }
    static class InputKeys {
        [StructLayout(LayoutKind.Sequential)]struct Key {public ushort vk,scan;public uint flags,time;public UIntPtr extra;}
        [StructLayout(LayoutKind.Sequential)]struct Mouse {public int x,y;public uint data,flags,time;public UIntPtr extra;}
        [StructLayout(LayoutKind.Explicit)]struct Union {[FieldOffset(0)]public Key key;[FieldOffset(0)]public Mouse mouse;}
        [StructLayout(LayoutKind.Sequential)]struct Input {public uint type;public Union data;}
        [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,Input[] inputs,int size);
        [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
        static Input K(ushort vk,ushort scan,uint flags){return new Input{type=1,data=new Union{key=new Key{vk=vk,scan=scan,flags=flags}}};}
        static void Send(Input[] list){if(SendInput((uint)list.Length,list,Marshal.SizeOf(typeof(Input)))!=list.Length)throw new Win32Exception("Windows লেখার ইনপুট নেয়নি");}
        public static void CheckModifiers(){foreach(int key in new[]{0x10,0x11,0x12,0x5b,0x5c})if((GetAsyncKeyState(key)&0x8000)!=0)throw new Exception("কিবোর্ডের Ctrl/Alt/Shift ছেড়ে আবার বলো");}
        public static void SelectAll(){Send(new[]{K(0x11,0,0),K(0x41,0,0),K(0x41,0,2),K(0x11,0,2)});}
        public static void Backspace(){Send(new[]{K(8,0,0),K(8,0,2)});}
        public static void Enter(){Send(new[]{K(13,0,0),K(13,0,2)});}
        public static void Type(string text){List<Input> list=new List<Input>(text.Length*2);foreach(char c in text){if(c=='\r')continue;if(c=='\n'){list.Add(K(0x10,0,0));list.Add(K(13,0,0));list.Add(K(13,0,2));list.Add(K(0x10,0,2));}else{list.Add(K(0,c,4));list.Add(K(0,c,6));}}Send(list.ToArray());}
        public static string AbiCheck(){if(IntPtr.Size==8&&Marshal.SizeOf(typeof(Input))!=40)throw new Exception("Windows keyboard input ABI size is invalid");return "PASS Windows x64 keyboard input ABI"+Environment.NewLine;}
    }
}

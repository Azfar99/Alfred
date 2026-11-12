using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Alfred {
    static class VoiceActions {
        static string Clean(string s){return Regex.Replace(s.Trim(),@"[।.!?]+$","").Trim();}
        static readonly Regex DictationStart=new Regex(@"^(?:(?:please\s+)?(?:start|begin)\s+(?:(English|Bengali|Bangla)\s+)?(?:dictation|voice typing)|(?:(বাংলায়|বাংলায়|বাংলা|ইংরেজিতে|ইংরেজি)\s+)?(?:ডিক্টেশন|ডিকটেশন|ভয়েস টাইপিং|ভয়েস টাইপিং|লেখা|লিখতে|টাইপ করা)\s+(?:শুরু করো|শুরু কর|চালু করো)|(?:আমি বলব তুমি লিখবে|আমি যা বলি তা লিখো|আমি যা বলবো তা লিখো|এখানে লেখো|এখানে লিখো|write here|type here))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex ChatDraftStart=new Regex(@"^(?:(?:please\s+)?(?:start|begin)\s+(?:a\s+)?(?:chat|message)\s+(?:draft|dictation)|(?:চ্যাট|মেসেজ)(?:ে|এর)?\s*(?:লেখা|খসড়া|খসড়া)\s*(?:শুরু করো|শুরু কর|চালু করো))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex DictationStop=new Regex(@"^(?:(?:alfred[, ]+|আলফ্রেড[, ]+)?(?:stop dictation|end dictation|finish dictation|stop voice typing|stop chat draft|finish chat draft|stop message draft|ডিক্টেশন (?:বন্ধ করো|শেষ করো|থামাও)|ডিকটেশন (?:বন্ধ করো|শেষ করো|থামাও)|ভয়েস টাইপিং বন্ধ করো|ভয়েস টাইপিং বন্ধ করো|চ্যাট(?:ের)? (?:খসড়া|খসড়া|লেখা) (?:বন্ধ করো|শেষ করো|থামাও)|মেসেজের লেখা (?:বন্ধ করো|শেষ করো|থামাও)|লেখা শেষ করো|লেখা বন্ধ করো))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex EditPrefix=new Regex(@"^(?:(?:alfred|আলফ্রেড)[, ]+)?(?:edit command|এডিট কমান্ড|সম্পাদনার কমান্ড)[\s,:।.!?]+(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline|RegexOptions.CultureInvariant);
        public static Intent ParseDictation(string raw){
            string text=(raw??"").Trim();
            if(text.Length==0)return null;
            if(DictationStop.IsMatch(Commands.Normalize(Clean(text))))return new Intent("dictation-stop","","ডিক্টেশন শেষ। লেখা দেখে নাও");
            Match edit=EditPrefix.Match(text);
            string control=edit.Success?edit.Groups[1].Value:Regex.Replace(text,@"^(?:alfred|আলফ্রেড)[\s,:।.!?]+","",RegexOptions.IgnoreCase);
            Intent action=Parse(control);
            if(action!=null&&action.Kind=="draft-send")return new Intent("dictation-send","","পাঠানোর নির্দেশ পেয়েছি—চ্যাটের খসড়া পাঠাচ্ছি");
            if(action!=null)switch(action.Kind){
                case "draft-rewrite":case "draft-append":case "draft-replace":case "draft-remove":
                case "draft-delete-word":case "draft-delete-sentence":case "draft-new-line":case "draft-undo":case "draft-clear":return action;
            }
            if(!edit.Success)return new Intent("draft-dictate",text,"কথা থেকে লিখেছি");
            return new Intent("dictation-edit-unknown","","এডিটের নির্দেশ বুঝিনি। বলো: শেষ বাক্যটা মুছো");
        }
        public static Intent Parse(string raw){
            string text=(raw??"").Trim();
            string s=Commands.Normalize(Clean(text));
            if(ChatDraftStart.IsMatch(s))return new Intent("dictation-start","","চ্যাটের লেখার মোড চালু করছি—যা বলবে খসড়ায় উঠবে, পাঠাব না");
            Match start=DictationStart.Match(s);
            if(start.Success){
                string language=start.Groups[1].Value+start.Groups[2].Value;
                string code=language.Length==0?"":language=="english"||language.StartsWith("ইংরেজি",StringComparison.Ordinal)?"en":"bn";
                return new Intent("dictation-start",code,"ডিক্টেশন শুরু। কথা বলো");
            }
            if(DictationStop.IsMatch(s))return new Intent("dictation-stop","","ডিক্টেশন শেষ। লেখা দেখে নাও");
            Match selectChat=Regex.Match(text,@"^(?:(?:ফেসবুক|মেসেঞ্জার|ইনস্টাগ্রাম|হোয়াটসঅ্যাপ|whatsapp|instagram|facebook|messenger)\s+)?(?:চ্যাট|ইনবক্স)(?:টা)?\s*(?:খোলো|ওপেন\s*করো|open)\s*(?:(?:with|সাথে|এর)\s+)?(.+)$",RegexOptions.IgnoreCase);
            if(!selectChat.Success)selectChat=Regex.Match(text,@"^(?:(?:open|select|choose|find)\s+(?:the\s+)?(?:chat|conversation|inbox)(?:\s+(?:with|for))?|(?:চ্যাট|ইনবক্স)(?:টা)?\s+(?:খোলো|ওপেন\s*করো)\s*(?:with|সাথে)?\s*)(.+)$",RegexOptions.IgnoreCase);
            if(selectChat.Success){string contact=Clean(selectChat.Groups[1].Value).Trim(' ','.',',','"','\'');if(contact.Length>0&&contact.Length<=100)return new Intent("chat-select",contact,"চ্যাটের নাম খুঁজছি—নির্বাচনের পর খসড়া লিখব, পাঠাব না");}
            Match ownerChat=Regex.Match(text,@"^(.+?)\s*(?:এর|র)\s*(?:চ্যাট|ইনবক্স)(?:টা)?\s*(?:খোলো|ওপেন\s*করো|open)$",RegexOptions.IgnoreCase);
            if(ownerChat.Success){string contact=Clean(ownerChat.Groups[1].Value).Trim();if(contact.Length>0&&contact.Length<=100)return new Intent("chat-select",contact,"চ্যাটের নাম খুঁজছি—নির্বাচনের পর খসড়া লিখব, পাঠাব না");}
            Match namedWrite=Regex.Match(text,@"^(?:write\s+to|message\s+to|text\s+to|send\s+a\s+message\s+to)\s+(.+?)(?:\s+(?:saying|that)\s+|\s*:\s*)(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(!namedWrite.Success)namedWrite=Regex.Match(text,@"^(.+?)(?:কে|এর\s+চ্যাটে)\s*(?:চ্যাটে\s*)?(?:লিখো|লেখো|মেসেজ\s*দাও|লিখে\s*দাও)\s*(?:হলো\s*যে|যে|:)?\s*(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(namedWrite.Success){string contact=Clean(namedWrite.Groups[1].Value).Trim(' ','.',',','"','\'');string message=namedWrite.Groups[2].Value.Trim();if(contact.Length>0&&contact.Length<=100&&message.Length>0){Intent i=new Intent("chat-write",message,"চ্যাটে খসড়া বসিয়েছি—দেখে আলাদা করে পাঠাতে বলো");i.Contact=contact;i.ChatTarget=true;return i;}}
            Match append=Regex.Match(text,@"^(?:আর(?:ও|ো)\s*(?:লেখো|লিখো)|যোগ\s*করো|append|add to (?:the )?draft)(?:\s*[:,-]\s*|\s+)(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(append.Success)return new Intent("draft-append",append.Groups[1].Value,"খসড়ায় যোগ করেছি—পাঠাইনি");
            Match write=Regex.Match(text,@"(?:^|\s)(?:এখানে\s+|এই\s+চ্যাটে\s+|ওখানে\s+)?(?:লিখো|লেখো|লিখে\s*দাও|টাইপ\s*করো|লিখবা|write here|type here|write this|type this|dictate)(?:\s+(?:হলো\s*যে|হল\s*যে|যে|as follows))?(?:\s*[:,-]\s*|\s+)(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(write.Success){if(Commands.Normalize(write.Groups[1].Value)=="না")return new Intent("unknown","","লেখার কাজ করিনি");Intent i=new Intent("draft-write",write.Groups[1].Value,"লেখার বক্সে খসড়া বসাচ্ছি—পাঠাইনি");i.ChatTarget=Regex.IsMatch(text.Substring(0,write.Groups[1].Index),@"চ্যাট|ফেসবুক|facebook|messenger|chat",RegexOptions.IgnoreCase);return i;}
            Match replace=Regex.Match(text,@"^(?:(?:তুমি\s+|এখন\s+)?(?:পুরো\s+|এই\s+)?(?:লেখা|টেক্সট|খসড়া)(?:টা)?\s*(?:বদলাও|বদলে\s*দাও|করেক্ট\s*করো|ঠিক\s*করো)|(?:rewrite|replace)(?:\s+the)?(?:\s+draft|\s+message)?(?:\s+with)?)(?:\s*[:,-]\s*|\s+)(.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(replace.Success)return Commands.Normalize(replace.Groups[1].Value)=="না"?new Intent("unknown","","খসড়া বদলাইনি"):new Intent("draft-rewrite",replace.Groups[1].Value,"খসড়া বদলে দিয়েছি—পাঠাইনি");
            string editText=Clean(text);
            Match word=Regex.Match(editText,@"^(.+?)\s*(?:এর\s*)?(?:জায়গায়|জায়গায়|বদলে)\s+(.+?)\s*(?:বসাও|দাও|করো|লিখো|লেখো)$",RegexOptions.IgnoreCase);
            if(word.Success)return new Intent("draft-replace",word.Groups[1].Value.Trim()+"\n"+word.Groups[2].Value.Trim(),"খসড়ার শব্দ বদলেছি—পাঠাইনি");
            Match englishWord=Regex.Match(editText,@"^change\s+(.+?)\s+to\s+(.+)$",RegexOptions.IgnoreCase);
            if(englishWord.Success)return new Intent("draft-replace",englishWord.Groups[1].Value.Trim()+"\n"+englishWord.Groups[2].Value.Trim(),"খসড়ার শব্দ বদলেছি—পাঠাইনি");
            bool negative=Regex.IsMatch(s,@"(^|[^\p{L}\p{M}\p{N}])(না|নয়|নাই|don't|do not)(?=$|[^\p{L}\p{M}\p{N}])");
            if(negative)return null;
            if(Regex.IsMatch(s,@"^(?:(?:এখন|এবার|ঠিক আছে|হ্যাঁ)\s+)?(?:পাঠাও|পাঠিয়ে দাও|পাঠিয়ে দাও|সেন্ড করো|send it|send message|send)$"))return new Intent("draft-send","","চ্যাটে পাঠানোর নির্দেশ দিয়েছি");
            if(Regex.IsMatch(s,@"^(?:শেষ শব্দ(?:টা)?\s*(?:মুছো|মুছে দাও)|delete last word)$"))return new Intent("draft-delete-word","","শেষ শব্দ মুছেছি—পাঠাইনি");
            if(Regex.IsMatch(s,@"^(?:শেষ বাক্য(?:টা)?\s*(?:মুছো|মুছে দাও)|delete last sentence)$"))return new Intent("draft-delete-sentence","","শেষ বাক্য মুছেছি—পাঠাইনি");
            if(Regex.IsMatch(s,@"^(?:নতুন (?:লাইন|অনুচ্ছেদ)(?: দাও| শুরু করো)?|new line|new paragraph)$"))return new Intent("draft-new-line","","নতুন লাইন দিয়েছি");
            if(Regex.IsMatch(s,@"^(?:আগের এডিট ফিরিয়ে দাও|শেষ পরিবর্তন (?:বাতিল করো|ফিরিয়ে দাও)|আনডু করো|undo|undo (?:the )?(?:last )?edit)$"))return new Intent("draft-undo","","শেষ পরিবর্তন ফিরিয়ে দিয়েছি");
            if(Regex.IsMatch(s,@"^(?:সব লেখা\s*(?:মুছো|মুছে দাও)|খসড়া বাতিল(?: করো)?|clear draft|cancel draft)$"))return new Intent("draft-clear","","এই খসড়া মুছেছি");
            if(Regex.IsMatch(s,@"^(?:এই লেখা(?:টা)? নাও|এই খসড়া নাও|adopt draft)$"))return new Intent("draft-adopt","","এই লেখাটাকে খসড়া হিসেবে নিয়েছি");
            Match remove=Regex.Match(editText,@"^(?:খসড়া থেকে\s+)?(.+?)\s+(?:শব্দটা\s+)?(?:মুছে দাও|মুছো)$",RegexOptions.IgnoreCase);
            if(!remove.Success)remove=Regex.Match(editText,@"^(?:remove|delete)\s+(?:the (?:word|phrase)\s+)?(.+?)(?:\s+from (?:the )?draft)?$",RegexOptions.IgnoreCase);
            if(remove.Success){string phrase=remove.Groups[1].Value.Trim(' ', '\"','\'','“','”');if(phrase.Length>0)return new Intent("draft-remove",phrase,"খসড়া থেকে কথাটা মুছেছি");}
            if(s.Contains("স্ক্রিনশট")||s.Contains("স্ক্রিন শট")||s.Contains("screenshot")||s.Contains("স্ন্যাপ নাও")||s.Contains("স্ন্যাপশট")){
                if(s.Contains("ফোল্ডার")||s.Contains("folder"))return new Intent("screenshots-open","","স্ক্রিনশটের ফোল্ডার খোলা হচ্ছে");
                if(s.Contains("শেষ")||s.Contains("latest"))return new Intent("screenshot-latest","","শেষ স্ক্রিনশট খোলা হচ্ছে");
                string area="full";
                if(s.Contains("অংশ বেছে")||s.Contains("অংশ নির্বাচন")||s.Contains("সিলেক্ট")||s.Contains("চিহ্নিত")||s.Contains("select region"))area="select";
                else if(s.Contains("সব মনিটর")||s.Contains("all monitors"))area="all";
                else if(s.Contains("উইন্ডো")||s.Contains("window"))area="window";
                else if((s.Contains("উপরে")||s.Contains("উপরের"))&&(s.Contains("ডান")||s.Contains("right")))area="top-right";
                else if((s.Contains("উপরে")||s.Contains("উপরের"))&&(s.Contains("বাম")||s.Contains("left")))area="top-left";
                else if((s.Contains("নিচে")||s.Contains("নিচের"))&&s.Contains("ডান"))area="bottom-right";
                else if((s.Contains("নিচে")||s.Contains("নিচের"))&&s.Contains("বাম"))area="bottom-left";
                else if(s.Contains("ডান")||s.Contains("right half"))area="right";
                else if(s.Contains("বাম")||s.Contains("left half"))area="left";
                else if(s.Contains("উপর")||s.Contains("top half"))area="top";
                else if(s.Contains("নিচ")||s.Contains("bottom half"))area="bottom";
                else if(s.Contains("মাঝ")||s.Contains("center"))area="center";
                else if(s.Contains("অংশ")||s.Contains("পোরশন")||s.Contains("portion"))area="select";
                return new Intent("screenshot",area,"স্ক্রিনশট F:\\Alfred-এ সেভ করেছি");
            }
            Match research=Regex.Match(text,@"(?:রিসার্চ\s*করো|গবেষণা\s*করো|research)\s+(.+)$",RegexOptions.IgnoreCase);
            if(research.Success)return new Intent("website","https://www.google.com/search?q="+Uri.EscapeDataString(research.Groups[1].Value),"গবেষণার জন্য ওয়েবে খুঁজছি");
            Match calc=Regex.Match(text,@"^(?:হিসাব\s*করো|কত\s*হয়|calculate)\s+(.+)$",RegexOptions.IgnoreCase);
            if(calc.Success)return new Intent("calculate",calc.Groups[1].Value,"হিসাবের ফল");
            return null;
        }
        public static string SelfTest(){
            Intent automatic=Parse("ডিক্টেশন শুরু করো");
            Intent english=Parse("start English dictation");
            Intent bengali=Parse("বাংলায় ডিক্টেশন শুরু করো");
            Intent chatMode=Parse("start chat draft"),chatModeBn=Parse("চ্যাটে লেখা শুরু করো"),chatStop=Parse("stop chat draft"),chatStopBn=Parse("চ্যাটের খসড়া থামাও");
            if(automatic==null||automatic.Kind!="dictation-start"||automatic.Value!=""||english==null||english.Value!="en"||bengali==null||bengali.Value!="bn"||chatMode==null||chatMode.Kind!="dictation-start"||chatModeBn==null||chatModeBn.Kind!="dictation-start"||chatStop==null||chatStop.Kind!="dictation-stop"||chatStopBn==null||chatStopBn.Kind!="dictation-stop")throw new Exception("Bilingual chat draft mode failed");
            Intent literal=ParseDictation("Please open Notepad and write hello. Then send it.");
            if(literal==null||literal.Kind!="draft-dictate"||literal.Value!="Please open Notepad and write hello. Then send it.")throw new Exception("Dictation content was interpreted as actions");
            Intent stop=ParseDictation("ডিক্টেশন বন্ধ করো");
            Intent edit=ParseDictation("এডিট কমান্ড, change hello to hi");
            Intent unsafeEdit=ParseDictation("এডিট কমান্ড, open Chrome");
            if(stop==null||stop.Kind!="dictation-stop"||edit==null||edit.Kind!="draft-replace"||unsafeEdit==null||unsafeEdit.Kind!="dictation-edit-unknown")throw new Exception("Dictation stop/edit boundary failed");
            string[][] liveControls={new[]{"শেষ শব্দটা মুছে দাও","draft-delete-word"},new[]{"সব লেখা মুছে দাও","draft-clear"},new[]{"change hello to hi","draft-replace"},new[]{"এবার পাঠাও","dictation-send"},new[]{"Alfred, send it","dictation-send"}};
            foreach(string[] c in liveControls){Intent action=ParseDictation(c[0]);if(action==null||action.Kind!=c[1])throw new Exception("Natural dictation control failed: "+c[0]);}
            foreach(string payload in new[]{"I told him to send it","এবার পাঠাও না","Please open Notepad and write hello. Then send it."})if(ParseDictation(payload).Kind!="draft-dictate")throw new Exception("Embedded or negated send became an action");
            Intent punctuation=ParseDictation("I will call you back tomorrow. এরপর দেখা হবে।");
            if(punctuation==null||punctuation.Value!="I will call you back tomorrow. এরপর দেখা হবে।")throw new Exception("Bilingual dictation punctuation failed");
            string[][] cases={
                new[]{"তুমি আমার ফেসবুক চ্যাটে ওখানে তুমি লিখবা হলো যে আমি বিকাল থেকে কীবোর্ডও টাচ করি নাই","draft-write"},
                new[]{"এখানে লেখো পিসি বন্ধ করো না","draft-write"},new[]{"লেখা বদলাও আজ আসব না","draft-rewrite"},new[]{"আরও লেখো পরে কথা হবে","draft-append"},new[]{"বিকাল এর জায়গায় সকাল বসাও","draft-replace"},
                new[]{"শেষ শব্দটা মুছে দাও","draft-delete-word"},new[]{"শেষ বাক্যটা মুছে দাও","draft-delete-sentence"},new[]{"সব লেখা মুছে দাও","draft-clear"},new[]{"এবার পাঠাও","draft-send"},
                new[]{"পুরো স্ক্রিনের স্ক্রিনশট নাও","screenshot"},new[]{"উপরের ডান কোণার স্ক্রিনশট নাও","screenshot"},new[]{"সামনের উইন্ডোর স্ক্রিনশট নাও","screenshot"},new[]{"অংশ নির্বাচন করে স্ক্রিনশট নাও","screenshot"},new[]{"স্ক্রিনশট ফোল্ডার খোলো","screenshots-open"},new[]{"রিসার্চ করো বাংলাদেশের পর্যটন","website"},new[]{"হিসাব করো ১২৫ গুণ ৮","calculate"}
            };
            foreach(string[] c in cases){Intent i=Parse(c[0]);if(i==null||i.Kind!=c[1])throw new Exception("Voice action failed: "+c[0]);}
            Intent openEnglish=Parse("open chat with Ayesha"),openBengali=Parse("চ্যাট খোলো রিমা"),sendEnglish=Parse("send"),writeEnglish=Parse("write here Hello, I will call later");
            Intent rewriteEnglish=Parse("rewrite the draft with I will call tomorrow");
            Intent namedEnglish=Parse("message to Ayesha saying I will call later"),namedBengali=Parse("রিমাকে লিখো আমি পরে ফোন করব");
            if(openEnglish==null||openEnglish.Kind!="chat-select"||openBengali==null||openBengali.Value!="রিমা"||sendEnglish==null||sendEnglish.Kind!="draft-send"||writeEnglish==null||writeEnglish.Kind!="draft-write"||rewriteEnglish==null||rewriteEnglish.Kind!="draft-rewrite"||namedEnglish==null||namedEnglish.Kind!="chat-write"||namedEnglish.Contact!="Ayesha"||namedBengali==null||namedBengali.Kind!="chat-write"||namedBengali.Contact!="রিমা"||Parse("hey")!=null)throw new Exception("Bilingual contact selection parsing failed");
            if(Parse("পাঠাও না")!=null||Parse("স্ক্রিনশট নিও না")!=null)throw new Exception("Negated voice action was accepted");
            if(Parse("এখানে লেখো না").Kind!="unknown"||Parse("লেখা বদলাও না").Kind!="unknown")throw new Exception("Negated dictation was typed");
            if(Parse("এখানে লেখো এরপর পাঠাও").Kind!="draft-write")throw new Exception("A dictation payload became a send command");
            if(Parse("এখানে লেখো তুমি কেমন আছো?").Value!="তুমি কেমন আছো?")throw new Exception("Dictation punctuation was lost");
            Intent chat=Parse("তুমি আমার ফেসবুক চ্যাটে ওখানে তুমি লিখবা হলো যে আমি বিকাল থেকে কীবোর্ডও টাচ করি নাই");
            if(!chat.ChatTarget||chat.Value!="আমি বিকাল থেকে কীবোর্ডও টাচ করি নাই")throw new Exception("Chat instruction/payload separation failed");
            if(Parse("উপরের ডান কোণার স্ক্রিনশট নাও").Value!="top-right")throw new Exception("Screenshot region routing failed");
            return "PASS bilingual continuous dictation, edit-only control, explicit sending, negation, screenshot and research routing"+Environment.NewLine;
        }
    }
}

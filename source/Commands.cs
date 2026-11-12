using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Alfred {
    public sealed class Intent {
        public string Kind, Value, Label;
        public bool ChatTarget;
        public string Contact;
        public Intent(string kind, string value, string label) { Kind=kind; Value=value; Label=label; }
    }
    public static class Commands {
        public static string Normalize(string text) {
            return Regex.Replace((text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s।!?]+", " ").Trim();
        }
        static bool Has(string s, params string[] words) {
            foreach(string w in words) if(s.Contains(w)) return true;
            return false;
        }
        public static Intent Parse(string raw) {
            string s=Normalize(raw);
            if(s.Length==0) return new Intent("unknown", "", "কথা বুঝতে পারিনি—আবার বলো।");
            Match note=Regex.Match((raw??"").Trim(),@"^(?:একটা\s+)?(?:নোট\s*(?:লিখে রাখো|করে রাখো|লিখো|রাখো)|লিখে রাখো|take a note|note that)\s*[:,-]?\s+(.+)$",RegexOptions.IgnoreCase);
            if(note.Success&&Normalize(note.Groups[1].Value)!="না")return new Intent("note-save",note.Groups[1].Value,"নোট F:\\Alfred-এ সেভ করেছি");
            Intent voiceAction=VoiceActions.Parse(raw);if(voiceAction!=null)return voiceAction;
            bool negative=Regex.IsMatch(s,@"(^|[^\p{L}\p{M}\p{N}])(না|নয়|নাই|don't|do not)(?=$|[^\p{L}\p{M}\p{N}])") || Has(s,"করো না","করবে না","don't");
            if(Has(s,"shutdown cancel","cancel shutdown","cancel restart","বন্ধ বাতিল","শাটডাউন বাতিল","রিস্টার্ট বাতিল")) return new Intent("cancel-power","","পিসি বন্ধ/রিস্টার্ট বাতিল");
            if(Has(s,"টাইমার বাতিল","টাইমার বন্ধ","cancel timer","cancel all timers"))return negative?new Intent("unknown","","টাইমার বাতিল করিনি"):new Intent("timer-cancel","","সব টাইমার বাতিল");
            if(Has(s,"টাইমার","timer")&&!Regex.IsMatch(s,@"(^|\s)(না|don't|do not)(\s|$)")){
                if(Has(s,"বাকি","remaining","কত সময়","কতক্ষণ"))return new Intent("timer-remaining","","টাইমারের বাকি সময়");
                double seconds=Helpers.DurationSeconds(s);if(seconds>0)return new Intent("timer",seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),"টাইমার চালু করেছি");
                return new Intent("unknown","","বলতে পারো: পাঁচ মিনিটের টাইমার দাও");
            }
            if(negative) return new Intent("unknown","","ঠিক আছে, এই নির্দেশে কোনো কাজ করিনি।");
            if(Has(s,"ফেসবুক","চ্যাট","messenger")&&Has(s,"পাঠিয়ে","পাঠিয়ে","টেক্সট পাঠাও","লেখার বক্স","টাইপ"))return new Intent("input-help","","লেখার বক্স বেছে বলো: এখানে লেখো ...; দেখে আলাদা করে বলো: এবার পাঠাও");
            Match youtube=Regex.Match(s,@"(?:ইউটিউবে?|ইউটুবে?|youtube)\s*(?:সার্চ|খুঁজো|খোঁজো|search)\s*(?:করো|কর|দাও|for)?\s+(.+)");
            if(youtube.Success)return new Intent("website","https://www.youtube.com/results?search_query="+Uri.EscapeDataString(youtube.Groups[1].Value),"ইউটিউবে খোঁজা হচ্ছে");
            Match map=Regex.Match(s,@"(?:ম্যাপে|গুগল ম্যাপে|maps?)\s*(?:খুঁজো|খোঁজো|সার্চ|search)\s*(?:করো|for)?\s+(.+)");
            if(map.Success)return new Intent("website","https://www.google.com/maps/search/?api=1&query="+Uri.EscapeDataString(map.Groups[1].Value),"ম্যাপে খোঁজা হচ্ছে");
            Match requestedSearch=Regex.Match(s,@"(?:গুগলে\s*সার্চ\s*(?:করো|কর|দাও)?|সার্চ\s*(?:করো|কর|দাও)?|search(?:\s+for)?)\s+(.+)");
            if(requestedSearch.Success)return new Intent("website","https://www.google.com/search?q="+Uri.EscapeDataString(requestedSearch.Groups[1].Value),"গুগলে খোঁজা হচ্ছে");
            if(Has(s,"আমার নোট","নোটগুলো","নোট খোলো","open notes","my notes"))return new Intent("notes-open","","তোমার নোট খোলা হচ্ছে");
            if(Has(s,"ক্লিপবোর্ড")&&Has(s,"নোট","সেভ","রাখো"))return new Intent("clipboard-note","","ক্লিপবোর্ডের লেখা নোটে সেভ করেছি");
            if(Has(s,"মাইক বন্ধ","মাইকটা বন্ধ","মাইক অফ","মাইকটা অফ","মাইক্রোফোন বন্ধ","শোনা বন্ধ","ঘুমাও","stop listening","pause microphone","microphone off"))return new Intent("pause","","মাইক বন্ধ");
            if(Has(s,"ডাউনলোড","downloads"))return new Intent("user-folder","downloads","ডাউনলোড ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"ডকুমেন্টস","ডকুমেন্ট ফোল্ডার","documents folder"))return new Intent("user-folder","documents","ডকুমেন্টস ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"ডেস্কটপ ফোল্ডার","desktop folder"))return new Intent("user-folder","desktop","ডেস্কটপ ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"ছবির ফোল্ডার","পিকচার ফোল্ডার","pictures folder"))return new Intent("user-folder","pictures","ছবির ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"ভিডিও ফোল্ডার","videos folder"))return new Intent("user-folder","videos","ভিডিও ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"পরের গান","পরবর্তী গান","next track","next song"))return new Intent("media-next","","পরের গান");
            if(Has(s,"আগের গান","previous track","previous song"))return new Intent("media-previous","","আগের গান");
            if(Has(s,"গান থামাও","মিডিয়া থামাও","pause music","pause media"))return new Intent("media-pause","","মিডিয়া থামানো হচ্ছে");
            if(Has(s,"গান চালাও","মিডিয়া চালাও","resume music","play music"))return new Intent("media-play","","মিডিয়া চালানো হচ্ছে");
            if(Has(s,"কত র‍্যাম","র‍্যাম কত","র্যাম কত","ram free","free ram","মেমোরি কত"))return new Intent("memory-info","","র‍্যামের তথ্য");
            if(Has(s,"কত জায়গা","ফাঁকা জায়গা","ফাঁকা জায়গা","disk space","free space"))return new Intent("disk-info","","F: ড্রাইভের ফাঁকা জায়গা");
            if(Has(s,"তারিখ কত","কত তারিখ","আজকের তারিখ","today's date","what date"))return new Intent("date","",DateTime.Now.ToString("dd MMMM yyyy"));
            if(Has(s,"মিনিমাইজ","minimize"))return new Intent("minimize","","সামনের উইন্ডো মিনিমাইজ করা হচ্ছে");
            if(Has(s,"উইন্ডোজ সেটিংস","সেটিংস খোলো","open settings"))return new Intent("settings","","উইন্ডোজ সেটিংস খোলা হচ্ছে");
            string[][] appNames={
                new[]{"Discord","ডিসকর্ড","ডিসকোর্ড","discord"},new[]{"Steam","স্টিম","steam"},new[]{"VALORANT","ভ্যালোরেন্ট","ভ্যালোরান্ট","valorant"},
                new[]{"Grand Theft Auto V Enhanced","জিটিএ ফাইভ","জিটিএ ৫","gta five","gta 5"},new[]{"Rockstar Games Launcher","রকস্টার","rockstar"},
                new[]{"Word","মাইক্রোসফট ওয়ার্ড","ওয়ার্ড খোলো","ওয়ার্ড খোলো","open word"},new[]{"Excel","এক্সেল","excel"},new[]{"PowerPoint","পাওয়ারপয়েন্ট","পাওয়ারপয়েন্ট","powerpoint"},
                new[]{"Visual Studio Code","ভিএস কোড","ভি এস কোড","ভিজুয়াল স্টুডিও কোড","vs code","visual studio code"},new[]{"Wondershare Filmora 13","ফিলমোরা","filmora"},
                new[]{"VLC media player","ভিএলসি","ভি এল সি","vlc"},new[]{"Audacity","অডাসিটি","audacity"},new[]{"Adobe Acrobat","অ্যাক্রোব্যাট","acrobat"},new[]{"Task Manager","টাস্ক ম্যানেজার","task manager"}
            };
            foreach(string[] entry in appNames)for(int n=1;n<entry.Length;n++)if(s.Contains(entry[n]))return Has(s,"বন্ধ","close","stop")?new Intent("unknown","","এই অ্যাপ বন্ধ করার কমান্ড রাখা হয়নি"):new Intent("app",entry[0],entry[0]+" খোলা হচ্ছে");
            if(Has(s,"শাটডাউন","shutdown","shut down") || (Has(s,"পিসি","কম্পিউটার","computer"," pc") && Has(s,"বন্ধ","turn off"))) return new Intent("shutdown","","পিসি বন্ধ করব?");
            if(Has(s,"রিস্টার্ট","restart","reboot")) return new Intent("restart","","পিসি রিস্টার্ট করব?");
            if(Has(s,"আলফ্রেড বন্ধ","alfred quit","quit alfred","exit alfred","তুমি বন্ধ হও")) return new Intent("quit","","Alfred পুরোপুরি বন্ধ");
            if(Has(s,"মাইক বন্ধ","শোনা বন্ধ","ঘুমাও","stop listening","pause microphone")) return new Intent("pause","","মাইক বন্ধ");
            if(Has(s,"ক্যালকুলেটর","ক্যালকুলেটার","ক্যালকুলেটারে","calculator")) return new Intent("calculator","","ক্যালকুলেটর খোলা হচ্ছে");
            if(Has(s,"নোটপ্যাড","নোট প্যাড","নোটপেড","নোট পেড","নোর্ডপেট","notepad")) return new Intent("notepad","","নোটপ্যাড খোলা হচ্ছে");
            if(Has(s,"আলফ্রেড ফোল্ডার","alfred folder")) return new Intent("folder","","Alfred-এর ফোল্ডার খোলা হচ্ছে");
            if(Has(s,"ফাইল এক্সপ্লোরার","file explorer","এক্সপ্লোরার")) return new Intent("explorer","","ফাইল এক্সপ্লোরার খোলা হচ্ছে");
            if(Has(s,"ভলিউম","volume","সাউন্ড","আওয়াজ","আওয়াজ")) {
                if(Has(s,"মিউট","mute","চুপ","সাউন্ড বন্ধ")) return new Intent("mute","","সাউন্ড মিউট পরিবর্তন");
                if(Has(s,"কমা","কমান","কম কর","কমিয়ে","কমিয়ে","down","lower","decrease")) return new Intent("volume-down","","ভলিউম কমানো হচ্ছে");
                if(Has(s,"বাড়া","বাড়াও","বাড়াও","বাড়িয়ে","বাড়িয়ে","বেশি","up","increase")) return new Intent("volume-up","","ভলিউম বাড়ানো হচ্ছে");
            }
            if(Has(s,"কটা বাজে","কয়টা বাজে","কয়টা বাজে","কয়টা বাজে","সময় বল","সময় বল","what time","time now")) return new Intent("time","",DateTime.Now.ToString("h:mm tt"));
            if(Has(s,"হেল্প","সাহায্য","কি করতে পারো","কী করতে পারো","help","commands")) return new Intent("help","","কমান্ডের তালিকা");
            string spaced=s.Replace("ডট", ".").Replace(" dot ",".");
            spaced=Regex.Replace(spaced,@"\s*\.\s*", ".");
            Match domain=Regex.Match(spaced,@"(?i)(?:https?://)?(?:www\.)?[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+(?::[0-9]{1,5})?(?:/[^\s<>""']*)?");
            if(domain.Success) {
                string url=domain.Value;
                if(!url.StartsWith("http://") && !url.StartsWith("https://")) url="https://"+url;
                Uri uri;
                if(Uri.TryCreate(url,UriKind.Absolute,out uri) && (uri.Scheme=="http"||uri.Scheme=="https") && String.IsNullOrEmpty(uri.UserInfo)) return new Intent("website",uri.AbsoluteUri,"ওয়েবসাইট খোলা হচ্ছে: "+uri.Host);
            }
            Match search=Regex.Match(s,@"(?:গুগলে\s*সার্চ\s*(?:করো|কর|দাও)?|সার্চ\s*(?:করো|কর|দাও)?|search(?:\s+for)?)\s+(.+)");
            if(search.Success && search.Groups[1].Value.Length>0) return new Intent("website","https://www.google.com/search?q="+Uri.EscapeDataString(search.Groups[1].Value),"গুগলে খোঁজা হচ্ছে");
            if(Has(s,"ইউটিউব","ইউটুব","youtube")) return new Intent("website","https://www.youtube.com/","ইউটিউব খোলা হচ্ছে");
            if(Has(s,"ফেসবুক","facebook")) return new Intent("website","https://www.facebook.com/","ফেসবুক খোলা হচ্ছে");
            if(Has(s,"জিমেইল","জি মেইল","gmail")) return new Intent("website","https://mail.google.com/","জিমেইল খোলা হচ্ছে");
            if(Has(s,"চ্যাটজিপিটি","চ্যাট জিপিটি","chatgpt")) return new Intent("website","https://chatgpt.com/","ChatGPT খোলা হচ্ছে");
            if(Has(s,"গুগল","google")) return new Intent("website","https://www.google.com/","গুগল খোলা হচ্ছে");
            if(Has(s,"ক্রোম","chrome","ব্রাউজার","browser")) return new Intent("browser","","ক্রোম খোলা হচ্ছে");
            return new Intent("unknown","","এই কমান্ডটা এখনও পারি না। বাম ক্লিক করে কমান্ডের তালিকা দেখো।");
        }
        public static string SelfTest() {
            if(!Orb.WakePhraseStartsAtUtterance(-300)||!Orb.WakePhraseStartsAtUtterance(0)||!Orb.WakePhraseStartsAtUtterance(1200)||Orb.WakePhraseStartsAtUtterance(-301)||Orb.WakePhraseStartsAtUtterance(1201)||Orb.WakePhraseStartsAtUtterance(-1))throw new Exception("Wake phrase utterance-start gate failed");
            string[][] cases={
                new[]{"এখন তুমি ক্যালকুলেটর খোলো","calculator"},
                new[]{"একটু ক্রোমে ঢুকে ইউটিউবে যাও","website"},
                new[]{"এখন পিসি বন্ধ করো","shutdown"},
                new[]{"পিসি বন্ধ করো না","unknown"},
                new[]{"পিসি বন্ধ না, ক্রোম খোলো","unknown"},
                new[]{"রিস্টার্ট না।","unknown"},
                new[]{"শাটডাউন বাতিল করো","cancel-power"},
                new[]{"ভলিউম কমাও","volume-down"},
                new[]{"সাউন্ড বাড়াও","volume-up"},
                new[]{"নোটপ্যাড চালু করো","notepad"},
                new[]{"নোর্ডপেট খুলত হিটু?","notepad"},
                new[]{"নোট প্যাড খোলো","notepad"},
                new[]{"ক্রোমে example.com এ যাও","website"},
                new[]{"গুগলে সার্চ করো বাংলাদেশের আবহাওয়া","website"},
                new[]{"কয়টা বাজে","time"},
                new[]{"মাইক বন্ধ করো","pause"},
                new[]{"আলফ্রেড বন্ধ করো","quit"},
                new[]{"open calculator","calculator"},
                new[]{"Can you open the calculator for me?","calculator"},
                new[]{"javascript:alert(1)","unknown"}
                ,new[]{"মাইকটা অফ রাখো","pause"},new[]{"এখন আপাতত মাইক্রোফোন বন্ধ রাখো","pause"},new[]{"পাঁচ মিনিটের টাইমার দাও","timer"},new[]{"নয় মিনিট টাইমার","timer"},new[]{"টাইমার বাতিল করো","timer-cancel"},new[]{"টাইমারে কত সময় বাকি","timer-remaining"},new[]{"নোট লিখে রাখো কালকে বাজার করব না","note-save"},new[]{"নোট লিখে রাখো পিসি বন্ধ করো","note-save"},new[]{"আমার নোট খোলো","notes-open"},new[]{"ডাউনলোড ফোল্ডার খোলো","user-folder"},new[]{"ডিসকর্ড খোলো","app"},new[]{"এক্সেল চালু করো","app"},new[]{"পরের গান দাও","media-next"},new[]{"আজকের তারিখ বলো","date"},new[]{"ইউটিউবে সার্চ করো বাংলা গান","website"}
            };
            StringBuilder sb=new StringBuilder();
            foreach(string[] c in cases) {
                Intent i=Parse(c[0]);
                if(i.Kind!=c[1]) throw new Exception("Intent failed: "+c[0]+" => "+i.Kind+", expected "+c[1]);
                sb.AppendLine("PASS "+c[0]+" => "+i.Kind);
            }
            if(Parse("ক্রোমে example.com এ যাও").Value!="https://example.com/") throw new Exception("URL routing failed");
            if(!Parse("ইউটিউবে সার্চ করো বাংলা গান").Value.StartsWith("https://www.youtube.com/results?search_query="))throw new Exception("YouTube search routing failed");
            if(Parse("নোট লিখে রাখো পিসি বন্ধ করো").Value!="পিসি বন্ধ করো")throw new Exception("Note payload handling failed");
            return sb.ToString()+Helpers.SelfTest()+VoiceActions.SelfTest()+DraftSession.SelfTest()+ScreenGrab.SelfTest()+SmallCalculation.SelfTest()+InputKeys.AbiCheck();
        }
    }
}

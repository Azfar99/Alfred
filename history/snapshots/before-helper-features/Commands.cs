using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Alfred {
    public sealed class Intent {
        public string Kind, Value, Label;
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
            bool negative=Regex.IsMatch(s,@"(^|[^\p{L}\p{M}\p{N}])(না|নয়|নাই|don't|do not)(?=$|[^\p{L}\p{M}\p{N}])") || Has(s,"করো না","করবে না","don't");
            if(Has(s,"shutdown cancel","cancel shutdown","cancel restart","বন্ধ বাতিল","শাটডাউন বাতিল","রিস্টার্ট বাতিল")) return new Intent("cancel-power","","পিসি বন্ধ/রিস্টার্ট বাতিল");
            if(negative) return new Intent("unknown","","ঠিক আছে, এই নির্দেশে কোনো কাজ করিনি।");
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
                new[]{"javascript:alert(1)","unknown"}
            };
            StringBuilder sb=new StringBuilder();
            foreach(string[] c in cases) {
                Intent i=Parse(c[0]);
                if(i.Kind!=c[1]) throw new Exception("Intent failed: "+c[0]+" => "+i.Kind+", expected "+c[1]);
                sb.AppendLine("PASS "+c[0]+" => "+i.Kind);
            }
            if(Parse("ক্রোমে example.com এ যাও").Value!="https://example.com/") throw new Exception("URL routing failed");
            return sb.ToString();
        }
    }
}

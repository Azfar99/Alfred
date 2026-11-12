using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Alfred {
    static class Helpers {
        static Dictionary<string,string> apps;
        public static string FindApp(string name) {
            if(apps==null){
                apps=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                string[] roots={Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)};
                foreach(string root in roots)foreach(string pattern in new[]{"*.lnk","*.url"})try{
                    foreach(string path in Directory.GetFiles(root,pattern,SearchOption.AllDirectories)){
                        string key=Path.GetFileNameWithoutExtension(path);
                        if(!apps.ContainsKey(key))apps.Add(key,path);
                    }
                }catch(IOException){}catch(UnauthorizedAccessException){}
            }
            string result;return apps.TryGetValue(name,out result)?result:null;
        }
        public static string NotesFile {get{return Path.Combine(Program.Data,"notes","notes.txt");}}
        public static void SaveNote(string text){
            Directory.CreateDirectory(Path.GetDirectoryName(NotesFile));
            File.AppendAllText(NotesFile,"["+DateTime.Now.ToString("yyyy-MM-dd HH:mm")+"] "+text.Trim()+Environment.NewLine,Encoding.UTF8);
        }
        public static double DurationSeconds(string text){
            string s=text;
            for(int i=0;i<10;i++)s=s.Replace((char)('০'+i),(char)('0'+i));
            if(s.Contains("আধা ঘণ্টা")||s.Contains("আধা ঘন্টা"))return 1800;
            Dictionary<string,double> numbers=new Dictionary<string,double>{
                {"এক",1},{"দুই",2},{"তিন",3},{"চার",4},{"পাঁচ",5},{"ছয়",6},{"ছয়",6},{"সাত",7},{"আট",8},{"নয়",9},{"নয়",9},{"দশ",10},{"বারো",12},{"পনেরো",15},{"বিশ",20},{"পঁচিশ",25},{"ত্রিশ",30},{"চল্লিশ",40},{"পঁয়তাল্লিশ",45},{"ষাট",60},{"দেড়",1.5},{"দেড়",1.5}
            };
            double total=0;
            MatchCollection matches=Regex.Matches(s,@"(?<n>\d+(?:\.\d+)?|পঁয়তাল্লিশ|পঁচিশ|পনেরো|চল্লিশ|ত্রিশ|বারো|বিশ|ষাট|দেড়|দেড়|এক|দুই|তিন|চার|পাঁচ|ছয়|ছয়|সাত|আট|নয়|নয়|দশ)\s*(?<u>সেকেন্ড|মিনিট|ঘণ্টা|ঘন্টা|seconds?|minutes?|hours?)",RegexOptions.IgnoreCase);
            foreach(Match m in matches){double n;if(!Double.TryParse(m.Groups["n"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out n)&&!numbers.TryGetValue(m.Groups["n"].Value,out n))continue;string u=m.Groups["u"].Value;total+=n*(u.StartsWith("ঘ")||u.StartsWith("hour")?3600:u.StartsWith("মিনিট")||u.StartsWith("minute")?60:1);}
            return total>=1&&total<=86400?total:0;
        }
        public static string Folder(string name){
            switch(name){
                case "downloads":return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
                case "documents":return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case "desktop":return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                case "pictures":return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case "videos":return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }return Program.Root;
        }
        [StructLayout(LayoutKind.Sequential)] struct MemoryStatus {public uint length,load;public ulong totalPhysical,availablePhysical,totalPage,availablePage,totalVirtual,availableVirtual,extended;}
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        public static string MemoryInfo(){MemoryStatus m=new MemoryStatus();m.length=(uint)Marshal.SizeOf(typeof(MemoryStatus));if(!GlobalMemoryStatusEx(ref m))return "র‍্যামের তথ্য পাওয়া যায়নি";return "ফাঁকা RAM: "+(m.availablePhysical/1073741824.0).ToString("F1")+" GB / "+(m.totalPhysical/1073741824.0).ToString("F1")+" GB";}
        public static string DiskInfo(){DriveInfo d=new DriveInfo(Path.GetPathRoot(Program.Root));return d.Name+" ড্রাইভে ফাঁকা: "+(d.AvailableFreeSpace/1073741824.0).ToString("F1")+" GB";}
        public static void Notice(string text){new SilentNotice(text).Show();}
        public static string SelfTest(){
            if(DurationSeconds("৫ মিনিটের টাইমার দাও")!=300||DurationSeconds("পাঁচ মিনিট টাইমার")!=300||DurationSeconds("নয় মিনিট")!=540||DurationSeconds("1 minute 30 seconds")!=90||DurationSeconds("দেড় ঘণ্টা")!=5400||DurationSeconds("0 seconds")!=0||DurationSeconds("48 hours")!=0)throw new Exception("Timer duration checks failed");
            return "PASS Bengali/English timer durations and bounds"+Environment.NewLine;
        }
    }
    sealed class SilentNotice : Form {
        readonly System.Windows.Forms.Timer life=new System.Windows.Forms.Timer();
        public SilentNotice(string text){
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;ClientSize=new Size(340,90);BackColor=Color.FromArgb(24,30,44);
            Rectangle area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-Width-20,area.Bottom-Height-20);
            Controls.Add(new Label{Text="Alfred · "+text,Font=new Font("Nirmala UI",11),ForeColor=Color.FromArgb(210,235,255),Bounds=new Rectangle(16,14,308,65)});
            life.Interval=6500;life.Tick+=delegate{Close();};Shown+=delegate{life.Start();};FormClosed+=delegate{life.Dispose();};
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x80|0x8000000;return p;}}
    }
}

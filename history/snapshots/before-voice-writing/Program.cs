using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Alfred {
    static class Program {
        public static readonly string Root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Data=Path.Combine(Root,"data"), Temp=Path.Combine(Root,"temp");
        public static readonly int ProcessId=Process.GetCurrentProcess().Id;
        static Mutex mutex;
        public static EventWaitHandle PanelEvent,ListenEvent,QuitEvent;
        [STAThread] static void Main(string[] args) {
            Directory.CreateDirectory(Data);Directory.CreateDirectory(Temp);
            Environment.SetEnvironmentVariable("TEMP",Temp);Environment.SetEnvironmentVariable("TMP",Temp);
            Environment.CurrentDirectory=Root;
            if(args.Length>0 && args[0]=="--self-test") {
                try {File.WriteAllText(Path.Combine(Root,"build","tests.txt"),Commands.SelfTest(),Encoding.UTF8);Environment.ExitCode=0;}
                catch(Exception ex){File.WriteAllText(Path.Combine(Root,"build","tests.txt"),ex.ToString(),Encoding.UTF8);Environment.ExitCode=1;}return;
            }
            PanelEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Panel.F");
            ListenEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Listen.F");
            QuitEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Quit.F");
            bool first;mutex=new Mutex(true,"Local\\Alfred.SmallAssistant.F",out first);
            if(!first){if(args.Length>0){if(args[0]=="--panel")PanelEvent.Set();if(args[0]=="--listen")ListenEvent.Set();if(args[0]=="--quit")QuitEvent.Set();}return;}
            if(args.Length>0&&args[0]=="--quit")return;
            if(args.Length>0&&args[0]=="--panel")PanelEvent.Set();
            if(args.Length>0&&args[0]=="--listen")ListenEvent.Set();
            try {
                Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Orb());
            }catch(Exception ex){File.WriteAllText(Path.Combine(Data,"error.txt"),ex.ToString(),Encoding.UTF8);}
            finally{mutex.ReleaseMutex();mutex.Dispose();}
        }
        public static string Json(string s){return "\""+(s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";}
    }
    sealed class Orb : Form {
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        readonly ToolTip tip=new ToolTip();
        readonly ContextMenuStrip menu=new ContextMenuStrip();
        readonly NotifyIcon tray=new NotifyIcon();
        readonly Bitmap surface=new Bitmap(160,160,PixelFormat.Format32bppArgb);
        Graphics surfaceGraphics;
        readonly PointF[] wavePoints=new PointF[193];
        static readonly Color[] WavePalette={Color.FromArgb(57,183,255),Color.FromArgb(129,104,255),Color.FromArgb(255,64,186),Color.FromArgb(255,137,85)};
        SpeechRecognitionEngine wake;
        WaveCapture capture;
        Process transcriber;
        volatile bool closing,paused,cancelJob;
        bool busy,hover,down,dragged,menuOpen,overlayHidden,overlayProminent;
        DateTime overlayCheck=DateTime.MinValue,previewUntil=DateTime.MinValue;
        IntPtr lastExternalWindow;
        readonly List<DateTime> timers=new List<DateTime>();
        Point dragOrigin,windowOrigin;
        DateTime phaseStart=DateTime.UtcNow, flashUntil=DateTime.MinValue;
        readonly Stopwatch visualClock=Stopwatch.StartNew();
        string phase="starting", message="চালু হচ্ছে…", lastHeard="",lastResult="",pendingPower="";
        DateTime powerDeadline;
        int audioLevel,diameter=160;
        float visualLevel;
        Form panel; Label panelState,panelHeard; TextBox panelInput; Button panelMic;
        CancellationTokenSource job;
        readonly object engineGate=new object();
        int wakeGeneration;
        public Orb() {
            Text="Alfred";Name="AlfredOrb";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=diameter>=64;TopMost=false;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(diameter,diameter);Cursor=Cursors.Hand;
            surfaceGraphics=Graphics.FromImage(surface);surfaceGraphics.SmoothingMode=SmoothingMode.AntiAlias;surfaceGraphics.ScaleTransform(diameter/32f,diameter/32f);
            ReadSettings();
            using(GraphicsPath path=new GraphicsPath()){path.AddEllipse(0,0,diameter,diameter);Region=new Region(path);}
            menu.Font=new Font("Nirmala UI",10);menu.BackColor=Color.FromArgb(28,32,41);menu.ForeColor=Color.White;menu.ShowImageMargin=false;
            tray.Icon=Native.TrayGlyph();tray.Text="Alfred — বাম ক্লিকে মাইক ও অপশন";tray.Visible=true;tray.MouseClick+=delegate{ShowOptions();};
            menu.Closed+=delegate{menuOpen=false;hover=ClientRectangle.Contains(PointToClient(Cursor.Position));Render();};
            MouseEnter+=delegate{hover=true;Render();};MouseLeave+=delegate{if(!down){hover=false;Render();}};
            MouseDown+=OnDown;MouseMove+=OnMove;MouseUp+=OnUp;
            timer.Interval=90;timer.Tick+=OnTick;
            Shown+=delegate {Render();timer.Start();Native.RegisterHotKey(Handle,1,0x0001|0x0002|0x4000,(uint)Keys.A);Native.RegisterHotKey(Handle,2,0x0001|0x0002|0x4000,(uint)Keys.M);if(paused)SetState("paused","মাইক বন্ধ—মেনু থেকে হাতে চালু করো");else StartWake();};
            FormClosing+=OnClosing;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x80000;if(diameter<64)p.ExStyle|=0x80|0x8000000;return p;}}
        protected override void WndProc(ref Message m){if(m.Msg==0x312){if(m.WParam.ToInt32()==1){BeginListen();return;}if(m.WParam.ToInt32()==2){if(paused)Resume();else Pause();return;}}base.WndProc(ref m);}
        void Ui(Action a){if(closing||IsDisposed)return;try{if(InvokeRequired)BeginInvoke(a);else a();}catch(InvalidOperationException){}}
        void SetState(string state,string text) {
            phase=state;message=text;phaseStart=DateTime.UtcNow;timer.Interval=(state=="recording"||state=="processing")?45:90;
            tip.SetToolTip(this,"Alfred · "+text+"\nবাম ক্লিক: অপশন · টেনে সরাও · Ctrl+Alt+A: কথা বলো");
            if(panelState!=null&&!panelState.IsDisposed)panelState.Text=text;
            if(panelMic!=null&&!panelMic.IsDisposed)panelMic.Text=paused?"মাইক চালু করো":"মাইক বন্ধ করো";
            if(panelHeard!=null&&!panelHeard.IsDisposed)panelHeard.Text=(lastHeard.Length==0?"শেষ কমান্ড: —":"শুনেছি: "+lastHeard)+"\n"+lastResult;
            tray.Text=paused?"Alfred — মাইক বন্ধ (ক্লিক করে চালু করো)":state=="recording"?"Alfred — বাংলা কমান্ড শুনছি":"Alfred — Hello Alfred / Hey Alfred";
            RefreshOverlay(true);WriteStatus();Render();
        }
        void WriteStatus(){try{using(Process current=Process.GetCurrentProcess())File.WriteAllText(Path.Combine(Program.Data,"status.json"),"{\"state\":"+Program.Json(phase)+",\"message\":"+Program.Json(message)+",\"microphonePaused\":"+(paused?"true":"false")+",\"wakeEngineReady\":"+(wake!=null?"true":"false")+",\"overlayVisible\":"+(!overlayHidden?"true":"false")+",\"timers\":"+timers.Count+",\"workingSetMB\":"+(current.WorkingSet64/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+",\"x\":"+Left+",\"y\":"+Top+",\"size\":"+diameter+",\"version\":\"1.1\"}",Encoding.UTF8);}catch{}}
        void ReadSettings(){
            Rectangle area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-diameter-18,area.Top+54);
            try{string[] v=File.ReadAllLines(Path.Combine(Program.Data,"position.ini"));Location=Clamp(new Point(Int32.Parse(v[0]),Int32.Parse(v[1])));}catch{}
            try{paused=File.ReadAllText(Path.Combine(Program.Data,"microphone-paused.ini")).Trim()=="true";}catch{}
            try{foreach(string line in File.ReadAllLines(Path.Combine(Program.Data,"timers.txt"))){long ticks;if(Int64.TryParse(line,out ticks)&&ticks>=DateTime.MinValue.Ticks&&ticks<=DateTime.MaxValue.Ticks&&timers.Count<8)timers.Add(new DateTime(ticks,DateTimeKind.Utc));}}catch{}
        }
        void SaveTimers(){List<string> lines=new List<string>();foreach(DateTime due in timers)lines.Add(due.Ticks.ToString(CultureInfo.InvariantCulture));File.WriteAllLines(Path.Combine(Program.Data,"timers.txt"),lines.ToArray());}
        void SaveMicrophone(){File.WriteAllText(Path.Combine(Program.Data,"microphone-paused.ini"),paused?"true":"false",Encoding.UTF8);}
        void RefreshOverlay(bool force){
            if(!IsHandleCreated||closing||(!force&&DateTime.UtcNow<overlayCheck))return;
            overlayCheck=DateTime.UtcNow.AddMilliseconds(350);
            bool active=phase=="recording"||phase=="processing";
            bool prominent=active||menuOpen||down||DateTime.UtcNow<previewUntil;
            IntPtr foreground=Native.GetForegroundWindow();uint owner=0;Native.GetWindowThreadProcessId(foreground,out owner);
            bool external=foreground!=IntPtr.Zero&&owner!=(uint)Program.ProcessId&&!Native.IsDesktop(foreground);
            if(external)lastExternalWindow=foreground;
            Native.Rect rect;bool covered=external&&Native.GetWindowRect(foreground,out rect)&&new Rectangle(rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top).IntersectsWith(Bounds);
            bool hide=!prominent&&covered;
            bool changed=hide!=overlayHidden;
            if(changed){Native.ShowWindow(Handle,hide?0:4);overlayHidden=hide;}
            if(!hide&&(force||prominent!=overlayProminent))Native.SetWindowPos(Handle,new IntPtr(prominent?-1:-2),Left,Top,Width,Height,0x10|0x40);
            if(!hide&&!prominent&&external)Native.SetWindowPos(Handle,foreground,Left,Top,Width,Height,0x10);
            overlayProminent=prominent;timer.Interval=active?45:hide?400:90;
            if(changed)WriteStatus();
        }
        Point Clamp(Point p){Rectangle a=Screen.FromPoint(p).WorkingArea;return new Point(Math.Max(a.Left,Math.Min(p.X,a.Right-diameter)),Math.Max(a.Top,Math.Min(p.Y,a.Bottom-diameter)));}
        void SavePosition(){try{File.WriteAllLines(Path.Combine(Program.Data,"position.ini"),new[]{Left.ToString(),Top.ToString()});WriteStatus();}catch{}}
        void OnDown(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){down=true;dragged=false;Capture=true;dragOrigin=Cursor.Position;windowOrigin=Location;}}
        void OnMove(object sender,MouseEventArgs e){if(!down)return;Point p=Cursor.Position;int dx=p.X-dragOrigin.X,dy=p.Y-dragOrigin.Y;if(Math.Abs(dx)>3||Math.Abs(dy)>3)dragged=true;if(dragged){Location=Clamp(new Point(windowOrigin.X+dx,windowOrigin.Y+dy));Render();}}
        void OnUp(object sender,MouseEventArgs e){bool click=down&&!dragged;down=false;Capture=false;if(dragged)SavePosition();if((click&&e.Button==MouseButtons.Left)||e.Button==MouseButtons.Right)ShowOptions();}
        void OnTick(object sender,EventArgs e){
            if(Program.QuitEvent.WaitOne(0)){Close();return;}
            if(Program.PanelEvent.WaitOne(0))ShowPanel();
            if(Program.ListenEvent.WaitOne(0))BeginListen();
            bool timerDone=false;for(int n=timers.Count-1;n>=0;n--)if(DateTime.UtcNow>=timers[n]){timers.RemoveAt(n);timerDone=true;}
            if(timerDone){SaveTimers();Helpers.Notice("টাইমার শেষ হয়েছে।");WriteStatus();}
            audioLevel=capture!=null?capture.Level:0;
            if(pendingPower.Length>0 && DateTime.UtcNow>=powerDeadline){string action=pendingPower;pendingPower="";LaunchHidden(Path.Combine(Environment.SystemDirectory,"shutdown.exe"),(action=="restart"?"/r":"/s")+" /t 0");}
            RefreshOverlay(false);if(!overlayHidden)Render();
        }
        void StartWake(){
            if(closing||paused)return;
            int generation=Interlocked.Increment(ref wakeGeneration);
            SetState("starting","মাইক্রোফোন চালু হচ্ছে…");
            ThreadPool.QueueUserWorkItem(delegate{
                SpeechRecognitionEngine engine=null;
                try {
                    var recognizers=SpeechRecognitionEngine.InstalledRecognizers();
                    RecognizerInfo info=null;foreach(var r in recognizers)if(r.Culture.Name=="en-US"){info=r;break;}
                    if(info==null)throw new Exception("ইংরেজি wake recognizer পাওয়া যায়নি");
                    engine=new SpeechRecognitionEngine(info);
                    GrammarBuilder gb=new GrammarBuilder{Culture=info.Culture};gb.Append(new Choices("Alfred","hello Alfred","hey Alfred"));
                    Grammar grammar=new Grammar(gb);grammar.Name="AlfredWake";engine.LoadGrammar(grammar);
                    engine.SpeechRecognized+=delegate(object s,SpeechRecognizedEventArgs e){if(e.Result.Confidence>=0.65&&e.Result.Words.Count==2&&e.Result.Words[0].Confidence>=0.65)Ui(delegate{if(generation==wakeGeneration&&wake==engine&&!paused&&!busy)BeginListen();});};
                    engine.RecognizeCompleted+=delegate(object s,RecognizeCompletedEventArgs e){if(e.Error!=null)Ui(delegate{if(generation==wakeGeneration&&!busy&&!paused)SetState("error","মাইক সমস্যা—বাম ক্লিকে আবার চালু করো");});};
                    engine.SetInputToDefaultAudioDevice();
                    lock(engineGate){if(closing||paused||generation!=wakeGeneration){engine.Dispose();return;}wake=engine;engine.RecognizeAsync(RecognizeMode.Multiple);}
                    Ui(delegate{if(generation==wakeGeneration&&!busy&&!paused)SetState("idle","‘হ্যালো আলফ্রেড’ বা ‘হেই আলফ্রেড’ বলো");});
                }catch(Exception ex){lock(engineGate){if(wake==engine)wake=null;}if(engine!=null)try{engine.Dispose();}catch{}Ui(delegate{if(generation==wakeGeneration&&!paused)SetState("error","মাইক চালু হয়নি: "+ex.Message);});}
            });
        }
        void DisposeWake(){Interlocked.Increment(ref wakeGeneration);lock(engineGate){var old=wake;wake=null;if(old!=null){try{old.RecognizeAsyncCancel();old.SetInputToNull();old.Dispose();}catch{}}}}
        void Pause(){paused=true;cancelJob=true;SaveMicrophone();if(job!=null)job.Cancel();try{if(capture!=null)capture.Stop();}catch{}DisposeWake();SetState("paused","মাইক বন্ধ—শুধু মেনু থেকে হাতে চালু হবে");}
        void Resume(){if(busy){message="কাজ থামছে—একটু অপেক্ষা করো";return;}paused=false;SaveMicrophone();StartWake();}
        void BeginListen(){
            if(closing||busy)return;
            if(paused){Flash("মাইক বন্ধ আছে—ট্রে আইকনের মেনু থেকে হাতে চালু করো",false);return;}
            if(!File.Exists(Path.Combine(Program.Root,"models","ggml-bengali-q5_1.bin"))){SetState("error","বাংলার মডেল পাওয়া যায়নি");return;}
            busy=true;cancelJob=false;job=new CancellationTokenSource();lastHeard="";SetState("recording","শুনছি… এবার বাংলা কমান্ড বলো");
            ThreadPool.QueueUserWorkItem(delegate{
                string audio=Path.Combine(Program.Temp,"command.wav"),output=Path.Combine(Program.Temp,"command-transcript");
                try {
                    DisposeWake();
                    using(WaveCapture rec=new WaveCapture()) {
                        capture=rec;rec.Start();
                        bool heard=rec.WaitForEnd(delegate{return cancelJob||closing||job.IsCancellationRequested;},16);
                        if(cancelJob||closing)return;
                        if(!heard){Ui(delegate{Flash("কথা পাইনি—আবার ‘হ্যালো আলফ্রেড’ বা ‘হেই আলফ্রেড’ বলো",false);});return;}
                        rec.Save(audio);capture=null;
                    }
                    Ui(delegate{SetState("processing","বাংলা কমান্ড বুঝছি…");});
                    string text=Transcribe(audio,output,job.Token);
                    Ui(delegate{if(!cancelJob&&!paused&&!closing){lastHeard=text;HandleIntent(Commands.Parse(text));}});
                } catch(OperationCanceledException){}
                catch(Exception ex){Ui(delegate{Flash("সমস্যা: "+ex.Message,false);});}
                finally{
                    capture=null;TryDelete(audio);TryDelete(output+".txt");
                    GC.Collect(2,GCCollectionMode.Optimized,false);
                    Ui(delegate{busy=false;audioLevel=0;if(job!=null){job.Dispose();job=null;}if(!paused&&!closing){StartWake();}else if(paused)SetState("paused","মাইক বন্ধ—মেনু থেকে হাতে চালু করো");});
                }
            });
        }
        string Transcribe(string audio,string output,CancellationToken token){
            ProcessStartInfo si=new ProcessStartInfo{FileName=Path.Combine(Program.Root,"engine","Release","whisper-cli.exe"),WorkingDirectory=Program.Temp,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            si.Arguments="-m \""+Path.Combine(Program.Root,"models","ggml-bengali-q5_1.bin")+"\" -f \""+audio+"\" -l bn -t 4 -p 1 -nt -np -otxt -of \""+output+"\" -ng -bs 1 -bo 1 --prompt \"Notepad, Calculator, Google Chrome, YouTube, Facebook, Gmail, volume.\"";
            si.EnvironmentVariables["TEMP"]=Program.Temp;si.EnvironmentVariables["TMP"]=Program.Temp;
            using(Process p=new Process()){
                p.StartInfo=si;StringBuilder error=new StringBuilder();p.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data!=null&&error.Length<4000)error.AppendLine(e.Data);};p.OutputDataReceived+=delegate{};
                transcriber=p;p.Start();try{p.PriorityClass=ProcessPriorityClass.BelowNormal;}catch{}p.BeginErrorReadLine();p.BeginOutputReadLine();
                Stopwatch watch=Stopwatch.StartNew();while(!p.WaitForExit(100)){if(token.IsCancellationRequested||closing||watch.Elapsed.TotalSeconds>75){try{p.Kill();}catch{}throw new OperationCanceledException();}}
                p.WaitForExit();transcriber=null;if(p.ExitCode!=0)throw new Exception("ভাষা বোঝার ইঞ্জিন চালু হয়নি ("+p.ExitCode+")");
            }
            string result=File.Exists(output+".txt")?File.ReadAllText(output+".txt",Encoding.UTF8).Trim():"";
            result=System.Text.RegularExpressions.Regex.Replace(result,@"\[[^\]]*\]|\([^)]*(?:music|silence|blank)[^)]*\)","").Trim();
            return result;
        }
        static void TryDelete(string f){try{if(File.Exists(f))File.Delete(f);}catch{}}
        void HandleIntent(Intent i){
            try{
                switch(i.Kind){
                    case "calculator":Launch(Path.Combine(Environment.SystemDirectory,"calc.exe"),null);break;
                    case "notepad":Launch(Path.Combine(Environment.SystemDirectory,"notepad.exe"),null);break;
                    case "explorer":Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),null);break;
                    case "folder":Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+Program.Root+"\"");break;
                    case "website":OpenBrowser(i.Value);break;
                    case "browser":OpenBrowser("https://www.google.com/");break;
                    case "user-folder":Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+Helpers.Folder(i.Value)+"\"");break;
                    case "app":string shortcut=Helpers.FindApp(i.Value);if(shortcut==null)throw new Exception(i.Value+"-এর শর্টকাট পাওয়া যায়নি");Launch(shortcut,null);break;
                    case "settings":Launch("ms-settings:",null);break;
                    case "note-save":Helpers.SaveNote(i.Value);break;
                    case "clipboard-note":string copied=Clipboard.GetText();if(String.IsNullOrWhiteSpace(copied))throw new Exception("ক্লিপবোর্ডে লেখা নেই");Helpers.SaveNote(copied.Length>100000?copied.Substring(0,100000):copied);break;
                    case "notes-open":Directory.CreateDirectory(Path.GetDirectoryName(Helpers.NotesFile));if(!File.Exists(Helpers.NotesFile))File.WriteAllText(Helpers.NotesFile,"",Encoding.UTF8);Launch(Path.Combine(Environment.SystemDirectory,"notepad.exe"),"\""+Helpers.NotesFile+"\"");break;
                    case "timer":if(timers.Count>=8)throw new Exception("একসঙ্গে সর্বোচ্চ আটটি টাইমার রাখা যায়");timers.Add(DateTime.UtcNow.AddSeconds(Double.Parse(i.Value,CultureInfo.InvariantCulture)));SaveTimers();break;
                    case "timer-cancel":timers.Clear();SaveTimers();break;
                    case "timer-remaining":if(timers.Count==0)i.Label="এখন কোনো টাইমার চলছে না";else{DateTime first=timers[0];foreach(DateTime due in timers)if(due<first)first=due;TimeSpan left=first-DateTime.UtcNow;i.Label="পরের টাইমারে বাকি: "+Math.Max(0,(int)left.TotalMinutes)+" মিনিট "+Math.Max(0,left.Seconds)+" সেকেন্ড";}Helpers.Notice(i.Label);break;
                    case "media-play":Native.SendMessage(Handle,0x319,Handle,new IntPtr(46<<16));break;
                    case "media-pause":Native.SendMessage(Handle,0x319,Handle,new IntPtr(47<<16));break;
                    case "media-next":Native.SendMessage(Handle,0x319,Handle,new IntPtr(11<<16));break;
                    case "media-previous":Native.SendMessage(Handle,0x319,Handle,new IntPtr(12<<16));break;
                    case "memory-info":i.Label=Helpers.MemoryInfo();Helpers.Notice(i.Label);break;
                    case "disk-info":i.Label=Helpers.DiskInfo();Helpers.Notice(i.Label);break;
                    case "date":Helpers.Notice(i.Label);break;
                    case "minimize":if(lastExternalWindow!=IntPtr.Zero)Native.ShowWindow(lastExternalWindow,6);break;
                    case "volume-up":Native.SendMessage(Handle,0x319,Handle,new IntPtr(10<<16));Native.SendMessage(Handle,0x319,Handle,new IntPtr(10<<16));break;
                    case "volume-down":Native.SendMessage(Handle,0x319,Handle,new IntPtr(9<<16));Native.SendMessage(Handle,0x319,Handle,new IntPtr(9<<16));break;
                    case "mute":Native.SendMessage(Handle,0x319,Handle,new IntPtr(8<<16));break;
                    case "time":Helpers.Notice(i.Label);break;
                    case "help":i.Label="কমান্ডের তালিকা দেখতে আইকনে বাম ক্লিক করো";break;
                    case "pause":Pause();return;
                    case "quit":Close();return;
                    case "shutdown":case "restart":ConfirmPower(i.Kind);return;
                    case "cancel-power":pendingPower="";break;
                    default:Flash(i.Label,false);return;
                }
                Flash(i.Label,true);
            }catch(Exception ex){Flash("কাজ হয়নি: "+ex.Message,false);}
        }
        void ConfirmPower(string kind){
            bool restart=kind=="restart";
            DialogResult result=MessageBox.Show((restart?"পিসি রিস্টার্ট":"পিসি বন্ধ")+" করতে চাও?\n\nহ্যাঁ দিলে ৬০ সেকেন্ড পরে হবে। আগে কাজ সেভ করে নাও।\nAlfred-এর বাম ক্লিক মেনু থেকে বাতিল করতে পারবে।", "Alfred — নিশ্চিত করো",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2,MessageBoxOptions.DefaultDesktopOnly);
            if(result==DialogResult.Yes){pendingPower=kind;powerDeadline=DateTime.UtcNow.AddSeconds(60);Flash("৬০ সেকেন্ডের মধ্যে হবে—মেনু থেকে বাতিল করতে পারো",true);}else Flash("ঠিক আছে, বাতিল করেছি",true);
        }
        static void Launch(string exe,string args){Process.Start(new ProcessStartInfo{FileName=exe,Arguments=args??"",UseShellExecute=true});}
        static void LaunchHidden(string exe,string args){Process.Start(new ProcessStartInfo{FileName=exe,Arguments=args,UseShellExecute=false,CreateNoWindow=true});}
        static void OpenBrowser(string url){
            Uri u;if(!Uri.TryCreate(url,UriKind.Absolute,out u)||(u.Scheme!="http"&&u.Scheme!="https"))throw new Exception("ওয়েব ঠিকানাটা ঠিক নয়");
            string[] roots={Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)};
            foreach(string root in roots){string chrome=Path.Combine(root,"Google","Chrome","Application","chrome.exe");if(File.Exists(chrome)){Launch(chrome,"--new-tab \""+u.AbsoluteUri+"\"");return;}}
            Launch(u.AbsoluteUri,null);
        }
        void Flash(string text,bool good){lastResult=text;flashUntil=DateTime.UtcNow.AddSeconds(2);SetState(good?"success":"notice",text);}
        void ShowOptions(){
            menu.Items.Clear();menu.Items.Add(new ToolStripMenuItem("Alfred · "+(paused?"মাইক বন্ধ":busy?"কাজ করছি":"ডাকের অপেক্ষায়")){Enabled=false});menu.Items.Add(new ToolStripSeparator());
            AddMenu("এখন কথা বলো  ·  Ctrl+Alt+A",delegate{BeginListen();},!busy&&!paused);
            AddMenu((paused?"মাইক আবার চালু করো":"মাইক বন্ধ রাখো")+"  ·  Ctrl+Alt+M",delegate{if(paused)Resume();else Pause();},true);
            AddMenu("বাংলা কমান্ড ও অবস্থা…",ShowPanel,true);
            AddMenu("সব কমান্ডের নির্দেশনা খোলো",delegate{Launch(Path.Combine(Environment.SystemDirectory,"notepad.exe"),"\""+Path.Combine(Program.Root,"COMMANDS-BN.txt")+"\"");},true);
            AddMenu("তোমার নোট খোলো",delegate{HandleIntent(new Intent("notes-open","","নোট খোলা হচ্ছে"));},true);
            AddMenu("ঢেউয়ের আইকন সামনে দেখাও",delegate{previewUntil=DateTime.UtcNow.AddSeconds(8);RefreshOverlay(true);Render();},true);
            AddMenu("পিসি বন্ধ / রিস্টার্ট বাতিল",delegate{HandleIntent(new Intent("cancel-power","","পিসি বন্ধ/রিস্টার্ট বাতিল"));},true);
            menu.Items.Add(new ToolStripSeparator());
            AddMenu("ডান কোণায় ফিরিয়ে নাও",delegate{Rectangle a=Screen.PrimaryScreen.WorkingArea;Location=new Point(a.Right-diameter-18,a.Top+54);SavePosition();Render();},true);
            AddMenu("Alfred-এর ফোল্ডার খোলো",delegate{HandleIntent(new Intent("folder","","ফোল্ডার খোলা হচ্ছে"));},true);
            menu.Items.Add(new ToolStripSeparator());AddMenu("পুরোপুরি বন্ধ করো  ·  Exit",Close,true);
            menuOpen=true;hover=true;RefreshOverlay(true);Render();menu.Show(Cursor.Position);
        }
        void AddMenu(string text,Action action,bool enabled){ToolStripMenuItem item=new ToolStripMenuItem(text);item.Enabled=enabled;item.Click+=delegate{action();};menu.Items.Add(item);}
        void ShowPanel(){
            if(panel!=null&&!panel.IsDisposed){panel.Show();panel.Activate();return;}
            panel=new Form{Text="Alfred — বাংলা কমান্ড ও অবস্থা",StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(510,560),BackColor=Color.FromArgb(22,26,34),ForeColor=Color.White,Font=new Font("Nirmala UI",10),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
            Label heading=new Label{Text="Alfred",Font=new Font("Segoe UI",23,FontStyle.Bold),Bounds=new Rectangle(24,14,460,48),ForeColor=Color.White};
            panelState=new Label{Text=message,Bounds=new Rectangle(26,68,454,46),ForeColor=Color.FromArgb(155,223,224)};
            Label instructions=new Label{Text="১. ‘হ্যালো আলফ্রেড’ / ‘হেই আলফ্রেড’ বলো।\n২. নিউক্লিয়াস উজ্জ্বল হলে বাংলা কমান্ড বলো।\n৩. কথার জোরে ঢেউ বাড়বে; বলা শেষ হলে একটু থামো।",Bounds=new Rectangle(26,118,460,88)};
            Label examples=new Label{Text="‘ক্যালকুলেটর খোলো’    ·    ‘নোটপ্যাড খোলো’\n‘ক্রোম খোলো’    ·    ‘ইউটিউবে যাও’\n‘ক্রোমে example.com এ যাও’\n‘গুগলে সার্চ করো বাংলাদেশের আবহাওয়া’\n‘ভলিউম কমাও / বাড়াও’    ·    ‘সাউন্ড মিউট করো’\n‘কয়টা বাজে’    ·    ‘পিসি বন্ধ করো / রিস্টার্ট করো’",Bounds=new Rectangle(26,210,460,155),ForeColor=Color.FromArgb(205,211,225)};
            panelHeard=new Label{Text=(lastHeard.Length==0?"শেষ কমান্ড: —":"শুনেছি: "+lastHeard)+"\n"+lastResult,Bounds=new Rectangle(26,370,460,50),AutoEllipsis=true};
            panelInput=new TextBox{AccessibleName="বাংলা কমান্ড",Bounds=new Rectangle(26,426,340,30),BackColor=Color.FromArgb(38,43,54),ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};
            Button send=new Button{Text="চালাও",Bounds=new Rectangle(376,424,108,32),FlatStyle=FlatStyle.Flat};send.Click+=delegate{if(!busy){lastHeard=panelInput.Text;HandleIntent(Commands.Parse(panelInput.Text));}};panelInput.KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Enter){send.PerformClick();e.SuppressKeyPress=true;}};
            Button listen=new Button{Text="কথা বলো",Bounds=new Rectangle(26,474,140,34),FlatStyle=FlatStyle.Flat};listen.Click+=delegate{BeginListen();};
            panelMic=new Button{Text=paused?"মাইক চালু করো":"মাইক বন্ধ করো",Bounds=new Rectangle(180,474,146,34),FlatStyle=FlatStyle.Flat};panelMic.Click+=delegate{if(paused)Resume();else Pause();};
            Button hide=new Button{Text="উইন্ডো বন্ধ",Bounds=new Rectangle(340,474,144,34),FlatStyle=FlatStyle.Flat};hide.Click+=delegate{panel.Close();};
            Label footer=new Label{Text="সম্পূর্ণ অফলাইন · অডিও / কথার ইতিহাস রাখা হয় না · F:\\Alfred",Bounds=new Rectangle(26,520,460,25),Font=new Font("Nirmala UI",8),ForeColor=Color.FromArgb(145,153,172)};
            panel.Controls.AddRange(new Control[]{heading,panelState,instructions,examples,panelHeard,panelInput,send,listen,panelMic,hide,footer});panel.Show();panel.Activate();
        }
        void Render(){
            if(!IsHandleCreated||closing||overlayHidden)return;
            Graphics g=surfaceGraphics;{
                g.Clear(Color.Transparent);
                const float center=16;
                bool active=phase=="recording"||phase=="processing";
                double t=visualClock.Elapsed.TotalSeconds*(active?1:paused?0.13:0.25);
                float target=phase=="recording"?Math.Min(1,audioLevel/26f):0;
                visualLevel+=(target>visualLevel?0.5f:0.16f)*(target-visualLevel);
                float voice=active?visualLevel:0;
                using(SolidBrush hitArea=new SolidBrush(Color.FromArgb(1,0,0,0)))g.FillEllipse(hitArea,0,0,32,32);
                Color[] palette=WavePalette;
                for(int strand=0;strand<22;strand++){
                    double offset=strand*0.155;
                    PointF[] points=wavePoints;
                    for(int j=0;j<points.Length;j++){
                        double a=j*Math.PI*2/(points.Length-1);
                        double drift=0.35*Math.Sin(t*0.61+offset)+t*0.65;
                        double wave=Math.Sin(a*3+offset+drift)*1.6+Math.Sin(a*2-offset*0.7-t*0.47)*1.2+Math.Sin(a*5+offset+t*0.83)*0.25;
                        float r=10.3f+voice*0.45f+(float)wave*(0.9f+voice*0.45f)+(float)Math.Sin(a*9+t*11-offset)*voice*0.4f;
                        points[j]=new PointF(center+(float)Math.Cos(a)*r,center+(float)Math.Sin(a)*r);
                    }
                    Color color=palette[Math.Min(palette.Length-1,strand*palette.Length/22)];
                    int alpha=paused?65:active?195+(int)(voice*60):hover||menuOpen?230:165;
                    if(active&&strand%5==0)using(Pen glow=new Pen(Color.FromArgb(35,color),0.65f))g.DrawLines(glow,points);
                    using(Pen thread=new Pen(Color.FromArgb(alpha,color),active?0.13f:0.105f))g.DrawLines(thread,points);
                }
                if(active){
                    float beat=(float)(0.5+0.5*Math.Sin(t*5));
                    for(int layer=7;layer>=1;layer--){float radius=0.85f+layer*(0.37f+beat*0.035f+voice*0.15f);using(SolidBrush glow=new SolidBrush(Color.FromArgb(10+layer*2,47,187,255)))g.FillEllipse(glow,center-radius,center-radius,radius*2,radius*2);}
                    using(SolidBrush core=new SolidBrush(Color.FromArgb(255,155,233,255)))g.FillEllipse(core,center-1.15f,center-1.15f,2.3f,2.3f);
                    using(SolidBrush light=new SolidBrush(Color.White))g.FillEllipse(light,center-0.65f,center-0.65f,1.3f,1.3f);
                }else{
                    using(SolidBrush core=new SolidBrush(Color.FromArgb(paused?115:190,110,137,159)))g.FillEllipse(core,center-0.8f,center-0.8f,1.6f,1.6f);
                }
                if(phase=="error")using(Pen warning=new Pen(Color.FromArgb(200,255,160,95),0.2f))g.DrawArc(warning,1,1,30,30,225,90);
                Native.UpdateBitmap(Handle,surface,Left,Top);
            }
        }
        void OnClosing(object sender,FormClosingEventArgs e){
            if(closing)return;SavePosition();closing=true;cancelJob=true;if(job!=null)job.Cancel();timer.Stop();Native.UnregisterHotKey(Handle,1);Native.UnregisterHotKey(Handle,2);DisposeWake();
            try{if(transcriber!=null&&!transcriber.HasExited)transcriber.Kill();}catch{}
            if(panel!=null&&!panel.IsDisposed)panel.Close();tray.Visible=false;Icon trayGlyph=tray.Icon;tray.Dispose();trayGlyph.Dispose();menu.Dispose();tip.Dispose();timer.Dispose();surfaceGraphics.Dispose();surface.Dispose();
            try{File.WriteAllText(Path.Combine(Program.Data,"status.json"),"{\"state\":\"stopped\",\"microphonePaused\":true}",Encoding.UTF8);}catch{}
        }
    }
    static class Native {
        [StructLayout(LayoutKind.Sequential)] struct P {public int x,y;public P(int x,int y){this.x=x;this.y=y;}}
        [StructLayout(LayoutKind.Sequential)] struct S {public int cx,cy;public S(int x,int y){cx=x;cy=y;}}
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend {public byte op,flags,alpha,format;}
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] public struct Rect {public int left,top,right,bottom;}
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect rect);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        public static Icon TrayGlyph(){using(Bitmap b=new Bitmap(32,32,PixelFormat.Format32bppArgb))using(Graphics g=Graphics.FromImage(b)){g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);using(Pen blue=new Pen(Color.FromArgb(54,190,255),2.6f))g.DrawEllipse(blue,3,6,26,20);using(Pen pink=new Pen(Color.FromArgb(236,85,218),1.8f))g.DrawEllipse(pink,7,3,18,26);using(SolidBrush core=new SolidBrush(Color.FromArgb(185,240,255)))g.FillEllipse(core,13,13,6,6);IntPtr h=b.GetHicon();try{return (Icon)Icon.FromHandle(h).Clone();}finally{DestroyIcon(h);}}}
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder name,int count);
        public static bool IsDesktop(IntPtr h){StringBuilder name=new StringBuilder(128);GetClassName(h,name,name.Capacity);string s=name.ToString();return s=="Progman"||s=="WorkerW"||s=="Shell_TrayWnd";}
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h,int id);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dst,ref P pos,ref S size,IntPtr src,ref P from,uint key,ref Blend blend,uint flags);
        public static void UpdateBitmap(IntPtr window,Bitmap image,int x,int y){
            IntPtr screen=GetDC(IntPtr.Zero),mem=CreateCompatibleDC(screen),bitmap=IntPtr.Zero,old=IntPtr.Zero;
            try{bitmap=image.GetHbitmap(Color.FromArgb(0));old=SelectObject(mem,bitmap);P pos=new P(x,y),from=new P(0,0);S size=new S(image.Width,image.Height);Blend b=new Blend{op=0,flags=0,alpha=255,format=1};UpdateLayeredWindow(window,screen,ref pos,ref size,mem,ref from,0,ref b,2);}
            finally{if(old!=IntPtr.Zero)SelectObject(mem,old);if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);DeleteDC(mem);ReleaseDC(IntPtr.Zero,screen);}
        }
    }
}

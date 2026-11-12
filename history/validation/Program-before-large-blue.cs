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
        SpeechRecognitionEngine wake;
        WaveCapture capture;
        Process transcriber;
        volatile bool closing,paused,cancelJob;
        bool busy,hover,down,dragged,menuOpen,silentWake;
        Point dragOrigin,windowOrigin;
        DateTime phaseStart=DateTime.UtcNow, flashUntil=DateTime.MinValue;
        DateTime revealUntil=DateTime.UtcNow.AddSeconds(5);
        string phase="starting", message="চালু হচ্ছে…", lastHeard="",lastResult="",pendingPower="";
        DateTime powerDeadline;
        int audioLevel,diameter=32;
        Form panel; Label panelState,panelHeard; TextBox panelInput; Button panelMic;
        CancellationTokenSource job;
        readonly object engineGate=new object();
        int wakeGeneration;
        public Orb() {
            Text="Alfred";Name="AlfredOrb";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(diameter,diameter);Cursor=Cursors.Hand;
            ReadSettings();
            using(GraphicsPath path=new GraphicsPath()){path.AddEllipse(0,0,diameter,diameter);Region=new Region(path);}
            menu.Font=new Font("Nirmala UI",10);menu.BackColor=Color.FromArgb(28,32,41);menu.ForeColor=Color.White;menu.ShowImageMargin=false;
            menu.Closed+=delegate{menuOpen=false;hover=ClientRectangle.Contains(PointToClient(Cursor.Position));Render();};
            MouseEnter+=delegate{hover=true;Render();};MouseLeave+=delegate{if(!down){hover=false;Render();}};
            MouseDown+=OnDown;MouseMove+=OnMove;MouseUp+=OnUp;
            timer.Interval=250;timer.Tick+=OnTick;
            Shown+=delegate {Native.SetWindowPos(Handle,new IntPtr(-1),Left,Top,Width,Height,0x10|0x40);Render();timer.Start();Native.RegisterHotKey(Handle,1,0x0001|0x0002|0x4000,(uint)Keys.A);StartWake();};
            FormClosing+=OnClosing;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x80000|0x80|0x8000000;return p;}}
        protected override void WndProc(ref Message m){if(m.Msg==0x312 && m.WParam.ToInt32()==1){BeginListen();return;}base.WndProc(ref m);}
        void Ui(Action a){if(closing||IsDisposed)return;try{if(InvokeRequired)BeginInvoke(a);else a();}catch(InvalidOperationException){}}
        void SetState(string state,string text) {
            phase=state;message=text;phaseStart=DateTime.UtcNow;timer.Interval=(state=="recording"||state=="processing")?45:250;
            tip.SetToolTip(this,"Alfred · "+text+"\nবাম ক্লিক: অপশন · টেনে সরাও · Ctrl+Alt+A: কথা বলো");
            if(panelState!=null&&!panelState.IsDisposed)panelState.Text=text;
            if(panelMic!=null&&!panelMic.IsDisposed)panelMic.Text=paused?"মাইক চালু করো":"মাইক বন্ধ করো";
            if(panelHeard!=null&&!panelHeard.IsDisposed)panelHeard.Text=(lastHeard.Length==0?"শেষ কমান্ড: —":"শুনেছি: "+lastHeard)+"\n"+lastResult;
            WriteStatus();Render();
        }
        void WriteStatus(){try{File.WriteAllText(Path.Combine(Program.Data,"status.json"),"{\"state\":"+Program.Json(phase)+",\"message\":"+Program.Json(message)+",\"microphonePaused\":"+(paused?"true":"false")+",\"wakeEngineReady\":"+(wake!=null?"true":"false")+",\"workingSetMB\":"+(Process.GetCurrentProcess().WorkingSet64/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+",\"x\":"+Left+",\"y\":"+Top+",\"size\":"+diameter+",\"version\":\"1.0\"}",Encoding.UTF8);}catch{}}
        void ReadSettings(){
            Rectangle area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-diameter-18,area.Top+54);
            try{string[] v=File.ReadAllLines(Path.Combine(Program.Data,"position.ini"));Location=Clamp(new Point(Int32.Parse(v[0]),Int32.Parse(v[1])));}catch{}
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
            if(capture!=null)audioLevel=capture.Level;
            if(pendingPower.Length>0 && DateTime.UtcNow>=powerDeadline){string action=pendingPower;pendingPower="";LaunchHidden(Path.Combine(Environment.SystemDirectory,"shutdown.exe"),(action=="restart"?"/r":"/s")+" /t 0");}
            if(phase=="recording"||phase=="processing"||DateTime.UtcNow<flashUntil)Render();
            else if(revealUntil!=DateTime.MinValue){Render();if(DateTime.UtcNow>=revealUntil)revealUntil=DateTime.MinValue;}
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
                    GrammarBuilder gb=new GrammarBuilder{Culture=info.Culture};gb.Append(new Choices("Alfred","hey Alfred","hello Alfred"));
                    Grammar grammar=new Grammar(gb);grammar.Name="AlfredWake";engine.LoadGrammar(grammar);
                    engine.SpeechRecognized+=delegate(object s,SpeechRecognizedEventArgs e){if(e.Result.Confidence>=0.52)Ui(delegate{if(generation==wakeGeneration&&wake==engine&&!paused&&!busy)BeginListen();});};
                    engine.RecognizeCompleted+=delegate(object s,RecognizeCompletedEventArgs e){if(e.Error!=null)Ui(delegate{if(generation==wakeGeneration&&!busy&&!paused)SetState("error","মাইক সমস্যা—বাম ক্লিকে আবার চালু করো");});};
                    engine.SetInputToDefaultAudioDevice();
                    lock(engineGate){if(closing||paused||generation!=wakeGeneration){engine.Dispose();return;}wake=engine;engine.RecognizeAsync(RecognizeMode.Multiple);}
                    Ui(delegate{if(generation==wakeGeneration&&!busy&&!paused)SetState("idle","‘আলফ্রেড’ বলো—আমি অপেক্ষায় আছি");});
                }catch(Exception ex){lock(engineGate){if(wake==engine)wake=null;}if(engine!=null)try{engine.Dispose();}catch{}Ui(delegate{if(generation==wakeGeneration&&!paused)SetState("error","মাইক চালু হয়নি: "+ex.Message);});}
            });
        }
        void DisposeWake(){Interlocked.Increment(ref wakeGeneration);lock(engineGate){var old=wake;wake=null;if(old!=null){try{old.RecognizeAsyncCancel();old.SetInputToNull();old.Dispose();}catch{}}}}
        void Pause(){paused=true;cancelJob=true;if(job!=null)job.Cancel();DisposeWake();SetState("paused","মাইক বন্ধ—আলফ্রেড ডাকেও চালু হবে না");}
        void Resume(){if(busy){message="কাজ থামছে—একটু অপেক্ষা করো";return;}paused=false;StartWake();}
        void BeginListen(){
            if(closing||busy)return;
            if(!File.Exists(Path.Combine(Program.Root,"models","ggml-bengali-q5_1.bin"))){SetState("error","বাংলার মডেল পাওয়া যায়নি");return;}
            paused=false;busy=true;cancelJob=false;job=new CancellationTokenSource();lastHeard="";SetState("recording","শুনছি… এবার বাংলা কমান্ড বলো");
            ThreadPool.QueueUserWorkItem(delegate{
                string audio=Path.Combine(Program.Temp,"command.wav"),output=Path.Combine(Program.Temp,"command-transcript");
                try {
                    DisposeWake();if(!silentWake){System.Media.SystemSounds.Asterisk.Play();Thread.Sleep(300);}silentWake=false;
                    using(WaveCapture rec=new WaveCapture()) {
                        capture=rec;rec.Start();
                        bool heard=rec.WaitForEnd(delegate{return cancelJob||closing||job.IsCancellationRequested;},16);
                        if(cancelJob||closing)return;
                        if(!heard){Ui(delegate{Flash("কথা পাইনি—আবার ‘আলফ্রেড’ বলো",false);});return;}
                        rec.Save(audio);capture=null;
                    }
                    Ui(delegate{SetState("processing","বাংলা কমান্ড বুঝছি…");});
                    string text=Transcribe(audio,output,job.Token);
                    Ui(delegate{if(!cancelJob&&!paused&&!closing){lastHeard=text;HandleIntent(Commands.Parse(text));}});
                } catch(OperationCanceledException){}
                catch(Exception ex){Ui(delegate{Flash("সমস্যা: "+ex.Message,false);});}
                finally{
                    capture=null;TryDelete(audio);TryDelete(output+".txt");
                    Ui(delegate{busy=false;audioLevel=0;if(!paused&&!closing){StartWake();}else if(paused)SetState("paused","মাইক বন্ধ");});
                }
            });
        }
        string Transcribe(string audio,string output,CancellationToken token){
            ProcessStartInfo si=new ProcessStartInfo{FileName=Path.Combine(Program.Root,"engine","Release","whisper-cli.exe"),WorkingDirectory=Program.Temp,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            si.Arguments="-m \""+Path.Combine(Program.Root,"models","ggml-bengali-q5_1.bin")+"\" -f \""+audio+"\" -l bn -t 4 -p 1 -nt -np -otxt -of \""+output+"\" -ng -bs 1 -bo 1";
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
                    case "volume-up":Native.SendMessage(Handle,0x319,Handle,new IntPtr(10<<16));Native.SendMessage(Handle,0x319,Handle,new IntPtr(10<<16));break;
                    case "volume-down":Native.SendMessage(Handle,0x319,Handle,new IntPtr(9<<16));Native.SendMessage(Handle,0x319,Handle,new IntPtr(9<<16));break;
                    case "mute":Native.SendMessage(Handle,0x319,Handle,new IntPtr(8<<16));break;
                    case "time":ShowPanel();break;
                    case "help":ShowPanel();break;
                    case "pause":Pause();return;
                    case "quit":Close();return;
                    case "shutdown":case "restart":ConfirmPower(i.Kind);return;
                    case "cancel-power":pendingPower="";break;
                    default:Flash(i.Label,false);ShowPanel();return;
                }
                Flash(i.Label,true);
            }catch(Exception ex){Flash("কাজ হয়নি: "+ex.Message,false);ShowPanel();}
        }
        void ConfirmPower(string kind){
            bool restart=kind=="restart";
            DialogResult result=MessageBox.Show((restart?"পিসি রিস্টার্ট":"পিসি বন্ধ")+" করতে চাও?\n\nহ্যাঁ দিলে ৬০ সেকেন্ড পরে হবে। আগে কাজ সেভ করে নাও।\nAlfred-এর বাম ক্লিক মেনু থেকে বাতিল করতে পারবে।", "Alfred — নিশ্চিত করো",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2,MessageBoxOptions.DefaultDesktopOnly);
            if(result==DialogResult.Yes){pendingPower=kind;powerDeadline=DateTime.UtcNow.AddSeconds(60);Flash("৬০ সেকেন্ডের মধ্যে হবে—মেনু থেকে বাতিল করতে পারো",true);ShowPanel();}else Flash("ঠিক আছে, বাতিল করেছি",true);
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
            AddMenu("এখন কথা বলো  ·  Ctrl+Alt+A",delegate{BeginListen();},!busy);
            AddMenu(paused?"মাইক আবার চালু করো":"মাইক বন্ধ রাখো",delegate{if(paused)Resume();else Pause();},true);
            AddMenu("বাংলা কমান্ড ও অবস্থা…",ShowPanel,true);
            AddMenu("পিসি বন্ধ / রিস্টার্ট বাতিল",delegate{HandleIntent(new Intent("cancel-power","","পিসি বন্ধ/রিস্টার্ট বাতিল"));},true);
            menu.Items.Add(new ToolStripSeparator());
            AddMenu("ডান কোণায় ফিরিয়ে নাও",delegate{Rectangle a=Screen.PrimaryScreen.WorkingArea;Location=new Point(a.Right-diameter-18,a.Top+54);SavePosition();Render();},true);
            AddMenu("Alfred-এর ফোল্ডার খোলো",delegate{HandleIntent(new Intent("folder","","ফোল্ডার খোলা হচ্ছে"));},true);
            menu.Items.Add(new ToolStripSeparator());AddMenu("পুরোপুরি বন্ধ করো  ·  Exit",Close,true);
            menuOpen=true;hover=true;Render();menu.Show(this,new Point(diameter/2,diameter));
        }
        void AddMenu(string text,Action action,bool enabled){ToolStripMenuItem item=new ToolStripMenuItem(text);item.Enabled=enabled;item.Click+=delegate{action();};menu.Items.Add(item);}
        void ShowPanel(){
            if(panel!=null&&!panel.IsDisposed){panel.Show();panel.Activate();return;}
            panel=new Form{Text="Alfred — বাংলা কমান্ড ও অবস্থা",StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(510,560),BackColor=Color.FromArgb(22,26,34),ForeColor=Color.White,Font=new Font("Nirmala UI",10),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
            Label heading=new Label{Text="Alfred",Font=new Font("Segoe UI",23,FontStyle.Bold),Bounds=new Rectangle(24,14,460,48),ForeColor=Color.White};
            panelState=new Label{Text=message,Bounds=new Rectangle(26,68,454,46),ForeColor=Color.FromArgb(155,223,224)};
            Label instructions=new Label{Text="১. ‘আলফ্রেড’ বলে ডাকো।\n২. রিং জ্বলে / শব্দ হলে কমান্ড বলো।\n৩. বলা শেষ হলে একটু থামো—নিজে বুঝে কাজ করবে।",Bounds=new Rectangle(26,118,460,88)};
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
            if(!IsHandleCreated||closing)return;
            using(Bitmap bmp=new Bitmap(diameter,diameter,PixelFormat.Format32bppArgb))using(Graphics g=Graphics.FromImage(bmp)){
                g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
                float center=diameter/2f;bool active=phase=="recording"||phase=="processing";
                bool reveal=DateTime.UtcNow<revealUntil;
                int alpha=hover||menuOpen||reveal?245:paused?45:90;
                using(SolidBrush shadow=new SolidBrush(Color.FromArgb(100,20,25,33)))g.FillEllipse(shadow,center-11,center-11,22,22);
                using(SolidBrush brush=new SolidBrush(Color.FromArgb(alpha,255,255,255)))g.FillEllipse(brush,center-10,center-10,20,20);
                using(Pen outline=new Pen(Color.FromArgb(hover?235:95,255,255,255),1))g.DrawEllipse(outline,center-10,center-10,20,20);
                Color ink=hover||menuOpen||reveal?Color.FromArgb(235,40,47,57):Color.FromArgb(180,255,255,255);
                using(Pen glyph=new Pen(ink,1.45f)){glyph.StartCap=LineCap.Round;glyph.EndCap=LineCap.Round;g.DrawLine(glyph,center-4,center+4,center,center-5);g.DrawLine(glyph,center,center-5,center+4,center+4);g.DrawLine(glyph,center-2.5f,center+1,center+2.5f,center+1);}
                if(active){
                    double t=(DateTime.UtcNow-phaseStart).TotalSeconds;float amp=phase=="recording"?Math.Min(1,audioLevel/45f):(float)(0.5+0.5*Math.Sin(t*4));
                    float radius=12+amp*2;Color ring=Color.FromArgb(235,214,252,255);
                    if(phase=="recording"){
                        PointF[] points=new PointF[97];for(int j=0;j<points.Length;j++){double a=j*Math.PI*2/(points.Length-1);float r=radius+(float)(Math.Sin(a*8+t*6)*amp*0.9);points[j]=new PointF(center+(float)Math.Cos(a)*r,center+(float)Math.Sin(a)*r);}
                        using(Pen p=new Pen(ring,1.6f))g.DrawLines(p,points);
                    }else using(Pen p=new Pen(ring,1.6f)){g.DrawArc(p,center-radius,center-radius,radius*2,radius*2,(float)(t*150)%360,240);}
                }else if(paused){using(Pen p=new Pen(Color.FromArgb(180,255,195,150),1.4f))g.DrawLine(p,center-7,center+7,center+7,center-7);}
                else if(phase=="error"){using(Pen p=new Pen(Color.FromArgb(220,255,170,140),1.3f))g.DrawEllipse(p,2,2,diameter-4,diameter-4);}
                else if(DateTime.UtcNow<flashUntil){using(Pen p=new Pen(Color.FromArgb(210,208,255,220),1.5f))g.DrawEllipse(p,2,2,diameter-4,diameter-4);}
                Native.UpdateBitmap(Handle,bmp,Left,Top);
            }
        }
        void OnClosing(object sender,FormClosingEventArgs e){
            if(closing)return;SavePosition();closing=true;cancelJob=true;if(job!=null)job.Cancel();timer.Stop();Native.UnregisterHotKey(Handle,1);DisposeWake();
            try{if(transcriber!=null&&!transcriber.HasExited)transcriber.Kill();}catch{}
            if(panel!=null&&!panel.IsDisposed)panel.Close();menu.Dispose();tip.Dispose();timer.Dispose();
            try{File.WriteAllText(Path.Combine(Program.Data,"status.json"),"{\"state\":\"stopped\",\"microphonePaused\":true}",Encoding.UTF8);}catch{}
        }
    }
    static class Native {
        [StructLayout(LayoutKind.Sequential)] struct P {public int x,y;public P(int x,int y){this.x=x;this.y=y;}}
        [StructLayout(LayoutKind.Sequential)] struct S {public int cx,cy;public S(int x,int y){cx=x;cy=y;}}
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend {public byte op,flags,alpha,format;}
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
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

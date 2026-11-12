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
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Alfred {
    static class Program {
        public static readonly string Root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Data=Path.Combine(Root,"data"), Temp=Path.Combine(Root,"temp");
        public static readonly int ProcessId=Process.GetCurrentProcess().Id;
        static Mutex mutex;
        static readonly string IpcId=MakeIpcId();
        static string MakeIpcId(){using(var hash=System.Security.Cryptography.SHA256.Create()){byte[] bytes=hash.ComputeHash(Encoding.UTF8.GetBytes(Root.ToLowerInvariant()));return BitConverter.ToString(bytes,0,8).Replace("-","");}}
        public static EventWaitHandle PanelEvent,ListenEvent,QuitEvent,PreviewEvent;
        [STAThread] static void Main(string[] args) {
            Directory.CreateDirectory(Data);Directory.CreateDirectory(Temp);
            Environment.SetEnvironmentVariable("TEMP",Temp);Environment.SetEnvironmentVariable("TMP",Temp);
            Environment.CurrentDirectory=Root;
            if(args.Length>0&&args[0]=="--check-speech-engine"){
                Directory.CreateDirectory(Path.Combine(Root,"build"));
                try{using(SpeechInput input=new SpeechInput(Path.Combine(Root,"engine","sherpa"),Path.Combine(Root,"models")))input.ValidateModels();File.WriteAllText(Path.Combine(Root,"build","native-check.txt"),"PASS sherpa-onnx 1.13.8 native ABI, wake models and neural VAD loaded without opening the microphone.");}
                catch(Exception ex){File.WriteAllText(Path.Combine(Root,"build","native-check.txt"),ex.ToString());Environment.ExitCode=1;}return;
            }
            if(args.Length>0 && args[0]=="--self-test") {
                Directory.CreateDirectory(Path.Combine(Root,"build"));
                try {File.WriteAllText(Path.Combine(Root,"build","tests.txt"),Commands.SelfTest()+SpeechInput.SelfTest(),Encoding.UTF8);Environment.ExitCode=0;}
                catch(Exception ex){File.WriteAllText(Path.Combine(Root,"build","tests.txt"),ex.ToString(),Encoding.UTF8);Environment.ExitCode=1;}return;
            }
            PanelEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Panel."+IpcId);
            ListenEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Listen."+IpcId);
            QuitEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Quit."+IpcId);
            PreviewEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Alfred.Preview."+IpcId);
            bool first;mutex=new Mutex(true,"Local\\Alfred.SmallAssistant."+IpcId,out first);
            if(!first){if(args.Length>0){if(args[0]=="--panel")PanelEvent.Set();if(args[0]=="--listen")ListenEvent.Set();if(args[0]=="--preview")PreviewEvent.Set();if(args[0]=="--quit")QuitEvent.Set();}return;}
            if(args.Length>0&&args[0]=="--quit")return;
            if(args.Length>0&&args[0]=="--panel")PanelEvent.Set();
            if(args.Length>0&&args[0]=="--listen")ListenEvent.Set();
            if(args.Length>0&&args[0]=="--preview")PreviewEvent.Set();
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
        readonly OrbVisual orbVisual=new OrbVisual();
        SpeechInput speechInput; SpeechSession speechSession;
        readonly SpeechTranscriber speech=new SpeechTranscriber(Program.Root,Program.Temp);
        int speechStarting; long wakeEndSample=-1;
        volatile bool closing,paused;
        volatile bool inputBusy,screenshotBusy;
        readonly DraftSession draft=new DraftSession();
        IntPtr commandWindow;
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
        int wakeRecognized,wakeAccepted;
        int speechBusy;
        bool dictating;
        string dictationLanguage="",speechLanguage="auto";
        bool retryCommand;
        int commandRetries;
        int lastCapturePeak,lastCaptureDuration,lastCaptureVoiceFrames;
        bool lastCaptureHeard;
        volatile string wakeLastCandidate="",wakeLastDecision="none",commandStage="none",lastCommandKind="";
        DateTime nextMetricsWrite=DateTime.MinValue;
        float visualLevel;
        Form panel; Label panelState,panelHeard; TextBox panelInput; Button panelMic;
        CancellationTokenSource job;
        readonly object engineGate=new object();
        int wakeGeneration;
        public Orb() {
            Text="Alfred";Name="AlfredOrb";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=false;StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(diameter,diameter);Cursor=Cursors.Hand;
            surfaceGraphics=Graphics.FromImage(surface);surfaceGraphics.SmoothingMode=SmoothingMode.AntiAlias;surfaceGraphics.ScaleTransform(diameter/32f,diameter/32f);
            ReadSettings();
            using(GraphicsPath path=new GraphicsPath()){path.AddEllipse(0,0,diameter,diameter);Region=new Region(path);}
            menu.Font=new Font("Nirmala UI",10);menu.BackColor=Color.FromArgb(28,32,41);menu.ForeColor=Color.White;menu.ShowImageMargin=false;
            tray.Icon=Native.TrayGlyph();tray.Text="Alfred — বাম ক্লিকে মাইক ও অপশন";tray.Visible=true;tray.MouseClick+=delegate{ShowOptions();};
            menu.Closed+=delegate{menuOpen=false;hover=ClientRectangle.Contains(PointToClient(Cursor.Position));Render();};
            MouseEnter+=delegate{hover=true;Render();};MouseLeave+=delegate{if(!down){hover=false;Render();}};
            MouseDown+=OnDown;MouseMove+=OnMove;MouseUp+=OnUp;
            timer.Interval=66;timer.Tick+=OnTick;
            Shown+=delegate {Render();timer.Start();Native.RegisterHotKey(Handle,1,0x0001|0x0002|0x4000,(uint)Keys.A);Native.RegisterHotKey(Handle,2,0x0001|0x0002|0x4000,(uint)Keys.M);if(paused)SetState("paused","মাইক বন্ধ—মেনু থেকে হাতে চালু করো");else StartWake();};
            FormClosing+=OnClosing;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x80000|0x80|0x8000000;return p;}}
        protected override void WndProc(ref Message m){if(m.Msg==0x312){if(m.WParam.ToInt32()==1){if(dictating)StopDictation(true);else BeginListen();return;}if(m.WParam.ToInt32()==2){if(paused)Resume();else Pause();return;}}base.WndProc(ref m);}
        void Ui(Action a){if(closing||IsDisposed)return;try{if(InvokeRequired)BeginInvoke(a);else a();}catch(InvalidOperationException){}}
        void SetState(string state,string text) {
            phase=state;message=text;phaseStart=DateTime.UtcNow;timer.Interval=(state=="activating"||state=="recording"||state=="processing")?40:66;
            tip.SetToolTip(this,"Alfred · "+text+"\nবাম ক্লিক: অপশন · টেনে সরাও · Ctrl+Alt+A: কথা বলো");
            if(panelState!=null&&!panelState.IsDisposed)panelState.Text=text;
            if(panelMic!=null&&!panelMic.IsDisposed)panelMic.Text=paused?"মাইক চালু করো":"মাইক বন্ধ করো";
            if(panelHeard!=null&&!panelHeard.IsDisposed)panelHeard.Text=(lastHeard.Length==0?"শেষ কমান্ড: —":"শুনেছি: "+lastHeard)+"\n"+lastResult;
            tray.Text=paused?"Alfred — মাইক বন্ধ (ক্লিক করে চালু করো)":state=="recording"?"Alfred — বাংলা / English শুনছি":"Alfred — Hello / Hey / Okay Alfred";
            RefreshOverlay(true);WriteStatus();Render();
        }
        public static bool WakePhraseStartsAtUtterance(int offsetMs){return offsetMs!=-1&&offsetMs>=-300&&offsetMs<=1200;}
        void WriteStatus(){try{using(Process current=Process.GetCurrentProcess()){
            SpeechInput input=speechInput;
            string json="{\"version\":\"2.0\",\"state\":"+Program.Json(phase)+",\"message\":"+Program.Json(message)+",\"microphonePaused\":"+(paused?"true":"false")+",\"wakeEngineReady\":"+(input!=null&&input.Error.Length==0?"true":"false")+",\"wakeBackend\":\"sherpa-onnx Zipformer int8\",\"voiceDetector\":\"Silero VAD\",\"microphoneLevel\":"+(input==null?0:input.Level)+",\"microphonePeak\":"+(input==null?0:input.PeakInput)+",\"voiceFrames\":"+(input==null?0:input.VoiceFrameCount)+",\"wakeCandidates\":"+(input==null?0:input.WakeCandidates)+",\"wakeRejected\":"+(input==null?0:input.WakeRejected)+",\"wakeDetectorDecision\":"+Program.Json(input==null?"":input.LastDecision)+",\"wakeDetectorCandidate\":"+Program.Json(input==null?"":input.LastCandidate)+",\"audioSamples\":"+(input==null?0:input.CurrentSample)+",\"speechError\":"+Program.Json(input==null?"":input.Error)+",\"wakeAccepted\":"+wakeAccepted+",\"wakeLastCandidate\":"+Program.Json(wakeLastCandidate)+",\"wakeLastDecision\":"+Program.Json(wakeLastDecision)+",\"commandStage\":"+Program.Json(commandStage)+",\"dictationActive\":"+(dictating?"true":"false")+",\"commandHeard\":"+(lastCaptureHeard?"true":"false")+",\"commandPeakLevel\":"+lastCapturePeak+",\"commandDurationMs\":"+lastCaptureDuration+",\"commandVoiceFrames\":"+lastCaptureVoiceFrames+",\"lastCommandKind\":"+Program.Json(lastCommandKind)+",\"speechBackend\":"+Program.Json(speech.Backend)+",\"speechLanguage\":"+Program.Json(speechLanguage)+",\"scriptRetry\":"+(speech.ScriptRetry?"true":"false")+",\"decodedLanguage\":"+Program.Json(speech.Language)+",\"transcribeMs\":"+speech.ElapsedMs+",\"speechFallback\":"+(speech.FallbackUsed?"true":"false")+",\"transcribePeakWorkingMB\":"+(speech.PeakWorkingSetBytes/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+",\"transcribePeakPrivateMB\":"+(speech.PeakPrivateBytes/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+",\"overlayVisible\":"+(!overlayHidden?"true":"false")+",\"timers\":"+timers.Count+",\"workingSetMB\":"+(current.WorkingSet64/1048576.0).ToString("F1",CultureInfo.InvariantCulture)+",\"x\":"+Left+",\"y\":"+Top+",\"size\":"+diameter+"}";
            File.WriteAllText(Path.Combine(Program.Data,"status.json"),json,Encoding.UTF8);
        }}catch{}}
        void ReadSettings(){
            Rectangle area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-diameter-18,area.Top+54);
            try{string[] v=File.ReadAllLines(Path.Combine(Program.Data,"position.ini"));Location=Clamp(new Point(Int32.Parse(v[0]),Int32.Parse(v[1])));}catch{}
            try{string saved=File.ReadAllText(Path.Combine(Program.Data,"speech-language.ini")).Trim();if(saved=="bn"||saved=="en")speechLanguage=saved;}catch{}
            try{paused=File.ReadAllText(Path.Combine(Program.Data,"microphone-paused.ini")).Trim()=="true";}catch{}
            try{foreach(string line in File.ReadAllLines(Path.Combine(Program.Data,"timers.txt"))){long ticks;if(Int64.TryParse(line,out ticks)&&ticks>=DateTime.MinValue.Ticks&&ticks<=DateTime.MaxValue.Ticks&&timers.Count<8)timers.Add(new DateTime(ticks,DateTimeKind.Utc));}}catch{}
        }
        void SaveTimers(){List<string> lines=new List<string>();foreach(DateTime due in timers)lines.Add(due.Ticks.ToString(CultureInfo.InvariantCulture));File.WriteAllLines(Path.Combine(Program.Data,"timers.txt"),lines.ToArray());}
        void SaveMicrophone(){File.WriteAllText(Path.Combine(Program.Data,"microphone-paused.ini"),paused?"true":"false",Encoding.UTF8);}
        void RefreshOverlay(bool force){
            if(!IsHandleCreated||closing||(!force&&DateTime.UtcNow<overlayCheck))return;
            overlayCheck=DateTime.UtcNow.AddMilliseconds(350);
            bool active=phase=="activating"||phase=="recording"||phase=="processing"||phase=="speaking";
            bool prominent=active||menuOpen||down||DateTime.UtcNow<previewUntil;
            IntPtr foreground=Native.GetForegroundWindow();uint owner=0;Native.GetWindowThreadProcessId(foreground,out owner);
            bool external=foreground!=IntPtr.Zero&&owner!=(uint)Program.ProcessId&&!Native.IsDesktop(foreground);
            if(external)lastExternalWindow=foreground;
            Native.Rect rect;bool covered=external&&Native.GetWindowRect(foreground,out rect)&&new Rectangle(rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top).IntersectsWith(Bounds);
            bool hide=screenshotBusy||(!prominent&&covered);
            bool changed=hide!=overlayHidden;
            if(changed){Native.ShowWindow(Handle,hide?0:4);overlayHidden=hide;}
            if(!hide&&(force||prominent!=overlayProminent))Native.SetWindowPos(Handle,new IntPtr(prominent?-1:-2),Left,Top,Width,Height,0x10|0x40);
            if(!hide&&!prominent&&external)Native.SetWindowPos(Handle,foreground,Left,Top,Width,Height,0x10);
            overlayProminent=prominent;timer.Interval=hide?400:active?40:66;
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
            if(Program.PreviewEvent.WaitOne(0)){previewUntil=DateTime.UtcNow.AddSeconds(8);RefreshOverlay(true);Render();}
            bool timerDone=false;for(int n=timers.Count-1;n>=0;n--)if(DateTime.UtcNow>=timers[n]){timers.RemoveAt(n);timerDone=true;}
            if(timerDone){SaveTimers();Helpers.Notice("টাইমার শেষ হয়েছে।");WriteStatus();}
            audioLevel=speechInput!=null?speechInput.Level:0;
            if(DateTime.UtcNow>=nextMetricsWrite){nextMetricsWrite=DateTime.UtcNow.AddMilliseconds(800);WriteStatus();if(panelState!=null&&!panelState.IsDisposed&&speechInput!=null)panelState.Text=message+"\nমাইকের জোর: "+speechInput.Level+" / 100 · voice frames: "+speechInput.VoiceFrameCount;}
            if(pendingPower.Length>0 && DateTime.UtcNow>=powerDeadline){string action=pendingPower;pendingPower="";LaunchHidden(Path.Combine(Environment.SystemDirectory,"shutdown.exe"),(action=="restart"?"/r":"/s")+" /t 0");}
            RefreshOverlay(false);if(!overlayHidden)Render();
        }
        void StartWake(){
            if(closing||paused||busy||inputBusy||Interlocked.CompareExchange(ref speechBusy,0,0)!=0)return;
            if(speechInput!=null&&speechInput.Error.Length>0)CloseSpeechInput();
            if(speechInput!=null){speechInput.Enabled=true;SetState("idle","Hello / Hey / Okay Alfred বলো · Ctrl+Alt+A দিয়েও কথা বলা যায়");return;}
            if(Interlocked.CompareExchange(ref speechStarting,1,0)!=0)return;
            int generation=Interlocked.Increment(ref wakeGeneration);SetState("starting","নতুন wake ও voice detector চালু হচ্ছে…");
            ThreadPool.QueueUserWorkItem(delegate{
                SpeechInput created=null;
                try{
                    created=new SpeechInput(Path.Combine(Program.Root,"engine","sherpa"),Path.Combine(Program.Root,"models"));
                    created.WakeDetected+=delegate(WakeHit hit){
                        Interlocked.Increment(ref wakeRecognized);Interlocked.Increment(ref wakeAccepted);wakeLastCandidate=hit.Phrase;wakeLastDecision="keyword detected";
                        Ui(delegate{if(!closing&&!paused&&!busy&&!inputBusy&&Interlocked.CompareExchange(ref speechBusy,0,0)==0){wakeEndSample=Math.Max(0,hit.StartSample-1600);commandStage="wake accepted";BeginListen();}});
                    };
                    SpeechInput observed=created;created.Failed+=delegate(string reason){Ui(delegate{if(speechInput==observed&&!paused&&!closing){if(job!=null)job.Cancel();speech.Cancel();dictating=false;CloseSpeechInput();SetState("error",reason+" · মাইক বন্ধ/চালু করে আবার চেষ্টা করো");}});};
                    created.Start();lock(engineGate){if(closing||paused||generation!=wakeGeneration){created.Dispose();return;}speechInput=created;}
                    Ui(delegate{if(!closing&&!paused&&generation==wakeGeneration)SetState("idle","Hello / Hey / Okay Alfred বলো · Ctrl+Alt+A দিয়েও কথা বলা যায়");});
                }catch(Exception ex){if(created!=null)created.Dispose();Ui(delegate{if(!closing&&!paused)SetState("error","Voice engine: "+ex.Message);});}
                finally{Interlocked.Exchange(ref speechStarting,0);}
            });
        }
        void DisposeWake(){if(speechInput!=null)speechInput.Enabled=false;}
        void EndSpeechSession(){SpeechSession old=speechSession;speechSession=null;if(old!=null)old.Dispose();}
        void CloseSpeechInput(){Interlocked.Increment(ref wakeGeneration);EndSpeechSession();SpeechInput old;lock(engineGate){old=speechInput;speechInput=null;}if(old!=null)old.Dispose();}
        void Pause(){paused=true;StopDictation(false);SaveMicrophone();if(job!=null)job.Cancel();speech.Cancel();CloseSpeechInput();SetState("paused","মাইক বন্ধ—শুধু মেনু থেকে হাতে চালু হবে");}
        void Resume(){if(busy){message="কাজ থামছে—একটু অপেক্ষা করো";return;}paused=false;SaveMicrophone();StartWake();}
        void ContinueListening(){
            if(closing||paused||busy||inputBusy||screenshotBusy||Interlocked.CompareExchange(ref speechBusy,0,0)!=0)return;
            if(dictating){BeginListen(true);return;}
            if(retryCommand&&commandRetries<2){
                retryCommand=false;
                commandRetries++;
                BeginListen(false,true);
                return;
            }
            retryCommand=false;
            commandRetries=0;
            StartWake();
        }
        void StopDictation(bool announce){
            if(!dictating)return;
            dictating=false;
            EndSpeechSession();
            if(!inputBusy)draft.EndDictation();
            if(busy&&job!=null){job.Cancel();}
            if(announce){
                Flash("চ্যাটের খসড়া থেমেছে। দেখে এডিট করো; পাঠাতে আলাদা করে বলো।",true);
                SpeakResponse(new Intent("dictation-stop","",""),true);
                ContinueListening();
            }
        }
        void BeginListen(bool dictationSegment=false,bool continueCurrentCommand=false){
            if(closing||busy||inputBusy||screenshotBusy||Interlocked.CompareExchange(ref speechBusy,0,0)!=0)return;
            if(paused){Flash("মাইক বন্ধ আছে—ট্রে আইকনের মেনু থেকে হাতে চালু করো",false);return;}
            if(speechInput==null){StartWake();Flash("Voice engine প্রস্তুত হচ্ছে—একটু পরে Ctrl+Alt+A চাপো",false);return;}
            if(!dictationSegment&&!continueCurrentCommand){commandRetries=0;retryCommand=false;EndSpeechSession();commandWindow=Native.GetForegroundWindow();uint pid;Native.GetWindowThreadProcessId(commandWindow,out pid);if(pid==(uint)Program.ProcessId)commandWindow=lastExternalWindow;}
            DisposeWake();busy=true;job=new CancellationTokenSource();lastHeard="";CancellationToken token=job.Token;string language=dictationSegment&&!String.IsNullOrEmpty(dictationLanguage)?dictationLanguage:speechLanguage;
            commandStage="recording";lastCapturePeak=lastCaptureDuration=lastCaptureVoiceFrames=0;lastCaptureHeard=false;lastCommandKind="";
            SetState("recording",dictationSegment?"কথা বলো—খসড়ায় লিখছি · সংশোধন বা ‘এবার পাঠাও’ বলা যাবে":continueCurrentCommand?"আবার শুনছি—কমান্ডটা বলো":"শুনছি—বাংলা বা English কমান্ড বলো");
            ThreadPool.QueueUserWorkItem(delegate{
                string id=Guid.NewGuid().ToString("N"),audio=Path.Combine(Program.Temp,"speech-"+id+".wav"),output=Path.Combine(Program.Temp,"speech-"+id);
                try{
                    SpeechSession current=speechSession;if(current==null){current=wakeEndSample>=0?speechInput.BeginSession(wakeEndSample):speechInput.BeginSession();wakeEndSample=-1;speechSession=current;}
                    SpeechSegment rec=current.Next(token,dictationSegment?10:15,dictationSegment?0.85:0.75,dictationSegment?90:8);
                    if(rec==null){Ui(delegate{if(token.IsCancellationRequested||paused)return;if(dictationSegment)StopDictation(true);else{retryCommand=commandRetries<1;Flash(retryCommand?"কথা ধরা পড়েনি—মাইক খোলা আছে, আবার বলো":"কথা আসেনি—আবার ডাকলে শুনব",false);}});return;}
                    lastCapturePeak=rec.PeakLevel;lastCaptureDuration=rec.DurationMs;lastCaptureVoiceFrames=rec.VoiceFrames;lastCaptureHeard=rec.Heard;if(token.IsCancellationRequested||closing)return;
                    rec.Save(audio);commandStage="transcribing";Ui(delegate{SetState("processing",dictationSegment?"লেখা তুলছি—বলা চালিয়ে যেতে পারো":"কমান্ড বুঝছি…");});
                    string text=speech.Transcribe(audio,output,token,language,dictationSegment);if(!dictationSegment)text=StripWakePrefix(text);
                    Ui(delegate{
                        if(token.IsCancellationRequested||paused||closing)return;lastHeard=text;Intent parsed=dictationSegment?VoiceActions.ParseDictation(text):Commands.Parse(text);
                        if(parsed==null){if(dictationSegment)Flash("কথা পরিষ্কার আসেনি—শুনছি, আবার বলো",false);else retryCommand=commandRetries<2;return;}
                        if(!dictationSegment&&parsed.Kind=="unknown"){retryCommand=commandRetries<2;string excerpt=text.Trim();if(excerpt.Length>100)excerpt=excerpt.Substring(0,100)+"…";parsed.Label=excerpt.Length>0?"শুনেছি “"+excerpt+"”। আবার বলো—শুনছি।":"কথা পরিষ্কার আসেনি—আবার বলো।";Flash(parsed.Label,false);}
                        else{retryCommand=false;commandRetries=0;HandleIntent(parsed);}lastCommandKind=parsed.Kind;commandStage="intent parsed";
                    });
                }catch(OperationCanceledException){}
                catch(Exception ex){Ui(delegate{if(token.IsCancellationRequested||paused)return;if(dictationSegment)StopDictation(false);commandStage="error";Flash("Voice: "+ex.Message,false);});}
                finally{TryDelete(audio);TryDelete(output+".txt");Ui(delegate{busy=false;audioLevel=0;if(job!=null){job.Dispose();job=null;}if(!dictating&&!retryCommand)EndSpeechSession();ContinueListening();});}
            });
        }
        public static string StripWakePrefix(string text){return System.Text.RegularExpressions.Regex.Replace(text??"",@"^(?:(?:hello|hey|okay|ok|হ্যালো|হেলো|হেই|ওকে)\s+)?(?:alfred|আলফ্রেড|অ্যালফ্রেড)[,।.!?\s]*(?:(?:bhai|vai|ভাই)[,।.!?\s]+)?","",System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();}
        static void TryDelete(string f){try{if(File.Exists(f))File.Delete(f);}catch{}}
        void DraftAction(Intent i){
            if(inputBusy||screenshotBusy){Flash("আগের কাজ শেষ হলে আবার বলো",false);return;}
            if(paused){Flash("লেখার ভয়েস নিয়ন্ত্রণের আগে হাতে মাইক চালু করো",false);return;}
            IntPtr target=busy?commandWindow:Native.GetForegroundWindow();
            inputBusy=true;SetState("processing","খসড়ার কাজ করছি…");
            ThreadPool.QueueUserWorkItem(delegate{
                string result=i.Label;bool good=true;
                try{
                    if(paused||closing)throw new OperationCanceledException();
                    switch(i.Kind){
                        case "dictation-start":draft.BeginDictation(FocusedTextTarget.Capture(target,delegate{return paused||closing;}));break;
                        case "draft-dictate":draft.Dictate(i.Value);break;
                        case "draft-remove":draft.RemoveWords(i.Value);break;
                        case "draft-new-line":draft.NewLine();break;
                        case "draft-undo":draft.Undo();break;
                        case "chat-select":ChatContactSelector.Select(target,i.Value,delegate{return paused||closing;});break;
                        case "chat-write":ChatContactSelector.Select(target,i.Contact,delegate{return paused||closing;});draft.Start(FocusedTextTarget.Capture(target,delegate{return paused||closing;},true),i.Value,false);break;
                        case "draft-write":draft.Start(FocusedTextTarget.Capture(target,delegate{return paused||closing;},i.ChatTarget),i.Value,false);break;
                        case "draft-adopt":draft.Start(FocusedTextTarget.Capture(target,delegate{return paused||closing;}),"",true);break;
                        case "draft-rewrite":draft.Rewrite(i.Value);break;
                        case "draft-append":draft.Append(i.Value);break;
                        case "draft-replace":int split=i.Value.IndexOf('\n');draft.ReplaceWords(i.Value.Substring(0,split),i.Value.Substring(split+1));break;
                        case "draft-delete-word":draft.DeleteWord();break;
                        case "draft-delete-sentence":draft.DeleteSentence();break;
                        case "draft-clear":draft.Clear();break;
                        case "draft-send":draft.Send();break;
                    }
                }catch(OperationCanceledException){good=false;result="লেখার কাজ থেমেছে";}
                catch(Exception ex){good=false;result=ex is System.Windows.Automation.ElementNotAvailableException?"লেখার বক্স আর পাওয়া যাচ্ছে না—আবার বেছে নাও":ex.Message;}
                Ui(delegate{
                    inputBusy=false;
                    if(closing||paused)return;
                    if(good&&i.Kind=="dictation-start"){
                        EndSpeechSession();dictating=true;
                        dictationLanguage=i.Value;
                        if(speechInput!=null)speechSession=speechInput.BeginSession();
                        result="চ্যাটের খসড়া লেখা শুরু। স্বাভাবিকভাবে বলো—বক্সে উঠবে, পাঠানো হবে না। থামাতে বলো ‘চ্যাটের খসড়া থামাও’।";
                    }
                    if(!good&&dictating)StopDictation(false);
                    Flash(result,good);
                    if(!dictating&&i.Kind!="dictation-start")SpeakResponse(i,good);
                    ContinueListening();
                });
            });
        }
        void Snapshot(string part){
            if(inputBusy||screenshotBusy){Flash("আগের কাজ শেষ হলে আবার বলো",false);return;}
            screenshotBusy=true;RefreshOverlay(true);
            if(part=="select"){
                new RegionPicker(delegate(Rectangle region){if(region.IsEmpty){screenshotBusy=false;RefreshOverlay(true);if(!busy&&!paused)StartWake();}else SaveSnapshot(region);}).Show();return;
            }
            Rectangle area=part=="all"?SystemInformation.VirtualScreen:Screen.FromHandle(lastExternalWindow).Bounds;
            if(part=="window"){
                Native.Rect r;if(lastExternalWindow==IntPtr.Zero||!Native.GetWindowRect(lastExternalWindow,out r)){screenshotBusy=false;Flash("সামনের উইন্ডো পাওয়া যায়নি",false);return;}
                area=new Rectangle(r.left,r.top,r.right-r.left,r.bottom-r.top);
            }else if(part!="all")area=ScreenGrab.Region(part,area);
            SaveSnapshot(area);
        }
        void SaveSnapshot(Rectangle area){
            ThreadPool.QueueUserWorkItem(delegate{
                string result="স্ক্রিনশট সেভ হয়েছে। দেখতে বলো: শেষ স্ক্রিনশট খোলো";bool good=true;
                try{Thread.Sleep(100);ScreenGrab.Capture(area);}catch(Exception ex){good=false;result="স্ক্রিনশট হয়নি: "+ex.Message;}
                Ui(delegate{screenshotBusy=false;Flash(result,good);SpeakResponse(new Intent("screenshot","",result),good);Helpers.Notice(result);if(!busy&&!paused&&!inputBusy)StartWake();});
            });
        }
        void HandleIntent(Intent i){
            if(i.Kind=="dictation-send"){dictating=false;EndSpeechSession();draft.EndDictation();DraftAction(new Intent("draft-send","","পাঠানোর ফল যাচাই করছি…"));return;}
            if(i.Kind=="dictation-stop"){StopDictation(true);return;}
            if(i.Kind=="dictation-edit-unknown"){Flash("এডিট কমান্ডটি বুঝিনি। আবার বলো।",false);return;}
            if(i.Kind=="dictation-start"||i.Kind=="chat-select"||i.Kind=="chat-write"||i.Kind.StartsWith("draft-",StringComparison.Ordinal)){DraftAction(i);return;}
            try{
                switch(i.Kind){
                    case "input-help":Helpers.Notice(i.Label);break;
                    case "screenshot":Snapshot(i.Value);return;
                    case "screenshots-open":Directory.CreateDirectory(ScreenGrab.Folder);Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+ScreenGrab.Folder+"\"");break;
                    case "screenshot-latest":string shot=ScreenGrab.Latest();if(shot==null)throw new Exception("এখনও স্ক্রিনশট সেভ হয়নি");Launch(shot,null);break;
                    case "calculate":i.Label="ফল: "+SmallCalculation.Evaluate(i.Value).ToString("0.##########",CultureInfo.CurrentCulture);Helpers.Notice(i.Label);break;
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
                    case "pause":SpeakResponse(i,true);Pause();return;
                    case "quit":Close();return;
                    case "shutdown":case "restart":ConfirmPower(i.Kind);return;
                    case "cancel-power":pendingPower="";break;
                    default:Flash(i.Label,false);SpeakResponse(i,false);return;
                }
                Flash(i.Label,true);SpeakResponse(i,true);
            }catch(Exception ex){Flash("কাজ হয়নি: "+ex.Message,false);SpeakResponse(i,false);}
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
        static string ButlerLine(Intent i,bool good){
            if(i==null)return null;
            if(!good){
                if(i.Kind=="unknown")return "I didn't understand that command, sir.";
                if(i.Kind=="no-speech")return "I didn't hear a command, sir.";
                return "I couldn't complete that, sir.";
            }
            switch(i.Kind){
                case "time":return "The time is "+DateTime.Now.ToString("h:mm tt",CultureInfo.GetCultureInfo("en-US"))+".";
                case "date":return "Today is "+DateTime.Now.ToString("dddd, MMMM d",CultureInfo.GetCultureInfo("en-US"))+".";
                case "calculator":return "Opening Calculator, sir.";
                case "notepad":return "Opening Notepad, sir.";
                case "browser":case "website":return "Opening that page, sir.";
                case "app":return "Opening "+i.Value+", sir.";
                case "note-save":case "clipboard-note":return "Note saved, sir.";
                case "timer":return "Timer set, sir.";
                case "timer-cancel":return "All timers cancelled, sir.";
                case "timer-remaining":case "memory-info":case "disk-info":return "I've shown the result, sir.";
                case "screenshot":return "Screenshot saved, sir.";
                case "dictation-start":return "Chat draft ready. Speak naturally; I will write, not send.";
                case "dictation-stop":return "Chat draft stopped. Review it before sending.";
                case "voice-preview":return "At your service, sir.";
                case "draft-send":return "Message sent, sir.";
                case "draft-write":case "draft-edit":return "The draft is ready for your review, sir.";
                case "draft-clear":return "Draft cleared, sir.";
                case "shutdown":case "restart":case "quit":return null;
                default:return "Done, sir.";
            }
        }
        void ConfigureButlerVoice(SpeechSynthesizer speaker){
            bool chosen=false;
            try{foreach(InstalledVoice voice in speaker.GetInstalledVoices())if(voice.Enabled&&String.Equals(voice.VoiceInfo.Name,"Microsoft David Desktop",StringComparison.OrdinalIgnoreCase)){speaker.SelectVoice(voice.VoiceInfo.Name);chosen=true;break;}}catch{}
            if(!chosen)try{speaker.SelectVoiceByHints(VoiceGender.Male,VoiceAge.Adult,0,CultureInfo.GetCultureInfo("en-US"));}catch{}
            speaker.Rate=-2;speaker.Volume=100;
        }
        void SpeakResponse(Intent intent,bool good){
            string line=ButlerLine(intent,good);if(String.IsNullOrWhiteSpace(line)||closing)return;
            if(Interlocked.CompareExchange(ref speechBusy,1,0)!=0)return;
            DisposeWake();
            SetState("speaking",message);
            ThreadPool.QueueUserWorkItem(delegate{
                try{using(SpeechSynthesizer speaker=new SpeechSynthesizer()){ConfigureButlerVoice(speaker);speaker.Speak(line);}}
                catch{}
                finally{
                    Thread.Sleep(500);
                    Ui(delegate{Interlocked.Exchange(ref speechBusy,0);ContinueListening();});
                }
            });
        }
        void Flash(string text,bool good){lastResult=text;flashUntil=DateTime.UtcNow.AddSeconds(2);SetState(good?"success":"notice",text);}
        void SetSpeechLanguage(string language){speechLanguage=language;File.WriteAllText(Path.Combine(Program.Data,"speech-language.ini"),language,Encoding.UTF8);Flash(language=="bn"?"বাংলা + English শব্দ: বাংলা মোড":language=="en"?"English speech mode":"বাংলা / English স্বয়ংক্রিয় মোড",true);}
        void ShowOptions(){
            menu.Items.Clear();menu.Items.Add(new ToolStripMenuItem("Alfred · "+(paused?"মাইক বন্ধ":busy?"কাজ করছি":"ডাকের অপেক্ষায়")){Enabled=false});menu.Items.Add(new ToolStripSeparator());
            AddMenu("এখন কথা বলো  ·  Ctrl+Alt+A",delegate{BeginListen();},!busy&&!paused);
            AddMenu("Alfred-এর কণ্ঠ শুনো",delegate{SpeakResponse(new Intent("voice-preview","",""),true);},!busy&&!inputBusy&&!screenshotBusy);
            AddMenu((paused?"মাইক আবার চালু করো":"মাইক বন্ধ রাখো")+"  ·  Ctrl+Alt+M",delegate{if(paused)Resume();else Pause();},true);
            AddMenu("ভাষা: বাংলা / English নিজে শনাক্ত করো"+(speechLanguage=="auto"?" ✓":""),delegate{SetSpeechLanguage("auto");},!busy&&!dictating);
            AddMenu("ভাষা: বাংলা প্রাধান্য"+(speechLanguage=="bn"?" ✓":""),delegate{SetSpeechLanguage("bn");},!busy&&!dictating);
            AddMenu("ভাষা: English"+(speechLanguage=="en"?" ✓":""),delegate{SetSpeechLanguage("en");},!busy&&!dictating);
            AddMenu("বাংলা / English কমান্ড ও অবস্থা…",ShowPanel,true);
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
            panel=new Form{Text="Alfred — বাংলা / English কমান্ড ও অবস্থা",StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(510,560),BackColor=Color.FromArgb(22,26,34),ForeColor=Color.White,Font=new Font("Nirmala UI",10),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
            Label heading=new Label{Text="Alfred",Font=new Font("Segoe UI",23,FontStyle.Bold),Bounds=new Rectangle(24,14,460,48),ForeColor=Color.White};
            panelState=new Label{Text=message,Bounds=new Rectangle(26,68,454,46),ForeColor=Color.FromArgb(155,223,224)};
            Label instructions=new Label{Text="১. ‘হ্যালো আলফ্রেড’ / ‘হেই আলফ্রেড’ / ‘ওকে আলফ্রেড’ বলো।\n২. নিউক্লিয়াস উজ্জ্বল হলে বাংলা বা English কমান্ড বলো।\n৩. কথার জোরে ঢেউ বাড়বে; বলা শেষ হলে একটু থামো।",Bounds=new Rectangle(26,118,460,88)};
            Label examples=new Label{Text="‘ক্যালকুলেটর খোলো’    ·    ‘নোটপ্যাড খোলো’\n‘ক্রোম খোলো’    ·    ‘open Google Chrome’\n‘open chat with Ayesha’ · ‘চ্যাট খোলো রিমা’\n‘চ্যাটে লেখা শুরু করো’ → কথা খসড়ায় উঠবে\n‘শেষ শব্দটা মুছো’ → লেখাটা সংশোধন করো\n‘এবার পাঠাও’ বললেই কেবল মেসেজ পাঠায়\n‘ভলিউম কমাও’    ·    ‘কয়টা বাজে’",Bounds=new Rectangle(26,210,460,155),ForeColor=Color.FromArgb(205,211,225)};
            panelHeard=new Label{Text=(lastHeard.Length==0?"শেষ কমান্ড: —":"শুনেছি: "+lastHeard)+"\n"+lastResult,Bounds=new Rectangle(26,370,460,50),AutoEllipsis=true};
            panelInput=new TextBox{AccessibleName="বাংলা / English কমান্ড",Bounds=new Rectangle(26,426,340,30),BackColor=Color.FromArgb(38,43,54),ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};
            Button send=new Button{Text="চালাও",Bounds=new Rectangle(376,424,108,32),FlatStyle=FlatStyle.Flat};send.Click+=delegate{if(!busy){lastHeard=panelInput.Text;HandleIntent(Commands.Parse(panelInput.Text));}};panelInput.KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Enter){send.PerformClick();e.SuppressKeyPress=true;}};
            Button listen=new Button{Text="কথা বলো",Bounds=new Rectangle(26,474,140,34),FlatStyle=FlatStyle.Flat};listen.Click+=delegate{BeginListen();};
            panelMic=new Button{Text=paused?"মাইক চালু করো":"মাইক বন্ধ করো",Bounds=new Rectangle(180,474,146,34),FlatStyle=FlatStyle.Flat};panelMic.Click+=delegate{if(paused)Resume();else Pause();};
            Button hide=new Button{Text="উইন্ডো বন্ধ",Bounds=new Rectangle(340,474,144,34),FlatStyle=FlatStyle.Flat};hide.Click+=delegate{panel.Close();};
            Label footer=new Label{Text="Local wake + neural VAD + GPU speech · F:\\Alfred",Bounds=new Rectangle(26,520,460,25),Font=new Font("Nirmala UI",8),ForeColor=Color.FromArgb(145,153,172)};
            panel.Controls.AddRange(new Control[]{heading,panelState,instructions,examples,panelHeard,panelInput,send,listen,panelMic,hide,footer});panel.Show();panel.Activate();
        }
        void Render(){
            if(!IsHandleCreated||closing||overlayHidden)return;
            float target=(phase=="recording"||dictating)?Math.Min(1,audioLevel/22f):phase=="activating"?0.18f:0;
            visualLevel+=(target>visualLevel?0.50f:0.16f)*(target-visualLevel);
            orbVisual.Draw(surfaceGraphics,visualClock.Elapsed.TotalSeconds,phase,visualLevel,paused,hover||menuOpen);
            Native.UpdateBitmap(Handle,surface,Left,Top);
        }
        void OnClosing(object sender,FormClosingEventArgs e){
            if(closing)return;SavePosition();closing=true;if(job!=null)job.Cancel();timer.Stop();Native.UnregisterHotKey(Handle,1);Native.UnregisterHotKey(Handle,2);CloseSpeechInput();speech.Dispose();
            if(panel!=null&&!panel.IsDisposed)panel.Close();tray.Visible=false;Icon trayGlyph=tray.Icon;tray.Dispose();trayGlyph.Dispose();menu.Dispose();tip.Dispose();timer.Dispose();orbVisual.Dispose();surfaceGraphics.Dispose();surface.Dispose();Program.PanelEvent.Dispose();Program.ListenEvent.Dispose();Program.QuitEvent.Dispose();Program.PreviewEvent.Dispose();
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



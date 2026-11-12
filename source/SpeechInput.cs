using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Alfred {
    // One microphone, one inference worker, bounded PCM queues. Native callbacks only copy audio.
    public sealed class SpeechInput : IDisposable {
        internal const int Rate=16000, FrameSize=512;
        const int InputQueueFrames=125, RingFrames=313; // 4 s callback queue, 10 s handoff ring
        readonly string nativeDir,modelDir;
        readonly object gate=new object(),callbackGate=new object();
        readonly Queue<AudioFrame> input=new Queue<AudioFrame>(),ring=new Queue<AudioFrame>();
        readonly AutoResetEvent available=new AutoResetEvent(false);
        readonly List<IntPtr> headers=new List<IntPtr>(),buffers=new List<IntPtr>();
        readonly float[] normalized=new float[FrameSize];
        Thread worker; IntPtr handle,spotter,keywordStream,vad;
        WaveCallback callback;
        volatile bool stopping,started,enabled=true;
        volatile int level,peakInput;int enableVersion,voiceFrameCount;
        volatile string error="",lastCandidate="",lastDecision="none";int wakeCandidates,wakeRejected;bool resetDuringSilence;
        int appliedEnableVersion=-1;
        long sampleCount,keywordBase,speechStart=-1,lastSpeechEnd=-1;
        bool wasSpeech;
        SpeechSession session;
        public event Action<WakeHit> WakeDetected;
        public event Action<string> Failed;
        public int Level {get{return level;}}
        public int PeakInput {get{return peakInput;}}
        public int VoiceFrameCount {get{return voiceFrameCount;}}
        public string Error {get{return error;}}
        public int WakeCandidates {get{return Interlocked.CompareExchange(ref wakeCandidates,0,0);}}
        public int WakeRejected {get{return Interlocked.CompareExchange(ref wakeRejected,0,0);}}
        public string LastCandidate {get{return lastCandidate;}}
        public string LastDecision {get{return lastDecision;}}
        public bool Enabled {get{return enabled;}set{if(enabled!=value){enabled=value;Interlocked.Increment(ref enableVersion);}}}
        public long CurrentSample {get{lock(gate)return sampleCount;}}
        public SpeechInput(string nativeDirectory,string modelsDirectory){nativeDir=Path.GetFullPath(nativeDirectory);modelDir=Path.GetFullPath(modelsDirectory);}
        public void ValidateModels(){if(started||stopping)throw new InvalidOperationException("Use a fresh input for validation.");try{CreateNative();}finally{DestroyNative();}}
        public void Start(){
            if(started)throw new InvalidOperationException("Microphone input is already started.");
            if(stopping)throw new ObjectDisposedException("SpeechInput");
            try{
                CreateNative();OpenMicrophone();started=true;
                worker=new Thread(Run){IsBackground=true,Name="Alfred speech input",Priority=ThreadPriority.BelowNormal};worker.Start();
            }catch{stopping=true;CloseMicrophone();DestroyNative();throw;}
        }
        public SpeechSession BeginSession(){long sample;lock(gate)sample=Math.Max(0,sampleCount-Rate/4);return BeginSession(sample);}
        public SpeechSession BeginSession(long startSample){
            if(!started||stopping)throw new InvalidOperationException("Microphone input is not running.");
            lock(gate){
                if(error.Length>0)throw new IOException(error);
                if(session!=null&&!session.IsDisposed)throw new InvalidOperationException("A speech session is already active.");
                if(ring.Count>0&&startSample<ring.Peek().Start)throw new IOException("Wake audio expired before command capture started. Please call Alfred again.");
                SpeechSession next=new SpeechSession(this);
                foreach(AudioFrame frame in ring)if(frame.End>startSample)next.Push(frame);
                session=next;return next;
            }
        }
        internal void EndSession(SpeechSession ended){lock(gate){if(session==ended)session=null;}}
        void Fail(string message){
            if(error.Length>0)return;error=message;Enabled=false;
            lock(gate){if(session!=null)session.Fail(message);}
            Action<string> failed=Failed;if(failed!=null)ThreadPool.QueueUserWorkItem(delegate{try{failed(message);}catch{}});
            available.Set();
        }
        void Run(){
            try{
                while(!stopping&&error.Length==0){
                    AudioFrame frame=null;lock(gate){if(input.Count>0)frame=input.Dequeue();}
                    if(frame==null){available.WaitOne(100);continue;}
                    Requeue(frame.Header);ProcessFrame(frame);
                }
            }catch(Exception ex){if(!stopping)Fail("Speech input failed: "+ex.Message);}
            finally{DestroyNative();}
        }
        void ProcessFrame(AudioFrame frame){
            for(int i=0;i<frame.Samples.Length;i++)normalized[i]=frame.Samples[i]/32768f;
            Sherpa.VadAccept(vad,normalized,frame.Samples.Length);
            frame.Speech=Sherpa.VadDetected(vad)!=0;if(frame.Speech)Interlocked.Increment(ref voiceFrameCount);
            // Never retain native VAD segment buffers; the managed bounded queues own PCM.
            while(Sherpa.VadEmpty(vad)==0)Sherpa.VadPop(vad);
            if(frame.Speech){if(!wasSpeech)speechStart=Math.Max(0,frame.Start-Rate/5);lastSpeechEnd=frame.End;resetDuringSilence=false;}
            wasSpeech=frame.Speech;
            lock(gate){
                ring.Enqueue(frame);while(ring.Count>RingFrames)ring.Dequeue();
                if(session!=null&&!session.IsDisposed)session.Push(frame);
            }
            int generation=enableVersion;
            if(!enabled)return;
            if(!frame.Speech&&!resetDuringSilence&&lastSpeechEnd>=0&&frame.End-lastSpeechEnd>=Rate/2){appliedEnableVersion=-1;resetDuringSilence=true;}
            if(appliedEnableVersion!=generation){
                if(keywordStream!=IntPtr.Zero)Sherpa.DestroyStream(keywordStream);
                keywordStream=Sherpa.CreateKeywordStream(spotter);
                if(keywordStream==IntPtr.Zero)throw new IOException("Could not create keyword stream.");
                appliedEnableVersion=generation;keywordBase=frame.Start;
            }
            Sherpa.Accept(keywordStream,Rate,normalized,frame.Samples.Length);
            while(Sherpa.KeywordReady(spotter,keywordStream)!=0){
                Sherpa.DecodeKeyword(spotter,keywordStream);
                IntPtr pointer=Sherpa.KeywordResult(spotter,keywordStream);
                try{
                    if(pointer==IntPtr.Zero)continue;
                    Sherpa.Keyword result=(Sherpa.Keyword)Marshal.PtrToStructure(pointer,typeof(Sherpa.Keyword));
                    string phrase=Marshal.PtrToStringAnsi(result.keyword)??"";
                    if(phrase.Length==0)continue;
                    Interlocked.Increment(ref wakeCandidates);lastCandidate=phrase.Replace('_',' ');
                    if(result.count<=0||result.count>64||result.timestamps==IntPtr.Zero||result.start_time<0){lastDecision="missing keyword timing";Interlocked.Increment(ref wakeRejected);Sherpa.ResetKeyword(spotter,keywordStream);continue;}
                    float[] times=new float[result.count];Marshal.Copy(result.timestamps,times,0,times.Length);
                    long first=keywordBase+(long)((result.start_time+times[0])*Rate);
                    long end=keywordBase+(long)((result.start_time+times[times.Length-1]+0.10f)*Rate);
                    // Greeting must begin near the beginning of a VAD utterance, never just a name inside a sentence.
                    bool known=AllowedWake(phrase);
                    bool atStart=speechStart>=0&&first>=speechStart-Rate/4&&first-speechStart<=Rate;
                    bool recent=lastSpeechEnd>=0&&frame.End-lastSpeechEnd<Rate;
                    Sherpa.ResetKeyword(spotter,keywordStream);
                    if(!known||!atStart||!recent){lastDecision=!known?"unapproved wake phrase":!atStart?"phrase inside longer speech":"keyword outside recent speech";Interlocked.Increment(ref wakeRejected);continue;}
                    lastDecision="accepted greeted wake at utterance start";
                    Enabled=false;
                    WakeHit hit=new WakeHit{Phrase=phrase.Replace('_',' '),StartSample=first,EndSample=Math.Min(frame.End,Math.Max(first,end))};
                    Action<WakeHit> found=WakeDetected;if(found!=null)found(hit);
                    break;
                }finally{if(pointer!=IntPtr.Zero)Sherpa.DestroyKeywordResult(pointer);}
            }
        }
        static bool AllowedWake(string phrase){string value=(phrase??"").Replace('_',' ').Trim().ToLowerInvariant();return value=="hello alfred"||value=="hey alfred"||value=="okay alfred"||value=="ok alfred";}
        void CreateNative(){
            Sherpa.Load(nativeDir);
            string wake=Path.Combine(modelDir,"wake");
            Sherpa.KwsConfig config=new Sherpa.KwsConfig();
            config.feature.sample_rate=Rate;config.feature.feature_dim=80;
            config.model.transducer.encoder=Find(wake,"encoder-*.int8.onnx");
            config.model.transducer.decoder=Find(wake,"decoder-*.int8.onnx");
            config.model.transducer.joiner=Find(wake,"joiner-*.int8.onnx");
            config.model.tokens=Path.Combine(wake,"tokens.txt");config.model.num_threads=1;config.model.provider="cpu";
            config.max_active_paths=4;config.num_trailing_blanks=1;config.keywords_score=2.5f;config.keywords_threshold=0.25f;
            config.keywords_file=Path.Combine(wake,"keywords.txt");
            if(!File.Exists(config.keywords_file))throw new FileNotFoundException("Alfred keyword definitions are missing.",config.keywords_file);
            spotter=Sherpa.CreateKeywordSpotter(ref config);if(spotter==IntPtr.Zero)throw new IOException("Keyword model could not be loaded.");
            Sherpa.VadConfig vc=new Sherpa.VadConfig();
            vc.silero.model=Path.Combine(modelDir,"silero_vad.onnx");vc.silero.threshold=0.5f;vc.silero.min_silence_duration=0.25f;vc.silero.min_speech_duration=0.10f;vc.silero.window_size=FrameSize;vc.silero.max_speech_duration=30;
            vc.sample_rate=Rate;vc.num_threads=1;vc.provider="cpu";
            vad=Sherpa.CreateVad(ref vc,40);if(vad==IntPtr.Zero)throw new IOException("Speech activity model could not be loaded.");
        }
        static string Find(string directory,string pattern){string[] files=Directory.GetFiles(directory,pattern);if(files.Length!=1)throw new FileNotFoundException("Expected one "+pattern+" model in "+directory);return files[0];}
        void DestroyNative(){if(keywordStream!=IntPtr.Zero){Sherpa.DestroyStream(keywordStream);keywordStream=IntPtr.Zero;}if(spotter!=IntPtr.Zero){Sherpa.DestroySpotter(spotter);spotter=IntPtr.Zero;}if(vad!=IntPtr.Zero){Sherpa.DestroyVad(vad);vad=IntPtr.Zero;}}
        // File-based probe uses the same VAD/KWS processing as the microphone, without opening a device.
        public WakeHit[] ProbeFile(string path){
            if(started||stopping)throw new InvalidOperationException("Use a fresh SpeechInput for a file probe.");
            List<WakeHit> hits=new List<WakeHit>();Action<WakeHit> found=delegate(WakeHit hit){hits.Add(hit);};WakeDetected+=found;
            try{
                CreateNative();short[] samples=ReadWave(path);
                for(int offset=0;offset<samples.Length+Rate;offset+=FrameSize){short[] part=new short[FrameSize];int n=Math.Min(FrameSize,Math.Max(0,samples.Length-offset));if(n>0)Array.Copy(samples,offset,part,0,n);ProcessFrame(new AudioFrame(offset,part));}
                return hits.ToArray();
            }finally{WakeDetected-=found;DestroyNative();}
        }
        static short[] ReadWave(string path){
            using(BinaryReader reader=new BinaryReader(File.OpenRead(path))){
                if(new string(reader.ReadChars(4))!="RIFF")throw new IOException("Expected a WAV file.");reader.ReadInt32();if(new string(reader.ReadChars(4))!="WAVE")throw new IOException("Expected WAVE audio.");
                bool format=false;int sourceRate=0;short[] result=null;
                while(reader.BaseStream.Position+8<=reader.BaseStream.Length){string id=new string(reader.ReadChars(4));int size=reader.ReadInt32();if(size<0||reader.BaseStream.Position+size>reader.BaseStream.Length)throw new IOException("Invalid WAV chunk.");long next=reader.BaseStream.Position+size+(size&1);
                    if(id=="fmt "){if(size<16||reader.ReadInt16()!=1||reader.ReadInt16()!=1)throw new IOException("Probe requires mono PCM WAV.");sourceRate=reader.ReadInt32();if(sourceRate<8000||sourceRate>96000)throw new IOException("Unsupported WAV sample rate.");reader.ReadInt32();reader.ReadInt16();if(reader.ReadInt16()!=16)throw new IOException("Probe requires 16-bit samples.");format=true;}
                    else if(id=="data"){byte[] bytes=reader.ReadBytes(size);result=new short[bytes.Length/2];Buffer.BlockCopy(bytes,0,result,0,result.Length*2);}
                    reader.BaseStream.Position=next;
                }
                if(!format||result==null)throw new IOException("WAV data or format is missing.");if(sourceRate==Rate)return result;short[] converted=new short[(int)((long)result.Length*Rate/sourceRate)];for(int i=0;i<converted.Length;i++){double position=(double)i*sourceRate/Rate;int a=(int)position,b=Math.Min(result.Length-1,a+1);converted[i]=(short)(result[a]+(result[b]-result[a])*(position-a));}return converted;
            }
        }
        void OpenMicrophone(){
            WaveFormat format=new WaveFormat{tag=1,channels=1,rate=Rate,average=Rate*2,align=2,bits=16};callback=OnAudio;
            uint code=waveInOpen(out handle,0xffffffff,ref format,callback,UIntPtr.Zero,0x30000);if(code!=0)throw new IOException("Microphone open failed ("+code+").");
            uint size=(uint)Marshal.SizeOf(typeof(WaveHeader));
            for(int i=0;i<32;i++){IntPtr buffer=Marshal.AllocHGlobal(FrameSize*2),header=Marshal.AllocHGlobal((int)size);buffers.Add(buffer);headers.Add(header);WaveHeader h=new WaveHeader{data=buffer,length=FrameSize*2};Marshal.StructureToPtr(h,header,false);if(waveInPrepareHeader(handle,header,size)!=0||waveInAddBuffer(handle,header,size)!=0)throw new IOException("Microphone buffer setup failed.");}
            if(waveInStart(handle)!=0)throw new IOException("Microphone start failed.");
        }
        void OnAudio(IntPtr device,uint message,UIntPtr instance,IntPtr pointer,IntPtr unused){
            if(message!=0x3c0||stopping||error.Length>0)return;
            lock(callbackGate){
                if(stopping||error.Length>0)return;
                try{
                    WaveHeader h=(WaveHeader)Marshal.PtrToStructure(pointer,typeof(WaveHeader));int count=Math.Min(FrameSize,(int)h.recorded/2);
                    if(count>0){short[] samples=new short[count];Marshal.Copy(h.data,samples,0,count);double sum=0;for(int i=0;i<count;i++)sum+=(double)samples[i]*samples[i];int peak=(int)Math.Min(100,Math.Sqrt(sum/count)/32768*700);level=peak;if(peak>peakInput)peakInput=peak;
                        lock(gate){if(input.Count>=InputQueueFrames){Fail("The audio worker fell behind. Pause and resume the microphone.");return;}input.Enqueue(new AudioFrame(sampleCount,samples){Header=pointer});sampleCount+=count;}available.Set();}

                }catch(Exception ex){if(!stopping)Fail("Microphone capture failed: "+ex.Message);}
            }
        }
        void Requeue(IntPtr pointer){
            if(pointer==IntPtr.Zero)return;
            lock(callbackGate){if(!stopping&&handle!=IntPtr.Zero&&waveInAddBuffer(handle,pointer,(uint)Marshal.SizeOf(typeof(WaveHeader)))!=0)Fail("Microphone buffer could not be queued again.");}
        }
        void CloseMicrophone(){
            IntPtr device=handle;if(device==IntPtr.Zero)return;
            waveInStop(device);waveInReset(device);
            // Reset returns the queued buffers; the callback lock ensures no copy still accesses them.
            lock(callbackGate){uint size=(uint)Marshal.SizeOf(typeof(WaveHeader));for(int i=0;i<headers.Count;i++){waveInUnprepareHeader(device,headers[i],size);Marshal.FreeHGlobal(headers[i]);Marshal.FreeHGlobal(buffers[i]);}headers.Clear();buffers.Clear();waveInClose(device);handle=IntPtr.Zero;}
        }
        public void Dispose(){
            if(stopping)return;stopping=true;Enabled=false;CloseMicrophone();available.Set();
            Thread running=worker;if(running!=null&&running!=Thread.CurrentThread)running.Join();else if(running==null)DestroyNative();
            lock(gate){if(session!=null)session.Fail("Microphone stopped.");input.Clear();ring.Clear();}available.Dispose();GC.KeepAlive(callback);
        }
        public static string SelfTest(){return SpeechSession.SelfTest();}
        [StructLayout(LayoutKind.Sequential,Pack=2)]struct WaveFormat{public ushort tag,channels;public uint rate,average;public ushort align,bits,extra;}
        [StructLayout(LayoutKind.Sequential)]struct WaveHeader{public IntPtr data;public uint length,recorded;public UIntPtr user;public uint flags,loops;public IntPtr next;public UIntPtr reserved;}
        delegate void WaveCallback(IntPtr handle,uint message,UIntPtr instance,IntPtr p1,IntPtr p2);
        [DllImport("winmm.dll")]static extern uint waveInOpen(out IntPtr handle,uint id,ref WaveFormat format,WaveCallback callback,UIntPtr instance,uint flags);
        [DllImport("winmm.dll")]static extern uint waveInPrepareHeader(IntPtr handle,IntPtr header,uint size);
        [DllImport("winmm.dll")]static extern uint waveInUnprepareHeader(IntPtr handle,IntPtr header,uint size);
        [DllImport("winmm.dll")]static extern uint waveInAddBuffer(IntPtr handle,IntPtr header,uint size);
        [DllImport("winmm.dll")]static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")]static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")]static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")]static extern uint waveInClose(IntPtr handle);
        internal sealed class AudioFrame{public readonly long Start;public readonly short[] Samples;public bool Speech;public IntPtr Header;public long End{get{return Start+Samples.Length;}}public AudioFrame(long start,short[] samples){Start=start;Samples=samples;}}
        static class Sherpa{
            const string Dll="sherpa-onnx-c-api.dll";
            static readonly object loadGate=new object();static bool loaded;
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
            internal static void Load(string directory){lock(loadGate){if(loaded)return;foreach(string file in new[]{"onnxruntime.dll",Dll}){string path=Path.Combine(directory,file);if(LoadLibraryEx(path,IntPtr.Zero,0x100|0x1000)==IntPtr.Zero)throw new IOException("Cannot load "+file+" ("+Marshal.GetLastWin32Error()+").");}string version=Marshal.PtrToStringAnsi(Version());if(version==null||!version.StartsWith("1.13.8",StringComparison.Ordinal))throw new IOException("Expected sherpa-onnx 1.13.8; found "+version);loaded=true;}}
            [StructLayout(LayoutKind.Sequential)]internal struct Transducer{[MarshalAs(UnmanagedType.LPStr)]public string encoder,decoder,joiner;}
            [StructLayout(LayoutKind.Sequential)]internal struct Paraformer{[MarshalAs(UnmanagedType.LPStr)]public string encoder,decoder;}
            [StructLayout(LayoutKind.Sequential)]internal struct Model{public Transducer transducer;public Paraformer paraformer;public IntPtr zipformer;[MarshalAs(UnmanagedType.LPStr)]public string tokens;public int num_threads;[MarshalAs(UnmanagedType.LPStr)]public string provider;public int debug;public IntPtr model_type,modeling_unit,bpe_vocab,tokens_buf;public int tokens_buf_size;public IntPtr nemo,tone;}
            [StructLayout(LayoutKind.Sequential)]internal struct Feature{public int sample_rate,feature_dim;}
            [StructLayout(LayoutKind.Sequential)]internal struct KwsConfig{public Feature feature;public Model model;public int max_active_paths,num_trailing_blanks;public float keywords_score,keywords_threshold;[MarshalAs(UnmanagedType.LPStr)]public string keywords_file;public IntPtr keywords_buf;public int keywords_buf_size;}
            [StructLayout(LayoutKind.Sequential)]internal struct Keyword{public IntPtr keyword,tokens,tokens_arr;public int count;public IntPtr timestamps;public float start_time;public IntPtr json;}
            [StructLayout(LayoutKind.Sequential)]internal struct VadModel{[MarshalAs(UnmanagedType.LPStr)]public string model;public float threshold,min_silence_duration,min_speech_duration;public int window_size;public float max_speech_duration;}
            [StructLayout(LayoutKind.Sequential)]internal struct VadConfig{public VadModel silero;public int sample_rate,num_threads;[MarshalAs(UnmanagedType.LPStr)]public string provider;public int debug;public VadModel ten;}
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxGetVersionStr")]static extern IntPtr Version();
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxCreateKeywordSpotter")]internal static extern IntPtr CreateKeywordSpotter(ref KwsConfig config);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxDestroyKeywordSpotter")]internal static extern void DestroySpotter(IntPtr pointer);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxCreateKeywordStream")]internal static extern IntPtr CreateKeywordStream(IntPtr spotter);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxDestroyOnlineStream")]internal static extern void DestroyStream(IntPtr stream);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxOnlineStreamAcceptWaveform")]internal static extern void Accept(IntPtr stream,int rate,[In]float[] samples,int n);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxIsKeywordStreamReady")]internal static extern int KeywordReady(IntPtr spotter,IntPtr stream);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxDecodeKeywordStream")]internal static extern void DecodeKeyword(IntPtr spotter,IntPtr stream);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxResetKeywordStream")]internal static extern void ResetKeyword(IntPtr spotter,IntPtr stream);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxGetKeywordResult")]internal static extern IntPtr KeywordResult(IntPtr spotter,IntPtr stream);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxDestroyKeywordResult")]internal static extern void DestroyKeywordResult(IntPtr result);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxCreateVoiceActivityDetector")]internal static extern IntPtr CreateVad(ref VadConfig config,float bufferSeconds);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxDestroyVoiceActivityDetector")]internal static extern void DestroyVad(IntPtr vad);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxVoiceActivityDetectorAcceptWaveform")]internal static extern void VadAccept(IntPtr vad,[In]float[] samples,int n);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxVoiceActivityDetectorDetected")]internal static extern int VadDetected(IntPtr vad);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxVoiceActivityDetectorEmpty")]internal static extern int VadEmpty(IntPtr vad);
            [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,EntryPoint="SherpaOnnxVoiceActivityDetectorPop")]internal static extern void VadPop(IntPtr vad);
        }
    }
    public sealed class WakeHit{public string Phrase;public long StartSample,EndSample;}
    public sealed class SpeechSegment{
        internal readonly short[] samples;public long StartSample{get;internal set;}public int PeakLevel{get;internal set;}public int VoiceFrames{get;internal set;}public bool Heard{get{return VoiceFrames>0;}}public int DurationMs{get{return samples.Length*1000/SpeechInput.Rate;}}
        internal SpeechSegment(short[] pcm){samples=pcm;}
        public void Save(string path){using(BinaryWriter writer=new BinaryWriter(File.Create(path))){writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));writer.Write(samples.Length*2+36);writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(SpeechInput.Rate);writer.Write(SpeechInput.Rate*2);writer.Write((short)2);writer.Write((short)16);writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));writer.Write(samples.Length*2);foreach(short sample in samples)writer.Write(sample);}}
    }
    public sealed class SpeechSession:IDisposable{
        const int MaxQueuedSamples=SpeechInput.Rate*90;
        readonly SpeechInput owner;readonly object gate=new object();readonly Queue<SpeechInput.AudioFrame> pending=new Queue<SpeechInput.AudioFrame>();readonly AutoResetEvent changed=new AutoResetEvent(false);
        int queuedSamples,readers;bool disposed,eventDisposed;string failure="";
        internal bool IsDisposed{get{lock(gate)return disposed;}}
        internal SpeechSession(SpeechInput input){owner=input;}
        internal void Push(SpeechInput.AudioFrame frame){lock(gate){if(disposed||failure.Length>0)return;if(queuedSamples+frame.Samples.Length>MaxQueuedSamples){failure="Speech backlog exceeded 90 seconds. Dictation stopped; review the existing draft before continuing.";pending.Clear();queuedSamples=0;changed.Set();return;}pending.Enqueue(frame);queuedSamples+=frame.Samples.Length;changed.Set();}}
        internal void Fail(string message){lock(gate){if(disposed)return;failure=message;changed.Set();}}
        // Use only to suppress Alfred's own spoken feedback; normal inference keeps collecting audio.
        public void DiscardPending(){lock(gate){pending.Clear();queuedSamples=0;}}
        public SpeechSegment Next(CancellationToken cancel,int maxSeconds,double trailingSilenceSeconds,double initialSilenceSeconds){
            if(maxSeconds<1||maxSeconds>60||trailingSilenceSeconds<0.1||trailingSilenceSeconds>5||initialSilenceSeconds<0.1||initialSilenceSeconds>120)throw new ArgumentOutOfRangeException("Speech segment timing is outside supported bounds.");
            lock(gate){if(disposed)throw new ObjectDisposedException("SpeechSession");if(readers!=0)throw new InvalidOperationException("Only one reader may consume a speech session.");readers++;}
            try{
            List<SpeechInput.AudioFrame> frames=new List<SpeechInput.AudioFrame>();long start=-1,firstVoice=-1,lastVoice=-1;int voiceFrames=0;var watch=Stopwatch.StartNew();long lastArrival=Stopwatch.GetTimestamp();
            while(true){
                cancel.ThrowIfCancellationRequested();SpeechInput.AudioFrame frame=null;
                lock(gate){if(disposed)throw new ObjectDisposedException("SpeechSession");if(failure.Length>0)throw new IOException(failure);if(pending.Count>0){frame=pending.Dequeue();queuedSamples-=frame.Samples.Length;}}
                if(frame==null){if(Stopwatch.GetTimestamp()-lastArrival>Stopwatch.Frequency*3)throw new IOException("The microphone stopped delivering audio. Pause and resume it, or select a working input device.");if(firstVoice<0&&watch.Elapsed.TotalSeconds>=initialSilenceSeconds)return null;changed.WaitOne(50);continue;}
                lastArrival=Stopwatch.GetTimestamp();
                if(start<0)start=frame.Start;frames.Add(frame);
                if(frame.Speech){if(firstVoice<0)firstVoice=frame.Start;lastVoice=frame.End;voiceFrames++;}
                if(firstVoice<0){while(frames.Count>10)frames.RemoveAt(0);if(frame.End-start>=initialSilenceSeconds*SpeechInput.Rate)return null;}
                else if(frame.End-firstVoice>=maxSeconds*SpeechInput.Rate||frame.End-lastVoice>=trailingSilenceSeconds*SpeechInput.Rate)return BuildSegment(frames,firstVoice,lastVoice,voiceFrames);
            }
            }finally{lock(gate){readers--;if(disposed&&readers==0&&!eventDisposed){eventDisposed=true;changed.Dispose();}}}
        }
        static SpeechSegment BuildSegment(List<SpeechInput.AudioFrame> frames,long first,long last,int voiceFrames){
            long from=Math.Max(frames[0].Start,first-SpeechInput.Rate/5),to=Math.Min(frames[frames.Count-1].End,last+SpeechInput.Rate/5);short[] pcm=new short[(int)(to-from)];double peak=0;
            foreach(SpeechInput.AudioFrame frame in frames){int offset=(int)Math.Max(0,from-frame.Start),count=(int)(Math.Min(to,frame.End)-Math.Max(from,frame.Start));if(count<=0)continue;Array.Copy(frame.Samples,offset,pcm,(int)(Math.Max(from,frame.Start)-from),count);double sum=0;for(int i=offset;i<offset+count;i++)sum+=(double)frame.Samples[i]*frame.Samples[i];peak=Math.Max(peak,Math.Sqrt(sum/count)/32768*700);}
            return new SpeechSegment(pcm){StartSample=from,PeakLevel=(int)Math.Min(100,peak),VoiceFrames=voiceFrames};
        }
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;pending.Clear();queuedSamples=0;changed.Set();if(readers==0&&!eventDisposed){eventDisposed=true;changed.Dispose();}}if(owner!=null)owner.EndSession(this);}
        internal static string SelfTest(){
            using(SpeechSession s=new SpeechSession(null)){for(int i=0;i<70;i++){short[] pcm=new short[SpeechInput.FrameSize];bool speech=i>=5&&i<25;for(int n=0;n<pcm.Length;n++)pcm[n]=(short)(speech?10000:0);s.Push(new SpeechInput.AudioFrame(i*SpeechInput.FrameSize,pcm){Speech=speech});}SpeechSegment part=s.Next(CancellationToken.None,5,0.3,2);if(part==null||!part.Heard||part.VoiceFrames!=20||part.PeakLevel<90)throw new Exception("Neural session segmentation check failed.");SpeechSegment silent=s.Next(CancellationToken.None,5,0.1,0.5);if(silent!=null)throw new Exception("Silence was treated as speech.");}
            using(SpeechSession s=new SpeechSession(null)){for(int i=0;i<3000;i++)s.Push(new SpeechInput.AudioFrame(i*SpeechInput.FrameSize,new short[SpeechInput.FrameSize]));bool rejected=false;try{s.Next(CancellationToken.None,2,0.2,0.3);}catch(IOException){rejected=true;}if(!rejected)throw new Exception("Speech backlog overflow was not reported.");}
            using(SpeechSession s=new SpeechSession(null)){using(CancellationTokenSource c=new CancellationTokenSource()){c.Cancel();bool cancelled=false;try{s.Next(c.Token,2,0.2,0.3);}catch(OperationCanceledException){cancelled=true;}if(!cancelled)throw new Exception("Speech cancellation failed.");}}
            return "PASS continuous speech queue, VAD segmentation, silence, overflow, and cancellation"+Environment.NewLine;
        }
    }
}
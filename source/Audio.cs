using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Alfred {
    public sealed class WaveCapture : IDisposable {
        // Lower gate catches quieter speech; two frames still reject a single transient click.
        const double VoiceRmsThreshold=0.006;
        const int BytesPerSecond=32000;
        [StructLayout(LayoutKind.Sequential,Pack=2)] struct Format { public ushort tag,channels; public uint rate,average; public ushort align,bits,extra; }
        [StructLayout(LayoutKind.Sequential)] struct Header { public IntPtr data; public uint length,recorded; public UIntPtr user; public uint flags,loops; public IntPtr next; public UIntPtr reserved; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Caps { public ushort mid,pid; public uint version; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string name; public uint formats; public ushort channels,reserved; }
        delegate void Callback(IntPtr handle,uint message,UIntPtr instance,IntPtr p1,IntPtr p2);
        [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr h,uint id,ref Format f,Callback cb,UIntPtr instance,uint flags);
        [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr h,IntPtr p,uint size);
        [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr h,IntPtr p,uint size);
        [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr h,IntPtr p,uint size);
        [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern uint waveInStop(IntPtr h);
        [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll",CharSet=CharSet.Unicode,EntryPoint="waveInGetDevCapsW")] static extern uint waveInGetDevCaps(UIntPtr id,out Caps caps,uint size);
        public static string[] Devices() { List<string> result=new List<string>(); for(uint i=0;i<waveInGetNumDevs();i++){Caps c;if(waveInGetDevCaps(new UIntPtr(i),out c,(uint)Marshal.SizeOf(typeof(Caps)))==0)result.Add(c.name);}return result.ToArray(); }
        readonly object gate=new object();
        readonly List<IntPtr> headers=new List<IntPtr>(),data=new List<IntPtr>();
        readonly MemoryStream pcm=new MemoryStream();
        readonly AutoResetEvent changed=new AutoResetEvent(false);
        readonly byte[] chunk=new byte[3200];
        Callback callback; IntPtr handle; volatile bool stopping; volatile int level,peakLevel,voiceFrames; volatile bool heard;
        int bytes,firstVoiceBytes=-1,lastVoiceBytes;
        public int Level { get{return level;} }
        public int PeakLevel { get{return peakLevel;} }
        public int VoiceFrames { get{return voiceFrames;} }
        public int DurationMs { get{lock(gate)return bytes/32;} }
        public bool Heard { get{return heard;} }
        public void Start() {
            Format f=new Format {tag=1,channels=1,rate=16000,average=32000,align=2,bits=16,extra=0};
            callback=OnAudio;
            uint result=waveInOpen(out handle,0xffffffff,ref f,callback,UIntPtr.Zero,0x30000);
            if(result!=0)throw new Exception("মাইক্রোফোন খোলা যায়নি (code "+result+")");
            uint hs=(uint)Marshal.SizeOf(typeof(Header));
            for(int i=0;i<4;i++) {
                IntPtr d=Marshal.AllocHGlobal(3200),p=Marshal.AllocHGlobal((int)hs);
                data.Add(d); headers.Add(p);
                Header h=new Header {data=d,length=3200}; Marshal.StructureToPtr(h,p,false);
                if(waveInPrepareHeader(handle,p,hs)!=0 || waveInAddBuffer(handle,p,hs)!=0)throw new Exception("মাইক বাফার চালু হয়নি");
            }
            if(waveInStart(handle)!=0)throw new Exception("রেকর্ডিং শুরু হয়নি");
        }
        void OnAudio(IntPtr h,uint message,UIntPtr instance,IntPtr p1,IntPtr p2) {
            if(message!=0x3c0 || stopping)return;
            try {
                Header header=(Header)Marshal.PtrToStructure(p1,typeof(Header));
                int length=Math.Min(chunk.Length,(int)header.recorded);
                Marshal.Copy(header.data,chunk,0,length);
                double sum=0;for(int i=0;i+1<length;i+=2){short n=(short)(chunk[i]|chunk[i+1]<<8);sum+=(double)n*n;}
                double rms=length==0?0:Math.Sqrt(sum/Math.Max(1,length/2))/32768;
                level=(int)Math.Min(100,rms*700);
                if(level>peakLevel)peakLevel=level;
                lock(gate){
                    pcm.Write(chunk,0,length);
                    if(rms>VoiceRmsThreshold){
                        if(firstVoiceBytes<0)firstVoiceBytes=bytes;
                        voiceFrames++;
                        lastVoiceBytes=bytes+length;
                        heard=voiceFrames>=2;
                    }
                    bytes+=length;
                }
                if(!stopping)waveInAddBuffer(handle,p1,(uint)Marshal.SizeOf(typeof(Header)));
                changed.Set();
            } catch { }
        }
        public bool WaitForEnd(Func<bool> cancel,int maxSeconds,double trailingSilenceSeconds,double initialSilenceSeconds) {
            var elapsed=Stopwatch.StartNew();
            int trailingBytes=(int)(BytesPerSecond*trailingSilenceSeconds);
            while(elapsed.Elapsed.TotalSeconds<maxSeconds) {
                if(cancel())return false;
                changed.WaitOne(100);
                lock(gate){if(heard && bytes-lastVoiceBytes>=trailingBytes)return true;}
                if(!heard && elapsed.Elapsed.TotalSeconds>=initialSilenceSeconds)return false;
            }
            return heard;
        }
        public void Save(string path) {
            Stop();byte[] audio;int start,length;
            lock(gate){
                audio=pcm.GetBuffer();
                start=Math.Max(0,firstVoiceBytes-BytesPerSecond/3)&~1;
                length=(Math.Min(bytes,lastVoiceBytes+BytesPerSecond/4)-start)&~1;
            }
            using(BinaryWriter w=new BinaryWriter(File.Create(path))) {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(length+36);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(length);w.Write(audio,start,length);
            }
        }
        public void Stop(){if(handle==IntPtr.Zero||stopping)return;stopping=true;waveInStop(handle);waveInReset(handle);}
        public void Dispose() {
            Stop();if(handle!=IntPtr.Zero){for(int i=0;i<headers.Count;i++){waveInUnprepareHeader(handle,headers[i],(uint)Marshal.SizeOf(typeof(Header)));Marshal.FreeHGlobal(headers[i]);Marshal.FreeHGlobal(data[i]);}waveInClose(handle);handle=IntPtr.Zero;}
            pcm.Dispose();changed.Dispose();GC.KeepAlive(callback);
        }
    }
}

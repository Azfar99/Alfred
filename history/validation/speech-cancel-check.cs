using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace Alfred {
 public static class Program { public static string Root=@"F:\Alfred", Temp=@"F:\Alfred\temp"; }
 public static class CancellationCheck {
  public static int Main() {
   int failures=0;
   foreach(bool direct in new bool[]{false,true}) {
    using(SpeechTranscriber transcriber=new SpeechTranscriber())
    using(CancellationTokenSource source=new CancellationTokenSource()) {
     Exception caught=null; Stopwatch watch=Stopwatch.StartNew();
     Thread cancel=new Thread(delegate(){ Thread.Sleep(250); if(direct)transcriber.Cancel();else source.Cancel(); });cancel.IsBackground=true;cancel.Start();
     try { transcriber.Transcribe(@"F:\Alfred\temp\calculator-request-sample.wav",@"F:\Alfred\build\cancel-check-"+(direct?"direct":"token"),source.Token,"auto",false); }
     catch(Exception error){caught=error;}
     cancel.Join();
     bool ok=caught is OperationCanceledException && !transcriber.FallbackUsed && watch.ElapsedMilliseconds<5000;
     Console.WriteLine((ok?"PASS":"FAIL")+" "+(direct?"Cancel()":"token cancellation")+" stops native process without CPU fallback in "+watch.ElapsedMilliseconds+" ms; exception="+(caught==null?"none":caught.GetType().Name));
     if(!ok)failures++;
    }
   }
   using(SpeechTranscriber transcriber=new SpeechTranscriber()) {
    transcriber.Dispose();bool stopped=false;
    try{transcriber.Transcribe(@"F:\Alfred\temp\calculator-request-sample.wav",@"F:\Alfred\build\disposed-check",CancellationToken.None,"auto",false);}catch(ObjectDisposedException){stopped=true;}
    Console.WriteLine((stopped?"PASS":"FAIL")+" disposed backend rejects transcription");if(!stopped)failures++;
   }
   return failures;
  }
 }
}
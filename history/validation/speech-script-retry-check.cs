using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
namespace Alfred {
 public static class Program { public static string Root=@"F:\Alfred", Temp=@"F:\Alfred\temp"; }
 public static class ScriptRetryCheck {
  public static int Main() {
   string report=@"F:\Alfred\build\speech-script-retry-check.txt";
   StringBuilder log=new StringBuilder(); int failures=0;
   MethodInfo guard=typeof(SpeechTranscriber).GetMethod("NeedsBengaliRetry",BindingFlags.NonPublic|BindingFlags.Static);
   string[] tests={"আমার meeting tomorrow সকাল দশটায়", "Can you open the calculator for me?", "हमार शोलार बानला हमी तोनाते", "১২৩৪৫!?", "AB कख"};
   bool[] expected={false,false,true,false,false};
   for(int i=0;i<tests.Length;i++){bool value=(bool)guard.Invoke(null,new object[]{tests[i]});bool pass=value==expected[i];if(!pass)failures++;log.AppendLine((pass?"PASS":"FAIL")+" script guard: "+tests[i]);}
   string[] clips={@"F:\Alfred\temp\calculator-request-sample.wav",@"F:\Alfred\build\bengali-sample.wav",@"F:\Alfred\temp\english-dictation-sample.wav"};
   foreach(string audio in clips) {
    using(SpeechTranscriber transcriber=new SpeechTranscriber()) {
     string text=transcriber.Transcribe(audio,@"F:\Alfred\build\retry-test-"+Path.GetFileNameWithoutExtension(audio),CancellationToken.None,"auto",audio.Contains("dictation"));
     log.AppendLine(Path.GetFileName(audio)+": "+transcriber.ElapsedMs+" ms; retry="+transcriber.ScriptRetry+"; language="+transcriber.Language+"; fallback="+transcriber.FallbackUsed+"; "+text);
     log.AppendLine("Diagnostic: "+transcriber.LastError);
     if(audio.Contains("bengali")){if(!transcriber.ScriptRetry||transcriber.Language!="bn")failures++;}
     else if(transcriber.ScriptRetry||transcriber.Language!="auto")failures++;
     if(audio.Contains("calculator") && text!="Can you open the calculator for me?")failures++;
    }
   }
   log.AppendLine(failures==0?"PASS all guard and recorded-sample route checks":"FAIL count="+failures);
   File.WriteAllText(report,log.ToString(),Encoding.UTF8);Console.Write(log.ToString());return failures;
  }
 }
}
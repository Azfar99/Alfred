using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
namespace Alfred {
 public static class Program { public static string Root="F:\\Alfred", Temp="F:\\Alfred\\temp"; }
 public static class SpeechBenchmark {
  static string Clean(string s){return s.Replace("\t"," ").Replace("\r"," ").Replace("\n"," ");}
  public static int Main(string[] args) {
   string root=@"F:\Alfred", temp=Path.Combine(root,"temp"), build=Path.Combine(root,"build");
   Directory.CreateDirectory(build);
   string report=Path.Combine(build,"speech-upgrade-benchmark.tsv");
   File.WriteAllText(report,"Sample\tBackend\tWallMs\tPeakWorkingMiB\tPeakPrivateMiB\tText\r\n",Encoding.UTF8);
   string[] clips={Path.Combine(temp,"calculator-request-sample.wav"),Path.Combine(build,"bengali-sample.wav"),Path.Combine(temp,"english-dictation-sample.wav")};
   foreach(string audio in clips) {
    if(!File.Exists(audio)){Console.WriteLine("MISSING "+audio);continue;}
    bool dictation=audio.IndexOf("dictation",StringComparison.OrdinalIgnoreCase)>=0;
    string output=Path.Combine(build,"bench-"+Path.GetFileNameWithoutExtension(audio));
    // The original CLI configuration is retained for the baseline, including CPU-only and eight threads.
    ProcessStartInfo si=new ProcessStartInfo {FileName=Path.Combine(root,"engine","Release","whisper-cli.exe"),WorkingDirectory=temp,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    si.Arguments="-m \""+Path.Combine(root,"models","ggml-bengali-q5_1.bin")+"\" -f \""+audio+"\" -l auto -t 8 -p 1 -nt -np -otxt -of \""+output+"-old\" -ng -bs 1 -bo 1";
    si.EnvironmentVariables["TEMP"]=temp;si.EnvironmentVariables["TMP"]=temp;
    long peakWorking=0,peakPrivate=0;Stopwatch w=Stopwatch.StartNew();
    using(Process p=new Process()) { p.StartInfo=si;p.OutputDataReceived+=delegate{};p.ErrorDataReceived+=delegate{};p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();
      while(!p.WaitForExit(30)){try{p.Refresh();peakWorking=Math.Max(peakWorking,p.PeakWorkingSet64);peakPrivate=Math.Max(peakPrivate,p.PrivateMemorySize64);}catch{} if(w.ElapsedMilliseconds>75000){p.Kill();p.WaitForExit();throw new TimeoutException("Baseline timeout");}}
      p.WaitForExit();if(p.ExitCode!=0)throw new Exception("Baseline exit "+p.ExitCode);
    }
    string old=File.ReadAllText(output+"-old.txt",Encoding.UTF8).Trim();
    File.AppendAllText(report,Clean(Path.GetFileName(audio))+"\tOld Bengali Q5 CPU\t"+w.ElapsedMilliseconds+"\t"+(peakWorking/1048576.0).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"\t"+(peakPrivate/1048576.0).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"\t"+Clean(old)+"\r\n",Encoding.UTF8);
    using(SpeechTranscriber t=new SpeechTranscriber(root,temp)) {
      string text=t.Transcribe(audio,output+"-turbo",CancellationToken.None,"auto",dictation);
      File.AppendAllText(report,Clean(Path.GetFileName(audio))+"\t"+t.Backend+"\t"+t.ElapsedMs+"\t"+(t.PeakWorkingSetBytes/1048576.0).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"\t"+(t.PeakPrivateBytes/1048576.0).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"\t"+Clean(text)+"\r\n",Encoding.UTF8);
      Console.WriteLine(Path.GetFileName(audio)+": "+t.Backend+", "+t.ElapsedMs+" ms, "+text);
    }
   }
   using(SpeechTranscriber t=new SpeechTranscriber(root,temp)) {
      CancellationTokenSource cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
      try{t.Transcribe(clips[0],Path.Combine(build,"cancel-pre-start"),cancelled.Token,"auto",false);}catch(OperationCanceledException){stopped=true;}
      if(!stopped||t.FallbackUsed)throw new Exception("Cancelled call incorrectly reached inference/fallback");
      Console.WriteLine("PASS cancelled token prevents inference and fallback");
   }
   Console.WriteLine("Report: "+report);return 0;
  }
 }
}
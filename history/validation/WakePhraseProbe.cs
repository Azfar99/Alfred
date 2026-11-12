using System;
using System.Diagnostics;
using System.IO;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;

static class WakePhraseProbe {
    static readonly string Root = @"F:\Alfred";
    static readonly string[] Phrases = { "Alfred", "hello Alfred", "hey Alfred", "okay Alfred", "ok Alfred", "hello Alfred bhai", "hey Alfred bhai", "okay Alfred bhai", "ok Alfred bhai", "hello Alfred bye", "hello Alfred with 700ms gap" };
    static readonly string[] WakePhrases = { "hello Alfred", "hey Alfred", "okay Alfred", "ok Alfred", "hello Alfred bhai", "hey Alfred bhai", "okay Alfred bhai", "ok Alfred bhai", "hello Alfred bye", "hey Alfred bye", "okay Alfred bye", "ok Alfred bye", "hello Alfred vai", "hey Alfred vai", "okay Alfred vai", "ok Alfred vai" };
    static bool Accept(RecognitionResult result) {
        if (result == null || result.Words.Count < 2 || result.Words.Count > 3 || result.Confidence < 0.55 || result.Words[0].Confidence < 0.50) return false;
        string phrase = (result.Text ?? "").Trim().ToLowerInvariant();
        foreach (string accepted in WakePhrases) if (phrase == accepted.ToLowerInvariant()) return true;
        return false;
    }
    static int Main() {
        RecognizerInfo info = null;
        foreach (RecognizerInfo candidate in SpeechRecognitionEngine.InstalledRecognizers()) if (candidate.Culture.Name == "en-US") { info = candidate; break; }
        if (info == null) throw new Exception("en-US recognizer unavailable");
        StringBuilder report = new StringBuilder();
        report.AppendLine("Recognizer: " + info.Name + " (" + info.Culture.Name + ")");
        using (SpeechSynthesizer voice = new SpeechSynthesizer()) {
            foreach (InstalledVoice installed in voice.GetInstalledVoices()) report.AppendLine("TTS voice: " + installed.VoiceInfo.Name + " (" + installed.VoiceInfo.Culture.Name + ")");
            for (int n = 0; n < Phrases.Length; n++) {
                string wav = Path.Combine(Root, "temp", "wake-optimization-" + n + ".wav");
                voice.SetOutputToWaveFile(wav);
                if (n == Phrases.Length - 1) { voice.Speak("hello"); System.Threading.Thread.Sleep(700); voice.Speak("Alfred"); }
                else voice.Speak(Phrases[n]);
                voice.SetOutputToNull();
                using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(info)) {
                    GrammarBuilder builder = new GrammarBuilder { Culture = info.Culture };
                    GrammarBuilder greeting = new GrammarBuilder { Culture = info.Culture };
                    greeting.Append(new Choices("hello", "hey", "okay", "ok"));
                    greeting.Append("Alfred");
                    GrammarBuilder suffix = new GrammarBuilder { Culture = info.Culture };
                    suffix.Append(new Choices("bhai", "bye", "vai"));
                    greeting.Append(suffix, 0, 1);
                    engine.LoadGrammar(new Grammar(greeting));
                    GrammarBuilder bare = new GrammarBuilder { Culture = info.Culture };
                    bare.Append("Alfred");
                    engine.LoadGrammar(new Grammar(bare));
                    engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(700); engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(800);
                    engine.SetInputToWaveFile(wav); Stopwatch timer = Stopwatch.StartNew(); RecognitionResult result = engine.Recognize(TimeSpan.FromSeconds(6)); timer.Stop();
                    bool accepted = Accept(result), expected = n != 0;
                    report.AppendFormat("{0} | {1} | confidence={2:F3} first={3:F3} | accepted={4} expected={5} | {6:F0}ms\r\n", Phrases[n], result == null ? "(none)" : result.Text, result == null ? 0 : result.Confidence, result == null || result.Words.Count == 0 ? 0 : result.Words[0].Confidence, accepted, expected, timer.Elapsed.TotalMilliseconds);
                    if (accepted != expected) { Console.Write(report.ToString()); File.WriteAllText(Path.Combine(Root, "build", "wake-phrase-optimization.txt"), report.ToString(), Encoding.UTF8); return 2; }
                }
            }
        }
        File.WriteAllText(Path.Combine(Root, "build", "wake-phrase-optimization.txt"), report.ToString(), Encoding.UTF8); Console.Write(report.ToString()); return 0;
    }
}

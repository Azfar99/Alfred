using System;
using System.IO;
using System.Speech.Synthesis;
using System.Text;
static class SpeechProbe {
    static int Main() {
        string root = @"F:\Alfred";
        string wave = Path.Combine(root, "temp", "speech-voice-check.wav");
        StringBuilder report = new StringBuilder();
        try {
            using (SpeechSynthesizer speaker = new SpeechSynthesizer()) {
                InstalledVoice chosen = null;
                foreach (InstalledVoice voice in speaker.GetInstalledVoices()) {
                    if (voice.Enabled && String.Equals(voice.VoiceInfo.Name, "Microsoft David Desktop", StringComparison.OrdinalIgnoreCase)) { chosen = voice; break; }
                }
                if (chosen == null) throw new Exception("Microsoft David Desktop unavailable");
                speaker.SelectVoice(chosen.VoiceInfo.Name);
                speaker.Rate = -2;
                speaker.Volume = 100;
                speaker.SetOutputToWaveFile(wave);
                speaker.Speak("Yes, sir. The time is six thirty PM. I have opened the page for you.");
                speaker.SetOutputToNull();
            }
            long length = new FileInfo(wave).Length;
            if (length < 1000) throw new Exception("Synthesized output is empty");
            report.AppendLine("PASS Microsoft David Desktop selected");
            report.AppendLine("PASS slow-rate speech generated, bytes=" + length);
            File.WriteAllText(Path.Combine(root, "build", "speech-voice-check.txt"), report.ToString(), Encoding.UTF8);
            Console.Write(report.ToString());
            return 0;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(root, "build", "speech-voice-check.txt"), ex.ToString(), Encoding.UTF8);
            Console.Error.WriteLine(ex);
            return 1;
        } finally { try { if (File.Exists(wave)) File.Delete(wave); } catch { } }
    }
}
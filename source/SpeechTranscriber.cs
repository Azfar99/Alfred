using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Alfred {
    // On-demand native inference: no resident model/server, and no microphone audio leaves this PC.
    public sealed class SpeechTranscriber : IDisposable {
        readonly string root, temp;
        readonly object gate = new object();
        Process active;
        bool disposed, running, cancelRequested;
        long peakWorking, peakPrivate;
        string backend = "not started", lastError = "";
        long elapsedMs;
        bool fallbackUsed, scriptRetry;
        string actualLanguage = "auto";
        public string Backend { get { lock(gate) return backend; } }
        public long ElapsedMs { get { lock(gate) return elapsedMs; } }
        public string LastError { get { lock(gate) return lastError; } }
        public bool FallbackUsed { get { lock(gate) return fallbackUsed; } }
        public bool ScriptRetry { get { lock(gate) return scriptRetry; } }
        public string Language { get { lock(gate) return actualLanguage; } }
        public long PeakWorkingSetBytes { get { lock(gate) return peakWorking; } }
        public long PeakPrivateBytes { get { lock(gate) return peakPrivate; } }
        public SpeechTranscriber() : this(Program.Root, Program.Temp) { }
        public SpeechTranscriber(string applicationRoot, string temporaryDirectory) {
            root = Path.GetFullPath(applicationRoot);
            temp = Path.GetFullPath(temporaryDirectory);
        }
        public string Transcribe(string audio, string output, CancellationToken token, string language, bool dictation) {
            lock(gate) {
                if(disposed) throw new ObjectDisposedException("SpeechTranscriber");
                if(running) throw new InvalidOperationException("Another transcription is already running.");
                running = true; cancelRequested = false; fallbackUsed = false; scriptRetry = false; actualLanguage = "auto"; lastError = ""; peakWorking = 0; peakPrivate = 0; elapsedMs = 0;
            }
            Stopwatch watch = Stopwatch.StartNew();
            try {
                CheckCancellation(token);
                if(!File.Exists(audio)) throw new FileNotFoundException("Recorded audio was not found.", audio);
                if(language != "bn" && language != "en") language = "auto";
                string gpuExe = Path.Combine(root, "engine", "cuda", "whisper-cli.exe");
                string turbo = Path.Combine(root, "models", "ggml-large-v3-turbo-q5_0.bin");
                if(File.Exists(gpuExe) && File.Exists(turbo)) {
                    try {
                        string text = Run(gpuExe, turbo, audio, output, token, language, dictation, true, watch);
                        // Very short Bengali clips can be classified as Hindi. Accept Bengali/Latin mixtures,
                        // but bound an automatic script mismatch to one Bengali decode of the same audio.
                        if(language == "auto" && NeedsBengaliRetry(text)) {
                            lock(gate) { scriptRetry = true; lastError = "Auto selected an unsupported script; retried Bengali once."; }
                            string retryOutput = output + ".bn-retry";
                            try {
                                string retried = Run(gpuExe, turbo, audio, retryOutput, token, "bn", dictation, true, watch);
                                if(!String.IsNullOrWhiteSpace(retried)) {
                                    File.WriteAllText(output + ".txt", retried, Encoding.UTF8);
                                    return retried;
                                }
                                lock(gate) { actualLanguage = "auto"; lastError = "Auto selected an unsupported script; Bengali retry was empty, so the original transcript was preserved."; }
                            }
                            catch(OperationCanceledException) { throw; }
                            catch(TimeoutException) { throw; }
                            catch(Exception retryError) {
                                CheckCancellation(token);
                                lock(gate) { actualLanguage = "auto"; lastError = "Auto selected an unsupported script; Bengali retry failed, so the original transcript was preserved: " + Limit(retryError.Message, 900); }
                            }
                            finally { try { if(File.Exists(retryOutput + ".txt")) File.Delete(retryOutput + ".txt"); } catch { } }
                        }
                        return text;
                    }
                    catch(OperationCanceledException) { throw; }
                    catch(TimeoutException) { throw; }
                    catch(Exception gpuError) {
                        CheckCancellation(token);
                        lock(gate) { if(disposed) throw new OperationCanceledException("Transcription stopped."); fallbackUsed = true; lastError = "Turbo GPU failed: " + Limit(gpuError.Message, 1200); }
                    }
                }
                return Run(Path.Combine(root, "engine", "Release", "whisper-cli.exe"),
                           File.Exists(Path.Combine(root,"models","ggml-small-q5_1.bin"))?Path.Combine(root,"models","ggml-small-q5_1.bin"):Path.Combine(root, "models", "ggml-bengali-q5_1.bin"),
                           audio, output, token, language, dictation, false, watch);
            }
            catch(Exception error) {
                lock(gate) { lastError = Limit((lastError.Length == 0 ? "" : lastError + " | ") + error.Message, 1800); }
                throw;
            }
            finally { lock(gate) { elapsedMs = watch.ElapsedMilliseconds; running = false; } }
        }
        string Run(string executable, string model, string audio, string output, CancellationToken token,
                   string language, bool dictation, bool gpu, Stopwatch totalWatch) {
            CheckCancellation(token);
            if(totalWatch.ElapsedMilliseconds >= 75000) throw new TimeoutException("Speech recognition took more than 75 seconds.");
            if(!File.Exists(executable)) throw new FileNotFoundException("Speech engine is missing.", executable);
            if(!File.Exists(model)) throw new FileNotFoundException("Speech model is missing.", model);
            Directory.CreateDirectory(temp);
            string textFile = output + ".txt";
            if(File.Exists(textFile)) File.Delete(textFile);
            ProcessStartInfo info = new ProcessStartInfo {
                FileName = executable, WorkingDirectory = temp, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            // Turbo uses the GPU; four host threads avoid starving the desktop. Never force an app vocabulary.
            info.Arguments = "-m " + Quote(model) + " -f " + Quote(audio) + " -l " + language +
                " -t 4 -p 1 -nt -otxt -of " + Quote(output) + " -bs " + (gpu && (dictation || language == "bn") ? "3" : "1") +
                " -bo 1 -sns" + (gpu ? "" : " -ng");
            info.EnvironmentVariables["TEMP"] = temp;
            info.EnvironmentVariables["TMP"] = temp;
            StringBuilder errors = new StringBuilder();
            object errorGate = new object();
            using(Process process = new Process()) {
                process.StartInfo = info;
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if(e.Data == null) return;
                    lock(errorGate) { errors.AppendLine(e.Data); if(errors.Length > 8192) errors.Remove(0, errors.Length - 8192); }
                };
                process.OutputDataReceived += delegate { };
                try {
                    lock(gate) {
                        if(disposed) throw new OperationCanceledException("Transcription stopped.");
                        CheckCancellation(token);
                        process.Start(); active = process; actualLanguage = language; backend = gpu ? "Whisper large-v3-turbo Q5 / CUDA" : "Whisper multilingual small Q5 / CPU";
                    }
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    process.BeginErrorReadLine(); process.BeginOutputReadLine();
                    using(token.Register(delegate { StopProcess(process); })) {
                        while(!process.WaitForExit(40)) {
                            SampleMemory(process);
                            CheckCancellation(token);
                            lock(gate) { if(disposed) throw new OperationCanceledException("Transcription stopped."); }
                            if(totalWatch.ElapsedMilliseconds >= 75000) throw new TimeoutException("Speech recognition took more than 75 seconds.");
                        }
                        process.WaitForExit(); // Drain both async readers before consuming stderr/output.
                        SampleMemory(process);
                        CheckCancellation(token);
                        lock(gate) { if(disposed) throw new OperationCanceledException("Transcription stopped."); }
                        if(process.ExitCode != 0) {
                            string detail; lock(errorGate) detail = Limit(errors.ToString().Trim(), 1500);
                            throw new InvalidOperationException("Speech engine exited with code " + process.ExitCode + ". " + detail);
                        }
                    }
                }
                finally {
                    StopProcess(process);
                    lock(gate) { if(Object.ReferenceEquals(active, process)) active = null; }
                }
            }
            if(!File.Exists(textFile)) throw new IOException("Speech engine did not produce a transcript.");
            string result = File.ReadAllText(textFile, Encoding.UTF8).Trim();
            // Remove only stand-alone silence/event markers. Do not rewrite or translate the speaker's text.
            result = Regex.Replace(result, @"\[(?:blank_audio|music|silence)\]|\((?:music|silence|blank)\)", "", RegexOptions.IgnoreCase).Trim();
            return result;
        }
        static bool NeedsBengaliRetry(string text) {
            int letters = 0, unsupported = 0;
            foreach(char c in text) {
                if(!Char.IsLetter(c)) continue;
                letters++;
                bool bengali = c >= '\u0980' && c <= '\u09ff';
                bool latin = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                             (c >= '\u00c0' && c <= '\u024f') || (c >= '\u1e00' && c <= '\u1eff');
                if(!bengali && !latin) unsupported++;
            }
            return unsupported >= 3 && unsupported * 2 > letters;
        }
        void SampleMemory(Process process) {
            try {
                process.Refresh();
                long working = process.PeakWorkingSet64, committed = process.PrivateMemorySize64;
                lock(gate) { if(working > peakWorking) peakWorking = working; if(committed > peakPrivate) peakPrivate = committed; }
            } catch { }
        }
        static string Quote(string value) {
            if(value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new ArgumentException("An invalid file path was passed to speech recognition.");
            return "\"" + value + "\"";
        }
        static string Limit(string value, int maximum) { return value.Length <= maximum ? value : value.Substring(value.Length - maximum); }
        static void StopProcess(Process process) {
            try { if(!process.HasExited) { process.Kill(); process.WaitForExit(2000); } } catch { }
        }
        void CheckCancellation(CancellationToken token) { token.ThrowIfCancellationRequested(); lock(gate) { if(disposed || cancelRequested) throw new OperationCanceledException("Transcription stopped.", token); } }
        public void Cancel() { Process process; lock(gate) { cancelRequested = true; process = active; } if(process != null) StopProcess(process); }
        public void Dispose() { Process process; lock(gate) { if(disposed) return; disposed = true; process = active; } if(process != null) StopProcess(process); }
    }
}
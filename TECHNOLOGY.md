# Alfred 2.0 local speech stack

Native C#/.NET Framework WinForms, WinMM 16kHz PCM shared capture, sherpa-onnx1.13.8 native Windows x64 runtime, English GigaSpeech3.3M int8 KWS, Silero VAD, whisper.cpp CUDA with multilingual large-v3-turbo Q5, Windows UI Automation and verified Unicode SendInput, Microsoft David Desktop TTS. No cloud credentials or ongoing paid API calls.

Official sources:
- https://k2-fsa.github.io/sherpa/onnx/kws/index.html
- https://k2-fsa.github.io/sherpa/onnx/kws/pretrained_models/index.html
- https://www.nuget.org/packages/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.8
- https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-large-v3-turbo-q5_0.bin
- https://github.com/ggml-org/whisper.cpp

Strong cloud mixed-language candidates researched: ElevenLabs Scribe v3 Live and Deepgram Nova3 multilingual. They require a separate API key, network and billing; neither is installed or enabled.

Known limits: real user wake is not yet reliable; Bengali sample accuracy remains imperfect; no acoustic echo cancellation; no blanket verification across Facebook/Instagram/WhatsApp live sites. Never auto-resend uncertain messages. Model/runtime assets require Git LFS when publishing.

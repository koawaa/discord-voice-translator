# Third-party components

Discord Voice Translator ships with or downloads the following components. Each one keeps its
own license.

| Component | Use | License |
|---|---|---|
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | .NET bindings for whisper.cpp | MIT |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | Speech recognition engine | MIT |
| [OpenAI Whisper large-v3-turbo](https://github.com/openai/whisper) (ggml q8_0 conversion, downloaded on first launch) | Speech recognition model | MIT |
| [Silero VAD](https://github.com/snakers4/silero-vad) | Voice activity detection model | MIT |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Runs Silero VAD | MIT |
| [.NET](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf) | Application runtime and UI | MIT |
| Microsoft Visual C++ Runtime | Required by whisper.cpp and ONNX Runtime | Microsoft Visual C++ Redistributable license |

Translation is done by the [DeepL API](https://www.deepl.com/pro-api) with your own key, under
DeepL's terms. Speaker names come from your own Discord client through Discord's local RPC.
This project is not affiliated with Discord, DeepL or OpenAI.

# EchoFlow

Push-to-talk dictation for Windows. Hold a shortcut, talk, let go, and your words are typed wherever your cursor is.

- **Local speech recognition** with Whisper (whisper.cpp). Uses your graphics card through Vulkan when there is one, and the CPU otherwise.
- **Optional AI cleanup** with Claude Haiku: removes filler words, fixes punctuation and formats lists. Needs your own Anthropic API key, which is stored encrypted on your PC.
- **Hold to talk** (Ctrl + Win by default). Double-tap for hands-free, then tap again to finish. Esc cancels.
- **Dictionary** of names and words that are always spelled your way. Words you correct right after dictating are added automatically.
- **History** of everything you've dictated, plus a Copy button when there was nowhere to paste.
- **Small voice visualizer** at the bottom of the screen. It's colorful while AI cleanup is working and white when it isn't.
- **Dark mode**, start/stop sounds, and pausing music while you talk.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
cd src
dotnet build -c Release
```

Run `bin/Release/net10.0-windows10.0.19041.0/win-x64/EchoFlow.exe`. The first run downloads the speech model (about 1.6 GB on PCs with a capable graphics card, less on others).

Your settings, history and dictionary are kept in `%APPDATA%\EchoFlow`. Models and the log are kept in `%LOCALAPPDATA%\EchoFlow`.

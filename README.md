# whisparrot

Minimal voice input for Windows.

Press a hotkey, speak in Japanese, and the text lands in the app you are using.
Transcription runs locally with Whisper; an optional second stage can send the
transcribed text (never the audio) to an AI endpoint you configure yourself
for cleanup such as punctuation and filler removal.

- No account, no subscription, no telemetry
- Portable single exe for x64 and ARM64, with a simple built-in update check
- A tiny pill UI modelled on the Windows voice typing bar (Win+H)

Status: specification phase. See [docs/spec-v1.0.md](docs/spec-v1.0.md) for the
frozen v1.0 specification (in Japanese).

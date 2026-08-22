---
status: accepted
---

# Use a native Windows single-process architecture

Focus Listener will use .NET 10, C#, and WPF in one desktop process, with NAudio for simultaneous microphone/WASAPI capture, the official Google.GenAI C# SDK for Gemini, and SQLite for local analysis. This deliberately replaces the reference design’s Tauri/Vue frontend plus Python/FastAPI sidecar: the first product is Windows-only, has a tiny overlay, and needs reliable native audio and global-input integration more than cross-platform UI portability. A deep FocusSession Module hides audio arbitration, model coordination, triggering, timing, queuing, validation, and journaling behind a two-method Interface; the WPF shell does not orchestrate those concerns.

## Considered options

- **Python and PySide6**: faster for exploratory scripts, but Windows loopback capture, global shortcuts, packaging, and long-running desktop lifecycle introduce more platform-specific integration risk.
- **Tauri/Vue plus Python/FastAPI**: offers web UI flexibility and provider expansion, but adds process management, a local transport seam, deployment complexity, and several shallow interfaces before the prototype needs them.

## Consequences

The prototype is intentionally Windows-specific and commits its desktop code to C#/WPF. There is no Python sidecar, local HTTP server, or public provider-plugin interface. Gemini, audio, time, and journaling remain replaceable through internal seams with production and test adapters, so model and infrastructure changes do not leak into the WPF Interface.

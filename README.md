# Codex Windows Completion Reminder

A small Windows tray companion for Codex Desktop. It can show a persistent, non-activating popup when a task completes or Codex requests tool approval. The popup stays on top, does not take keyboard focus, and closes on a keyboard key or mouse button while passing that input through.

This project targets **Codex Desktop on Windows**. Codex CLI, WSL, remote sessions, and service sessions are not supported in v1.

## Features

- Separate notification preferences for task completion, tool approval, and conversation-choice requests.
- Choose custom popup, Codex native notification, both, or off for each category.
- Edit a title and description per category; adjust popup width, height, and four colors with a live preview.
- Pause temporarily, disable this companion persistently, or uninstall it.
- No network calls. The hook passes only event type and an opaque event/turn ID to the local companion. It does not store or transmit prompts, command text, or key/mouse input.

## Event support

- **Task completed:** Codex `Stop` hook.
- **Tool approval:** Codex `PermissionRequest` hook. The hook never approves or denies the request; Codex's normal approval prompt and policy remain in control.
- **Conversation choice/input:** Codex Desktop's native question notification is the supported fallback. The current public Hook API does not expose a stable custom event for every conversation-choice prompt, so selecting a custom popup for this category falls back to the native notification. The app does not guess from message punctuation or text.

`Stop` marks the end of an assistant turn, not proof that the user's overall task is finished. If the assistant ends a turn by asking a question, the custom completion popup may appear alongside Codex's native question notification. The default remains the custom completion popup; choose native or off for completion if you prefer to avoid that pairing.

Codex native notifications are managed by Codex and Windows. This app does not change those settings. Enable the relevant notification category in Codex Settings and allow Windows notifications for the ChatGPT/Codex app. Native notification wording and duration remain controlled by Codex/Windows. See the [Codex notifications guide](https://learn.chatgpt.com/docs/notifications).

## Build

Requirements: Windows with .NET Framework 4.x and Windows PowerShell.

```powershell
.\build.ps1
```

The build compiles `CodexReminder.exe` and `CodexReminderHook.exe` into `dist/` and runs the built-in settings/route self-tests.

## Install and update

For a prebuilt install, download the Windows ZIP from GitHub Releases, extract it, and run `install.ps1` in PowerShell. The included binaries are unsigned; see the release trust note below.

To build from source, clone the repository and run `build.ps1` first, then `install.ps1`:

```powershell
.\install.ps1
```

The installer builds the app, copies it to `%USERPROFILE%\.codex\codex-reminder`, merges only this app's `Stop` and `PermissionRequest` handlers into `%USERPROFILE%\.codex\hooks.json`, and adds a per-user Startup entry. Existing hooks and Codex notification settings are preserved.

Restart Codex and review/trust the updated hook definitions when prompted. Hooks that have not been reviewed and trusted are skipped by Codex.

## Settings

Right-click the tray icon and choose **提醒设置…**. Settings are saved per Windows user under `%LOCALAPPDATA%\CodexCompletionReminder\settings.json`.

- The global switch and per-event channel choices control this companion's custom popups. The native Codex channel is still controlled by Codex/Windows settings.
- For the conversation-choice category, the settings window explains that custom event detection is unavailable and native notifications are used as fallback.
- The temporary pause only applies until the app exits. The global switch persists across restarts.
- Choosing **退出提醒器** closes the current process; it does not uninstall the hook or disable a future launch. Use the global switch to disable reminders or run the uninstaller to remove the integration.

## Uninstall

```powershell
.\uninstall.ps1
```

Uninstall removes only this app's hook handlers and Startup entry. It keeps user settings by default. To also remove the settings file:

```powershell
.\uninstall.ps1 -RemoveSettings
```

## Tests

```powershell
.\tests\Run-Tests.ps1
```

The automated tests cover defaults, config serialization and normalization, channel routing, hook output behavior, and safe install/uninstall merging. The release checklist also requires a manual desktop test for foreground/background popups, key/mouse dismissal, click-through, Codex native notification fallback, and multi-monitor sizing.

## Release trust

GitHub Release executables are unsigned. Verify the SHA-256 file attached to a release, or build from source. Windows may show a SmartScreen warning for unsigned binaries.

## License

MIT. See [LICENSE](LICENSE).

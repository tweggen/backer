# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this directory.

## Overview

YourBacker is an Avalonia-based cross-platform desktop client (Windows, macOS, Linux) that serves as a system tray application for controlling and monitoring the BackerAgent backup service. It has no main window - the UI is entirely driven by a system tray icon and popup dialogs.

## Build & Run

```bash
# Build
dotnet build YourBacker/

# Run
dotnet run --project YourBacker/
```

## Architecture

**Pattern**: MVVM using CommunityToolkit.Mvvm (v8.4.0) with `[ObservableProperty]` attributes.

**Communication with BackerAgent** (localhost:5931):
- **Primary**: SignalR hub at `/backercontrolhub` for real-time push updates
- **Fallback**: HTTP polling every 10 seconds when SignalR disconnects

```
YourBacker
├── SignalR Hub Connection
│   ├── ServiceStateChanged → Updates tray menu state
│   └── TransferStatsUpdated → Updates TransferWindow
│
└── HTTP REST Endpoints
    ├── GET  /status     → RCloneServiceState
    ├── GET  /config     → RCloneServiceOptions
    ├── GET  /transfers  → TransferStatsResult
    ├── PUT  /config     → Update configuration
    └── POST /start, /stop, /restart, /quit
```

## Key Files

| File | Purpose |
|------|---------|
| `App.axaml.cs` | Application lifecycle, tray icon, SignalR connection, polling timer |
| `ConfigWindow.axaml.cs` | Configuration dialog with validation (URL, email, password, autostart) |
| `TransferWindow.axaml.cs` | Real-time file transfer monitoring UI |
| `TransferManager.cs` | Manages ObservableCollection of active transfers |
| `FileTransferViewModel.cs` | ViewModel for individual transfer (progress, speed, size) |
| `Platform/` | Platform-specific service launchers |

## Platform-Specific Service Launchers

Located in `Platform/`:
- **Windows**: Uses `sc.exe start BackerAgent` with UAC elevation (`Verb = "runas"`)
- **macOS**: Uses `launchctl` with user-level LaunchAgent or elevated LaunchDaemon
- **Linux**: Currently unsupported (`UnsupportedServiceLauncher`)

macOS also uses P/Invoke in `Program.cs` to hide the dock icon (accessory mode).

## State Management

**Service States** (from `RCloneServiceState.ServiceState` enum):
Starting, WaitConfig, CheckOnline, BackendsLoggingIn, CheckRCloneProcess, StartRCloneProcess, WaitStart, Running, WaitStop, RestartingForReauth, Exiting

**Transfer Lifecycle**:
1. Transfer appears in stats → added to TransferManager
2. Transfer disappears from stats → marked as "done"
3. 10 seconds after "done" → removed from list

## Window Management

No traditional navigation - windows are event-driven singletons:
```csharp
if (_window == null || !_window.IsVisible)
{
    _window = new Window();
    _window.Closed += (s, e) => _window = null;
    _window.Show();
}
else
{
    _window.Activate();
}
```

## Thread Safety

All UI updates from async operations (SignalR, HTTP) are marshaled via:
```csharp
Dispatcher.UIThread.Post(() => UpdateUI());
```

## Dependencies

- **Avalonia** v11.2.3 - UI framework with Fluent theme
- **Microsoft.AspNetCore.SignalR.Client** v9.0.7 - Real-time communication
- **CommunityToolkit.Mvvm** v8.4.0 - MVVM helpers
- **WorkerRClone** (project reference) - Shared models

## Tray Menu Structure

```
Status: [current state]
─────────────────────
Start Service / Stop Service (toggle)
Restart Service
Quit Service / Launch Service (toggle)
─────────────────────
Configure...
Transfers...
─────────────────────
Exit YourBacker
```

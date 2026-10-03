# Architecture

```
src/
├── HyprWin.App/           # WPF application (UI layer)
│   ├── App.xaml.cs         # Entry point, orchestrates all subsystems
│   ├── TopBarWindow        # Taskbar replacement with modular widgets
│   ├── SystemMenuWindow    # macOS Control Center-style popup
│   ├── SettingsWindow      # Visual configuration editor
│   └── CalendarPopupWindow # Calendar popup for clock widget
│
└── HyprWin.Core/           # Core logic (no UI dependencies)
    ├── TilingEngine         # BSP tree layout with DeferWindowPos batching
    ├── TilingLayout         # HWND-free BSP / master-stack math
    ├── WorkspaceManager     # Virtual workspace management
    ├── WindowTracker        # Win32 event hooks for window lifecycle
    ├── WindowDispatcher     # Keybind action handler (incl. robust close)
    ├── KeyboardHook         # WH_KEYBOARD_LL global hook
    ├── AnimationEngine      # Frame-synced window animations
    ├── BorderRenderer       # GPU-accelerated focus border
    ├── SystemInfoService    # Hardware metrics, media, battery, brightness
    ├── TouchpadGestureService # Raw Input HID touchpad gesture detection
    ├── TaskbarManager       # Native taskbar hide/show
    ├── MonitorManager       # Multi-monitor enumeration
    └── Configuration/       # TOML config parsing with hot-reload
```

Config lives at `%APPDATA%\HyprWin\hyprwin.toml`. The app runs as the logged-in user (`asInvoker`). IPC is a named pipe `\\.\pipe\hyprwin-ipc` restricted to the current user and is not started when the process is elevated.

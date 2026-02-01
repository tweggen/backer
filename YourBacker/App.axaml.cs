using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using WorkerRClone.Models;
using YourBacker.Platform;

namespace YourBacker;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _startStopItem;
    private NativeMenuItem? _quitLaunchItem;
    
    private readonly IServiceLauncher _serviceLauncher = ServiceLauncherFactory.Create();
    private bool _serviceAvailable = true;
    
    private ConfigWindow? _configWindow;
    private TransferWindow? _transferWindow;
    
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5931") };
    private HubConnection? _backerAgentConnection;
    private DispatcherTimer? _pollTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Don't create a main window - we're a tray application
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Setup tray icon
            SetupTrayIcon();
            
            // Setup SignalR connection
            _ = SetupSignalRConnectionAsync();
            
            // Fallback polling timer
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _pollTimer.Tick += async (s, e) =>
            {
                if (_backerAgentConnection?.State != HubConnectionState.Connected)
                {
                    await UpdateServiceStateAsync();
                }
            };
            _pollTimer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        _statusItem = new NativeMenuItem("Connecting...")
        {
            IsEnabled = false
        };

        _startStopItem = new NativeMenuItem("Connecting...")
        {
            IsEnabled = false
        };
        _startStopItem.Click += async (s, e) =>
        {
            if (_startStopItem.Header?.ToString()?.StartsWith("Start") == true)
            {
                await _http.PostAsync("/start", null);
            }
            else
            {
                await _http.PostAsync("/stop", null);
            }
        };

        var restartItem = new NativeMenuItem("Restart Service");
        restartItem.Click += async (s, e) => await _http.PostAsync("/restart", null);

        _quitLaunchItem = new NativeMenuItem("Quit Service");
        _quitLaunchItem.Click += OnQuitLaunchClicked;

        var configItem = new NativeMenuItem("Configure...");
        configItem.Click += (s, e) => ShowConfigWindow();

        var transfersItem = new NativeMenuItem("Transfers...");
        transfersItem.Click += (s, e) => ShowTransferWindow();

        var exitItem = new NativeMenuItem("Exit YourBacker");
        exitItem.Click += (s, e) =>
        {
            _trayIcon?.Dispose();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };

        _trayMenu = new NativeMenu
        {
            _statusItem,
            _startStopItem,
            new NativeMenuItemSeparator(),
            restartItem,
            _quitLaunchItem,
            new NativeMenuItemSeparator(),
            configItem,
            transfersItem,
            new NativeMenuItemSeparator(),
            exitItem
        };

        _trayIcon = new TrayIcon
        {
            ToolTipText = "YourBacker",
            Menu = _trayMenu,
            IsVisible = true,
            Icon = CreateTrayIcon()
        };
        
        // Double-click opens transfer window
        _trayIcon.Clicked += (s, e) => ShowTransferWindow();
    }

    /// <summary>
    /// Loads the tray icon from Avalonia resources or falls back to a generated icon.
    /// </summary>
    private WindowIcon? CreateTrayIcon()
    {
        try
        {
            // Try to load from Avalonia embedded resources
            var uri = new Uri("avares://YourBacker/Assets/backer-tray.png");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                return new WindowIcon(stream);
            }
            
            // Fallback: try to load from file next to executable
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "backer-tray.png");
            if (File.Exists(iconPath))
            {
                return new WindowIcon(iconPath);
            }
            
            // No icon available - create a simple colored bitmap
            return CreateFallbackIcon();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load tray icon: {ex.Message}");
            return CreateFallbackIcon();
        }
    }
    
    /// <summary>
    /// Creates a simple fallback icon (colored square) if no icon file is available
    /// </summary>
    private WindowIcon? CreateFallbackIcon()
    {
        try
        {
            // Create a 32x32 bitmap with a simple colored background
            var bitmap = new WriteableBitmap(
                new Avalonia.PixelSize(32, 32),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);
            
            using (var fb = bitmap.Lock())
            {
                unsafe
                {
                    var ptr = (uint*)fb.Address;
                    // Fill with a blue-ish color (BGRA format)
                    // Color: #4A90D9 (a nice blue)
                    uint color = 0xFFD9904A; // BGRA: Blue=0xD9, Green=0x90, Red=0x4A, Alpha=0xFF
                    
                    for (int y = 0; y < 32; y++)
                    {
                        for (int x = 0; x < 32; x++)
                        {
                            // Create a simple "B" shape or just a filled rounded-ish square
                            // For simplicity, let's create a filled square with a border effect
                            bool isBorder = x < 2 || x >= 30 || y < 2 || y >= 30;
                            bool isInner = x >= 4 && x < 28 && y >= 4 && y < 28;
                            
                            if (isBorder)
                            {
                                ptr[y * 32 + x] = 0xFF8B6914; // Darker border
                            }
                            else if (isInner)
                            {
                                ptr[y * 32 + x] = color;
                            }
                            else
                            {
                                ptr[y * 32 + x] = 0xFFA07828; // Mid tone
                            }
                        }
                    }
                }
            }
            
            // Convert bitmap to icon
            using var ms = new MemoryStream();
            bitmap.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        catch
        {
            return null;
        }
    }

    private void ShowConfigWindow()
    {
        if (_configWindow == null || !_configWindow.IsVisible)
        {
            _configWindow = new ConfigWindow();
            _configWindow.Closed += (s, e) => _configWindow = null;
            _configWindow.Show();
        }
        else
        {
            _configWindow.Activate();
        }
    }

    private void ShowTransferWindow()
    {
        if (_transferWindow == null || !_transferWindow.IsVisible)
        {
            _transferWindow = new TransferWindow();
            _transferWindow.Closed += (s, e) => _transferWindow = null;
            _transferWindow.Show();
        }
        else
        {
            _transferWindow.Activate();
        }
    }

    private async void OnQuitLaunchClicked(object? sender, EventArgs e)
    {
        if (_serviceAvailable)
        {
            // Service is running — quit it
            try
            {
                await _http.PostAsync("/quit", null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to quit service: {ex.Message}");
            }
        }
        else if (_serviceLauncher.IsSupported)
        {
            // Service is not running — try to launch it
            if (_quitLaunchItem != null)
            {
                _quitLaunchItem.Header = "Launching...";
                _quitLaunchItem.IsEnabled = false;
            }

            var success = await _serviceLauncher.TryLaunchAsync();

            if (!success)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_quitLaunchItem != null)
                    {
                        _quitLaunchItem.Header = "Launch Service";
                        _quitLaunchItem.IsEnabled = true;
                    }
                });
            }
            // On success the polling / SignalR reconnection will pick up
            // the new state and update the UI automatically.
        }
    }

    private async Task SetupSignalRConnectionAsync()
    {
        try
        {
            _backerAgentConnection = new HubConnectionBuilder()
                .WithUrl("http://localhost:5931/backercontrolhub")
                .WithAutomaticReconnect(new[] 
                { 
                    TimeSpan.Zero, 
                    TimeSpan.FromSeconds(2), 
                    TimeSpan.FromSeconds(5), 
                    TimeSpan.FromSeconds(10) 
                })
                .Build();

            // Subscribe to service state changes
            _backerAgentConnection.On<RCloneServiceState>("ServiceStateChanged", state =>
            {
                Dispatcher.UIThread.Post(() => UpdateUIWithState(state));
            });

            // Subscribe to transfer stats updates  
            _backerAgentConnection.On<TransferStatsResult>("TransferStatsUpdated", stats =>
            {
                Dispatcher.UIThread.Post(() => _transferWindow?.UpdateTransferStats(stats));
            });

            // Handle reconnection events
            _backerAgentConnection.Reconnecting += error =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_statusItem != null) _statusItem.Header = "Reconnecting...";
                    if (_startStopItem != null) _startStopItem.IsEnabled = false;
                });
                return Task.CompletedTask;
            };

            _backerAgentConnection.Reconnected += connectionId =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_statusItem != null) _statusItem.Header = "Reconnected";
                });
                return Task.CompletedTask;
            };

            _backerAgentConnection.Closed += async error =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_statusItem != null) _statusItem.Header = "Disconnected";
                    if (_startStopItem != null) _startStopItem.IsEnabled = false;
                });

                // Try to reconnect after 5 seconds
                await Task.Delay(5000);
                await ConnectSignalRAsync();
            };

            await ConnectSignalRAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SignalR setup error: {ex}");
        }
    }

    private async Task ConnectSignalRAsync()
    {
        try
        {
            if (_backerAgentConnection != null)
            {
                await _backerAgentConnection.StartAsync();
                
                // Request initial state
                await _backerAgentConnection.InvokeAsync("RequestCurrentState");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SignalR connection error: {ex}");
            // Connection failed, fallback polling will handle it
        }
    }

    private async Task UpdateServiceStateAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<RCloneServiceState>(
                "/status", 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (status != null)
            {
                Dispatcher.UIThread.Post(() => UpdateUIWithState(status));
            }
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                _serviceAvailable = false;
                if (_statusItem != null) _statusItem.Header = "Service unavailable";
                if (_startStopItem != null)
                {
                    _startStopItem.Header = "Service unavailable";
                    _startStopItem.IsEnabled = false;
                }
                if (_quitLaunchItem != null)
                {
                    if (_serviceLauncher.IsSupported)
                    {
                        _quitLaunchItem.Header = "Launch Service";
                        _quitLaunchItem.IsEnabled = true;
                    }
                    else
                    {
                        _quitLaunchItem.Header = "Service unavailable";
                        _quitLaunchItem.IsEnabled = false;
                    }
                }
            });
        }
    }

    private void UpdateUIWithState(RCloneServiceState status)
    {
        // Service is responding — mark as available and restore quit item
        _serviceAvailable = true;
        
        if (_statusItem != null)
        {
            _statusItem.Header = status.StateString;
        }
        
        if (_quitLaunchItem != null)
        {
            _quitLaunchItem.Header = "Quit Service";
            _quitLaunchItem.IsEnabled = true;
        }
        
        if (_startStopItem != null)
        {
            switch (status.State)
            {
                case RCloneServiceState.ServiceState.CheckRCloneProcess:
                case RCloneServiceState.ServiceState.StartRCloneProcess:
                case RCloneServiceState.ServiceState.WaitStop:
                case RCloneServiceState.ServiceState.Starting:
                case RCloneServiceState.ServiceState.WaitConfig:
                case RCloneServiceState.ServiceState.Exiting:
                case RCloneServiceState.ServiceState.CheckOnline:
                case RCloneServiceState.ServiceState.BackendsLoggingIn:
                case RCloneServiceState.ServiceState.RestartingForReauth:
                    _startStopItem.Header = "";
                    _startStopItem.IsEnabled = false;
                    break;

                case RCloneServiceState.ServiceState.WaitStart:
                    _startStopItem.Header = "Start Service";
                    _startStopItem.IsEnabled = true;
                    break;

                case RCloneServiceState.ServiceState.Running:
                    _startStopItem.Header = "Stop Service";
                    _startStopItem.IsEnabled = true;
                    break;
            }
        }
    }
}

using System.Configuration;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using WorkerRClone.Models;

namespace BackerControl;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIcon trayIcon;

    private ConfigWindow? _winConfig = null;
    private TransferWindow? _winTransfer = null;
    private HttpClient http = new() { BaseAddress = new Uri("http://localhost:5931") };
    
    private ToolStripMenuItem _startStopItem;
    private ToolStripMenuItem _statusItem;
    private DispatcherTimer _pollTimer;
    private System.Threading.SynchronizationContext _formsContext;
    
    // SignalR connection (BackerControl as CLIENT, BackerAgent as SERVER)
    private HubConnection? _backerAgentConnection;


    private void _showTransferWindow()
    {
        if (_winTransfer == null)
        {
            _winTransfer = new TransferWindow();
            _winTransfer.Closed += (s, e) => _winConfig = null; // release reference when closed
            _winTransfer.Show();
        }
        else
        {
            if (_winTransfer.WindowState == WindowState.Minimized)
                _winTransfer.WindowState = WindowState.Normal;
            
            _winTransfer.Activate();
        }
    }
    
    
    private void _showConfigWindow()
    {
        if (_winConfig == null)
        {
            _winConfig = new ConfigWindow();
            _winConfig.Closed += (s, e) => _winConfig = null; // release reference when closed
            _winConfig.Show();
        }
        else
        {
            // If already open, bring it to front
            if (_winConfig.WindowState == WindowState.Minimized)
                _winConfig.WindowState = WindowState.Normal;

            _winConfig.Activate();
        }
    }
    
    
    private async Task _updateServiceState()
    {
        try
        {
            // Example: GET /status returns JSON { "running": true }
            
            var status = await http.GetFromJsonAsync<RCloneServiceState>("/status", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (status != null)
            {
                // Update Windows Forms controls on the Forms UI thread
                _formsContext.Post(_ =>
                {
                    _statusItem.Text = status.StateString;
                    switch (status.State)
                    {
                        case RCloneServiceState.ServiceState.CheckRCloneProcess:
                        case RCloneServiceState.ServiceState.StartRCloneProcess:
                        case RCloneServiceState.ServiceState.WaitStop:
                        case RCloneServiceState.ServiceState.Starting:
                        case RCloneServiceState.ServiceState.WaitConfig:
                        case RCloneServiceState.ServiceState.Exiting:
                        case RCloneServiceState.ServiceState.CheckOnline:
                            _startStopItem.Text = "";
                            _startStopItem.Enabled = false;
                            break;
                        
                        case RCloneServiceState.ServiceState.WaitStart:
                            _startStopItem.Text = "Start Service";
                            _startStopItem.Enabled = true;
                            break;
                            
                        case RCloneServiceState.ServiceState.Running:
                            _startStopItem.Text = "Stop Service";
                            _startStopItem.Enabled = true;
                            break;
                    }
                }, null);
            }
        }
        catch
        {
            // Update Windows Forms controls on the Forms UI thread
            _formsContext.Post(_ =>
            {
                _statusItem.Text = "Service unavailable";
                _startStopItem.Text = "Service unavailable";
                _startStopItem.Enabled = false;
            }, null);
        }
    }
    
    
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            File.WriteAllText("error.log", ev.ExceptionObject.ToString());
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            File.WriteAllText("error.log", ev.Exception.ToString());
            ev.Handled = true;
        };
        
        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        
        // Capture the Windows Forms synchronization context
        _formsContext = System.Threading.SynchronizationContext.Current 
            ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
        
        trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Backer Control",
            Visible = true
        };

        _statusItem = new ToolStripMenuItem("Connecting...");
        _statusItem.Enabled = false;

        _startStopItem = new ToolStripMenuItem("Connecting...");
        
        _startStopItem.Click += async (s, ev) =>
        {
            if (_startStopItem.Text.StartsWith("Start"))
            {
                await http.PostAsync("/start", null);
            }
            else
            {
                await http.PostAsync("/stop", null);
            }
        };
        
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_startStopItem);
        menu.Items.Add("Restart Service", null, 
            async (s, ev) => await http.PostAsync("/restart", null));
        menu.Items.Add("Quit Service", null,
            async (s, ev) => await http.PostAsync("/quit", null));
        menu.Items.Add("Configure...", null, 
            async (s, ev) =>
        {
            _showConfigWindow();
        });
        menu.Items.Add("Transfers...", null, 
            async (s, ev) =>
            {
                _showTransferWindow();
            });
        menu.Items.Add("Exit Tray", null, (s, ev) =>
        {
            trayIcon.Visible = false;
            Shutdown();
        });

        trayIcon.ContextMenuStrip = menu;
        
        // Setup SignalR connection to BackerAgent
        await _setupSignalRConnection();
        
        // Fallback poll every 10 seconds in case SignalR disconnects
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _pollTimer.Tick += async (s, ev) => 
        {
            // Only poll if SignalR is disconnected
            if (_backerAgentConnection?.State != HubConnectionState.Connected)
            {
                await _updateServiceState();
            }
        };
        _pollTimer.Start();
    }
    
    private async Task _setupSignalRConnection()
    {
        try
        {
            _backerAgentConnection = new HubConnectionBuilder()
                .WithUrl("http://localhost:5931/backercontrolhub")
                .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
                .Build();
            
            // Subscribe to service state changes
            _backerAgentConnection.On<RCloneServiceState>("ServiceStateChanged", state =>
            {
                Dispatcher.Invoke(() =>
                {
                    _updateUIWithState(state);
                });
            });
            
            // Subscribe to transfer stats updates
            _backerAgentConnection.On<TransferStatsResult>("TransferStatsUpdated", stats =>
            {
                Dispatcher.Invoke(() =>
                {
                    _winTransfer?.UpdateTransferStats(stats);
                });
            });
            
            // Handle reconnection
            _backerAgentConnection.Reconnecting += error =>
            {
                Dispatcher.Invoke(() =>
                {
                    _formsContext.Post(_ =>
                    {
                        _statusItem.Text = "Reconnecting...";
                        _startStopItem.Enabled = false;
                    }, null);
                });
                return Task.CompletedTask;
            };
            
            _backerAgentConnection.Reconnected += connectionId =>
            {
                Dispatcher.Invoke(() =>
                {
                    _formsContext.Post(_ =>
                    {
                        _statusItem.Text = "Reconnected";
                    }, null);
                });
                return Task.CompletedTask;
            };
            
            _backerAgentConnection.Closed += async error =>
            {
                Dispatcher.Invoke(() =>
                {
                    _formsContext.Post(_ =>
                    {
                        _statusItem.Text = "Disconnected";
                        _startStopItem.Enabled = false;
                    }, null);
                });
                
                // Try to reconnect after 5 seconds
                await Task.Delay(5000);
                await _connectSignalR();
            };
            
            await _connectSignalR();
        }
        catch (Exception ex)
        {
            File.AppendAllText("error.log", $"SignalR setup error: {ex}\n");
        }
    }
    
    private async Task _connectSignalR()
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
            // Connection failed, fallback to polling
            File.AppendAllText("error.log", $"SignalR connection error: {ex}\n");
        }
    }
    
    private void _updateUIWithState(RCloneServiceState status)
    {
        _formsContext.Post(_ =>
        {
            _statusItem.Text = status.StateString;
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
                    _startStopItem.Text = "";
                    _startStopItem.Enabled = false;
                    break;
                
                case RCloneServiceState.ServiceState.WaitStart:
                    _startStopItem.Text = "Start Service";
                    _startStopItem.Enabled = true;
                    break;
                    
                case RCloneServiceState.ServiceState.Running:
                    _startStopItem.Text = "Stop Service";
                    _startStopItem.Enabled = true;
                    break;
            }
        }, null);
    }
}
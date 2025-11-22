using System.Configuration;
using System.Data;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using WorkerRClone.Models;

namespace BackerControl;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIcon trayIcon;

    private ConfigWindow? _winConfig = null;
    private HttpClient http = new() { BaseAddress = new Uri("http://localhost:5931") };
    
    private ToolStripMenuItem _startStopItem;
    private ToolStripMenuItem _statusItem;
    private DispatcherTimer  _pollTimer;
    
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
            var status = await http.GetFromJsonAsync<RCloneServiceState>("/status");
            if (status != null)
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
            }
        }
        catch
        {
            _statusItem.Text = "Service unavailable";
            _startStopItem.Text = "Service unavailable";
            _startStopItem.Enabled = false;
        }
    }
    
    
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        
        trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "My Service Control",
            Visible = true
        };

        _statusItem = new ToolStripMenuItem("Checking state...");
        _statusItem.Enabled = false;

        _startStopItem = new ToolStripMenuItem("Checking state...");
        
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
        menu.Items.Add("Exit Tray", null, (s, ev) =>
        {
            trayIcon.Visible = false;
            Shutdown();
        });

        trayIcon.ContextMenuStrip = menu;
        
        // Poll service state every second
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += async (s, ev) => await _updateServiceState();
        _pollTimer.Start();
    }
}
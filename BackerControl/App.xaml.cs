using System.Configuration;
using System.Data;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Forms;

namespace BackerControl;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIcon trayIcon;

    private ConfigWindow? _winConfig = null;
    private HttpClient http = new() { BaseAddress = new Uri("http://localhost:5931") };
    
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

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restart Service", null, async (s, ev) => await http.PostAsync("/restart", null));
        menu.Items.Add("Quit Service", null, async (s, ev) => await http.PostAsync("/quit", null));
        menu.Items.Add("Configure...", null, async (s, ev) =>
        {

            _showConfigWindow();
        });
        menu.Items.Add("Exit Tray", null, (s, ev) =>
        {
            trayIcon.Visible = false;
            Shutdown();
        });

        trayIcon.ContextMenuStrip = menu;
    }
}
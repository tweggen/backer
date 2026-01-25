using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkerRClone.Configuration;

namespace YourBacker;

public partial class ConfigWindow : Window
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5931") };

    public ConfigWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = await _http.GetFromJsonAsync<RCloneServiceOptions>("/config");
            
            CloudUrlTextBox.Text = options?.UrlSignalR ?? "";
            EmailTextBox.Text = options?.BackerUsername ?? "";
            PasswordBox.Text = options?.BackerPassword ?? "";
            AutostartCheckBox.IsChecked = options?.Autostart ?? false;
        }
        catch (HttpRequestException ex)
        {
            // Show error - in Avalonia we can use a simple dialog or inline message
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            
            // Set defaults on error
            CloudUrlTextBox.Text = "";
            EmailTextBox.Text = "";
            PasswordBox.Text = "";
            AutostartCheckBox.IsChecked = false;
        }
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var options = new RCloneServiceOptions
        {
            BackerUsername = EmailTextBox.Text ?? "",
            BackerPassword = PasswordBox.Text ?? "",
            UrlSignalR = CloudUrlTextBox.Text ?? "",
            RClonePath = "",
            RCloneOptions = "",
            Autostart = AutostartCheckBox.IsChecked ?? false
        };

        try
        {
            await _http.PutAsJsonAsync("/config", options);
            Close();
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            // Could show error dialog here
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

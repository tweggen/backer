using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using WorkerRClone.Configuration;

namespace BackerControl;

public partial class ConfigWindow : Window
{
    private HttpClient http = new() { BaseAddress = new Uri("http://localhost:5931") };

    public ConfigWindow()
    {
        InitializeComponent();
        Loaded += _onLoaded;
    }


    private async void _onLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // LoadingIndicator.Visibility = Visibility.Visible;

            //var data = await FetchDataAsync("https://api.example.com/data");
            var options = await http.GetFromJsonAsync<RCloneServiceOptions>("/config");
            CloudUrlTextBox.Text = options?.UrlSignalR ?? "nothing";
            EmailTextBox.Text = options?.BackerUsername ?? "your@email.com";
            PasswordBox.Password = options?.BackerPassword ?? "secret";
            AutostartCheckBox.IsChecked = options?.Autostart ?? false;
        }
        catch (HttpRequestException ex)
        {
            System.Windows.MessageBox.Show($"Error loading data: {ex.Message}");
        }
        finally
        {
            // LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }
    
    
    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // Dummy implementation: just read values and show a message
        string cloudUrl = CloudUrlTextBox.Text;
        string email = EmailTextBox.Text;
        string password = PasswordBox.Password;
        bool isAutostartEnabled = AutostartCheckBox.IsChecked ?? false;
        
        RCloneServiceOptions options = new ()
        {
            BackerUsername = email,
            BackerPassword = password,
            UrlSignalR = cloudUrl,
            RClonePath = "",
            RCloneOptions = "",
            Autostart = isAutostartEnabled
        };
        await http.PutAsJsonAsync("/config", options);

        // Close the window after "saving"
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        // Dummy implementation: just show a message and close
        System.Windows.MessageBox.Show(
            "Dummy Cancel: No changes saved.",
            "Cancel Clicked",
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );

        Close();
    }
}
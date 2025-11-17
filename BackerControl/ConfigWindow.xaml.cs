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
    }
    
    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // Dummy implementation: just read values and show a message
        string cloudUrl = CloudUrlTextBox.Text;
        string email = EmailTextBox.Text;
        string password = PasswordBox.Password;

        System.Windows.MessageBox.Show(
            $"Dummy Save:\nCloud URL: {cloudUrl}\nEmail: {email}\nPassword: {new string('*', password.Length)}",
            "Save Clicked",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
        
        
        RCloneServiceOptions options = new ()
        {
            BackerUsername = email,
            BackerPassword = password,
            UrlSignalR = cloudUrl,
            RClonePath = "",
            RCloneOptions = ""
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
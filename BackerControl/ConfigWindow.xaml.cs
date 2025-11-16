using System.Windows;

namespace BackerControl;

public partial class ConfigWindow : Window
{
    public ConfigWindow()
    {
        InitializeComponent();
    }
    
    private void OnSaveClicked(object sender, RoutedEventArgs e)
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
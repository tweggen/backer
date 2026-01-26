using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WorkerRClone.Configuration;

namespace YourBacker;

public partial class ConfigWindow : Window
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5931") };
    private bool _isSaving = false;

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
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            
            // Set defaults on error
            CloudUrlTextBox.Text = "";
            EmailTextBox.Text = "";
            PasswordBox.Text = "";
            AutostartCheckBox.IsChecked = false;
        }
    }

    private void ClearAllErrors()
    {
        CloudUrlErrorText.Text = "";
        CloudUrlErrorText.IsVisible = false;
        EmailErrorText.Text = "";
        EmailErrorText.IsVisible = false;
        PasswordErrorText.Text = "";
        PasswordErrorText.IsVisible = false;
        StatusText.IsVisible = false;
    }

    private void SetError(TextBlock errorTextBlock, string message)
    {
        errorTextBlock.Text = message;
        errorTextBlock.IsVisible = true;
    }

    private void SetStatus(string message, bool isSuccess)
    {
        StatusText.Text = message;
        StatusText.Foreground = isSuccess ? Brushes.Green : Brushes.Orange;
        StatusText.IsVisible = true;
    }

    /// <summary>
    /// Validates input fields on the client side.
    /// Returns true if all validations pass.
    /// </summary>
    private bool ValidateInputs()
    {
        ClearAllErrors();
        bool isValid = true;

        // Validate Cloud URL
        var cloudUrl = CloudUrlTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(cloudUrl))
        {
            SetError(CloudUrlErrorText, "Cloud Service URL is required.");
            isValid = false;
        }
        else if (!Uri.TryCreate(cloudUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            SetError(CloudUrlErrorText, "Please enter a valid URL (http:// or https://).");
            isValid = false;
        }

        // Validate Email
        var email = EmailTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(EmailErrorText, "Email is required.");
            isValid = false;
        }
        else if (!email.Contains('@') || !email.Contains('.'))
        {
            SetError(EmailErrorText, "Please enter a valid email address.");
            isValid = false;
        }

        // Validate Password
        var password = PasswordBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(password))
        {
            SetError(PasswordErrorText, "Password is required.");
            isValid = false;
        }

        return isValid;
    }

    private RCloneServiceOptions BuildOptionsFromForm()
    {
        return new RCloneServiceOptions
        {
            BackerUsername = EmailTextBox.Text?.Trim() ?? "",
            BackerPassword = PasswordBox.Text ?? "",
            UrlSignalR = CloudUrlTextBox.Text?.Trim() ?? "",
            RClonePath = "",
            RCloneOptions = "",
            Autostart = AutostartCheckBox.IsChecked ?? false
        };
    }

    /// <summary>
    /// Result of attempting to save configuration to the local BackerAgent service.
    /// </summary>
    private enum SaveResult
    {
        Success,
        ServerError,
        Timeout,
        ServiceNotRunning
    }

    /// <summary>
    /// Attempts to save the configuration to the local BackerAgent service.
    /// Returns the result and handles displaying appropriate error messages.
    /// </summary>
    private async Task<SaveResult> TrySaveConfigAsync(RCloneServiceOptions options)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            var response = await _http.PutAsJsonAsync("/config", options, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                return SaveResult.Success;
            }
            
            // Server returned an error - parse and display it
            var errorMessage = await response.Content.ReadAsStringAsync();
            
            // Try to map server errors to specific fields
            if (errorMessage.Contains("Cloud URL", StringComparison.OrdinalIgnoreCase))
            {
                SetError(CloudUrlErrorText, errorMessage);
            }
            else if (errorMessage.Contains("username", StringComparison.OrdinalIgnoreCase))
            {
                SetError(EmailErrorText, errorMessage);
            }
            else if (errorMessage.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                SetError(PasswordErrorText, errorMessage);
            }
            else
            {
                // Generic server error - show on the Cloud URL field as it's the "connection" field
                SetError(CloudUrlErrorText, $"Server error: {errorMessage}");
            }
            return SaveResult.ServerError;
        }
        catch (OperationCanceledException)
        {
            return SaveResult.Timeout;
        }
        catch (HttpRequestException ex)
        {
            // Local BackerAgent service is not running - this is a valid scenario
            System.Diagnostics.Debug.WriteLine($"BackerAgent not reachable: {ex.Message}");
            return SaveResult.ServiceNotRunning;
        }
    }

    private async void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        // Prevent re-triggering while a request is in flight
        if (_isSaving)
        {
            return;
        }

        // First, perform client-side validation
        if (!ValidateInputs())
        {
            return;
        }

        var options = BuildOptionsFromForm();
        _isSaving = true;

        try
        {
            var result = await TrySaveConfigAsync(options);
            
            switch (result)
            {
                case SaveResult.Success:
                    SetStatus("Configuration applied successfully.", isSuccess: true);
                    break;
                case SaveResult.Timeout:
                case SaveResult.ServiceNotRunning:
                    SetError(CloudUrlErrorText, "Internal error: BackerAgent service not reachable.");
                    break;
                // ServerError already sets its own error message
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        // Prevent re-triggering while a request is in flight
        if (_isSaving)
        {
            return;
        }

        // First, perform client-side validation
        if (!ValidateInputs())
        {
            return;
        }

        var options = BuildOptionsFromForm();
        _isSaving = true;

        try
        {
            var result = await TrySaveConfigAsync(options);
            
            switch (result)
            {
                case SaveResult.Success:
                    Close();
                    break;
                case SaveResult.Timeout:
                case SaveResult.ServiceNotRunning:
                    SetError(CloudUrlErrorText, "Internal error: BackerAgent service not reachable.");
                    break;
                // ServerError already sets its own error message
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

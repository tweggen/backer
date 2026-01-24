using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Poe.Components.Pages.BackerPages;


public class CustomValidator : ComponentBase
{
    [CascadingParameter] private EditContext? CurrentEditContext { get; set; }
    private ValidationMessageStore? _messageStore;


    public void Clear()
    {
        _messageStore?.Clear();
        CurrentEditContext?.NotifyValidationStateChanged();
    }

    protected override void OnInitialized()
    {
        if (CurrentEditContext != null)
        {
            _messageStore = new ValidationMessageStore(CurrentEditContext);
        }
    }

    public void AddError(string fieldName, string message)
    {
        if (CurrentEditContext == null || _messageStore == null)
            return;
            
        var fieldIdentifier = new FieldIdentifier(CurrentEditContext.Model, fieldName);
        _messageStore.Add(fieldIdentifier, message);
        CurrentEditContext.NotifyValidationStateChanged();
    }
}


public partial class Storages : ComponentBase
{
}
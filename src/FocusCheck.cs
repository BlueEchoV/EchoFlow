using System.Windows.Automation;

namespace EchoFlow;

/// Asks Windows whether the focused control accepts typing, so we know if a paste will land anywhere.
static class FocusCheck
{
    /// True if there's a text box to type into, false if not, null if Windows couldn't tell in time.
    public static async Task<bool?> HasTextFieldAsync()
    {
        var check = Task.Run(Check);
        return await Task.WhenAny(check, Task.Delay(700)) == check ? check.Result : null;
    }

    static bool? Check()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return false;

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                return !((ValuePattern)value).Current.IsReadOnly;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
            {
                // Web pages and viewers expose read-only text; editors expose editable text.
                var readOnly = ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                return readOnly is not true;
            }

            var type = element.Current.ControlType;
            return type == ControlType.Edit || type == ControlType.Document;
        }
        catch (Exception ex)
        {
            Log.Write($"Focus check failed: {ex.Message}");
            return null;
        }
    }
}

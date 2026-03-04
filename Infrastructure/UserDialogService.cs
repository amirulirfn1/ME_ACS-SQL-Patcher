using System.Windows;

namespace MagDbPatcher.Infrastructure;

public class UserDialogService : IUserDialogService
{
    public void ShowInfo(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title, bool useYesNo = false)
    {
        var buttons = useYesNo ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel;
        var result = MessageBox.Show(message, title, buttons, MessageBoxImage.Information);
        return useYesNo ? result == MessageBoxResult.Yes : result == MessageBoxResult.OK;
    }
}

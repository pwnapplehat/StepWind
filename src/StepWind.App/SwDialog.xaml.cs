using System.Windows;

namespace StepWind.App;

/// <summary>
/// The app's own dialog (native MessageBox would shatter the custom identity). Two shapes:
///   Confirm(...)      → primary/cancel, primary optionally danger-styled;
///   ThreeChoice(...)  → primary + secondary (danger) + cancel, for "keep or delete history".
/// Returns which button was pressed.
/// </summary>
public partial class SwDialog : Window
{
    public enum Choice
    {
        Cancel,
        Primary,
        Secondary,
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    private SwDialog(Window owner, string title, string message, string primary, string? secondary, bool dangerPrimary)
    {
        InitializeComponent();
        Owner = owner;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        if (dangerPrimary)
        {
            PrimaryButton.Style = (Style)FindResource("SW.Button.Danger");
        }

        if (secondary is not null)
        {
            SecondaryButton.Content = secondary;
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    public static Choice Confirm(Window owner, string title, string message, string primaryLabel, bool danger = false)
    {
        var d = new SwDialog(owner, title, message, primaryLabel, secondary: null, dangerPrimary: danger);
        d.ShowDialog();
        return d.Result;
    }

    public static Choice ThreeChoice(Window owner, string title, string message, string primaryLabel, string secondaryLabel)
    {
        var d = new SwDialog(owner, title, message, primaryLabel, secondaryLabel, dangerPrimary: false);
        d.ShowDialog();
        return d.Result;
    }

    /// <summary>A themed OK-style notice (result irrelevant).</summary>
    public static void Notice(Window owner, string title, string message)
    {
        var d = new SwDialog(owner, title, message, "OK", secondary: null, dangerPrimary: false);
        d.CancelButton.Visibility = Visibility.Collapsed;
        d.ShowDialog();
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Result = Choice.Primary;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Result = Choice.Secondary;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = Choice.Cancel;
        Close();
    }
}

using System.Windows;
using System.Windows.Input;

namespace SonarQuickMixer.Views;

public partial class PromptTextWindow : Window
{
    public PromptTextWindow(string title, string prompt, string initialText = "")
    {
        InitializeComponent();
        WindowDarkMode.TryEnable(this);
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialText ?? string.Empty;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string ResultText => InputBox.Text?.Trim() ?? string.Empty;

    public static bool TryPrompt(
        Window owner,
        string title,
        string prompt,
        string initialText,
        out string value)
    {
        var dialog = new PromptTextWindow(title, prompt, initialText)
        {
            Owner = owner
        };
        var ok = dialog.ShowDialog() == true;
        value = ok ? dialog.ResultText : string.Empty;
        return ok && !string.IsNullOrWhiteSpace(value);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputBox.Text))
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}

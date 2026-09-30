using System.Windows;
using Surfio.Services;

namespace Surfio;

public partial class UrlDialog : Window
{
    public UrlDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        Loaded += (_, _) =>
        {
            if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                UrlBox.Text = uri.ToString();
                UrlBox.SelectAll();
            }
            UrlBox.Focus();
        };
    }

    public string Url => UrlBox.Text;

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "That doesn't look like a URL.", "Surfio");
            return;
        }
        DialogResult = true;
    }
}

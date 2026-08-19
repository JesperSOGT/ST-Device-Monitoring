using System.Windows;

namespace ST_Device_Monitoring.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        Title = "About " + AppInfo.ProductName;
        ProductText.Text = AppInfo.ProductName;
        VersionText.Text = AppInfo.VersionLine;
        CopyrightText.Text = AppInfo.Copyright;
        RuntimeText.Text = $".NET {Environment.Version} · {Environment.OSVersion.VersionString}";
    }
}

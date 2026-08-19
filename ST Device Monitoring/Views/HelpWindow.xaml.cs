using System.Windows;

namespace ST_Device_Monitoring.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();

        VersionText.Text = $"{AppInfo.ProductName} {AppInfo.VersionLine}";

        foreach (var topic in HelpContent.Topics)
            TopicList.Items.Add(topic);

        TopicList.SelectedIndex = 0;
    }

    private void Topic_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TopicList.SelectedItem is not HelpTopic topic) return;
        TopicTitle.Text = topic.Title;
        TopicBody.Text = topic.Body;
    }
}

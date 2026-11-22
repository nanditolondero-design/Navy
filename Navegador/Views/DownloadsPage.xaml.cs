using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Navegador.Models;
using Navegador.Services;

namespace Navegador.Views;

public sealed partial class DownloadsPage : UserControl
{
    public DownloadsPage()
    {
        InitializeComponent();
        ItemsList.ItemsSource = DownloadsStore.Items;
        DownloadsStore.Items.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = DownloadsStore.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    private void Open_Click(object sender, RoutedEventArgs e) => ItemOf(sender)?.OpenFile();
    private void Folder_Click(object sender, RoutedEventArgs e) => ItemOf(sender)?.ShowInFolder();
    private void Cancel_Click(object sender, RoutedEventArgs e) => ItemOf(sender)?.Cancel();
    private void ClearList_Click(object sender, RoutedEventArgs e) => DownloadsStore.Items.Clear();
}

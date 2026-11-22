using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Navegador.Models;
using Navegador.Services;

namespace Navegador.Views;

public sealed partial class HistoryPage : UserControl, IRefreshable
{
    private readonly BrowserServices _services;
    private readonly ObservableCollection<HistoryEntry> _items = new();

    public HistoryPage(BrowserServices services)
    {
        _services = services;
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        Refresh();
    }

    public void Refresh()
    {
        _items.Clear();
        foreach (var entry in _services.History.Search(SearchBox.Text))
            _items.Add(entry);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry entry) _services.OpenUrl(entry.Url);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _services.History.Clear();
        Refresh();
    }
}

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Navegador.Models;
using Navegador.Services;

namespace Navegador.Views;

public sealed partial class BookmarksPage : UserControl, IRefreshable
{
    private readonly BrowserServices _services;
    private readonly ObservableCollection<Bookmark> _items = new();

    public BookmarksPage(BrowserServices services)
    {
        _services = services;
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        Refresh();
    }

    public void Refresh()
    {
        _items.Clear();
        foreach (var b in _services.Bookmarks.All) _items.Add(b);
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Bookmark b) _services.OpenUrl(b.Url);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Bookmark b)
        {
            _services.Bookmarks.Remove(b.Url);
            Refresh();
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Encore.App;

/// <summary>Builds the tray icon's menu: the live status, the Open items, Start with Windows, Restart and Quit.</summary>
internal static class TrayMenu
{
    /// <summary>The Restart item, shown only while the engine is in a crash loop.</summary>
    public const string RestartItemName = "RestartItem";

    /// <summary>The Control gallery item, which only dev and test instances have.</summary>
    public const string GalleryItemName = "GalleryItem";

    /// <summary>The live status item.</summary>
    public const string StatusItemName = "StatusItem";

    /// <summary>The menu; restart, gallery and openFolder are optional, and the gallery item is left out without its action.</summary>
    public static ContextMenu Create(
        MainViewModel model,
        Action open,
        Action quit,
        Action? restart = null,
        Action? gallery = null,
        Action<string>? openFolder = null)
    {
        var restartItem = new MenuItem { Header = "Restart", Name = RestartItemName, Visibility = Visibility.Collapsed };
        restartItem.Click += (_, _) => restart?.Invoke();

        var menu = new ContextMenu
        {
            Items =
            {
                StatusItem(model),
                new Separator(),
                Item("Open", open),
                Item("Open Library", () => OpenLibrary(model, open)),
                Item("Open the Clips folder", () => OpenClipsFolder(model, openFolder ?? OpenInExplorer)),
                new Separator(),
                StartWithWindowsItem(model),
                restartItem,
            },
        };
        if (gallery is not null)
        {
            var galleryItem = Item("Control gallery", gallery);
            galleryItem.Name = GalleryItemName;
            menu.Items.Add(galleryItem);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", quit));
        return menu;
    }

    /// <summary>Shows the Restart item only while the engine is in a crash loop.</summary>
    public static void ShowCrashLoop(MenuItem restartItem, bool crashLoop) =>
        restartItem.Visibility = crashLoop ? Visibility.Visible : Visibility.Collapsed;

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    // The status header's text, live while the menu is open.
    private static MenuItem StatusItem(MainViewModel model)
    {
        var item = new MenuItem { Name = StatusItemName, IsEnabled = false };
        var binding = new Binding(nameof(model.StatusLine.State)) { Source = model.StatusLine, Mode = BindingMode.OneWay };
        item.SetBinding(HeaderedItemsControl.HeaderProperty, binding);
        return item;
    }

    private static MenuItem StartWithWindowsItem(MainViewModel model)
    {
        var item = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        var binding = new Binding(nameof(model.General.StartWithWindows)) { Source = model.General, Mode = BindingMode.TwoWay };
        item.SetBinding(MenuItem.IsCheckedProperty, binding);
        return item;
    }

    private static void OpenLibrary(MainViewModel model, Action open)
    {
        model.SelectedNavItem = model.LibraryNav;
        open();
    }

    private static void OpenClipsFolder(MainViewModel model, Action<string> openFolder)
    {
        if (model.ClipsFolder is { } folder)
        {
            openFolder(folder);
        }
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Nothing to open: the tray has nowhere to say so, and the Library shows the folder.
        }
    }
}

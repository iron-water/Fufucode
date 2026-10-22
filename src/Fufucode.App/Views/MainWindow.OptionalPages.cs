using System;
using System.IO;
using Avalonia.Controls;
using Fufucode.App.Features.Library;
using Fufucode.App.Features.LocalReview;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Infrastructure;

namespace Fufucode.App;

public partial class MainWindow
{
    private LibraryPageView CreateLibraryPage()
    {
        _libraryService ??= new LibraryService(Path.Combine(
            Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root, "library"));
        var view = new LibraryPageView();
        var library = new LibraryViewModel(_libraryService);
        library.CitationCopyRequested += text => library.StatusText = AcceptLibraryCitation(text);
        view.Attach(library);
        return view;
    }

    IPluginWorkspacePage ILibraryPageFactory.CreatePage()
    {
        var view = CreateLibraryPage();
        return new PluginPageAdapter("library", view, view.Detach);
    }

    IPluginWorkspacePage ILocalReviewPageFactory.CreatePage()
    {
        var view = CreateLocalReviewPage();
        return new PluginPageAdapter("local-review", view, view.Detach, path =>
        {
            if (view.ViewModel is { } vm)
            {
                vm.ProjectPath = path ?? string.Empty;
                vm.RefreshExecutorContext();
            }
        });
    }
}

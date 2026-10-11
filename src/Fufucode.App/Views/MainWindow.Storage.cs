using System.IO;
using Fufucode.App.Features.Storage;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private StorageManagerViewModel _storageManager = null!;
    private StoragePage _storagePage = null!;
    internal StorageManagerViewModel StorageForTesting => _storageManager;

    private void InitializeStorage()
    {
        var root = Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root;
        var catalog = new StorageCatalog(new AppDataLayout(root), Path.Combine(root, "webview"), Path.Combine(root, "library"));
        _storageManager = new(catalog, 14, path =>
        {
            try { _ = OpenFolderAsync(path); }
            catch (System.Exception error) { _storageManager.Status = "打开目录失败：" + error.Message; }
        });
        _storagePage = new(_storageManager);
    }
}

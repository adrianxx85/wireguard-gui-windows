using System.Windows;

namespace WGClient;

internal static class PrivateClipboard
{
    public static void SetText(string text) => Clipboard.SetDataObject(CreateData(text), copy: true);

    static DataObject CreateData(string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        // Windows recognizes this clipboard format and skips history and cloud sync.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0], 0, 1, false, true), autoConvert: false);
        return data;
    }
}

using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CertWatch.App.Presentation;

/// <summary>
/// Windows notifications (Action Center toasts) through the built-in WinRT API, no extra package.
/// An unpackaged desktop app needs an App User Model ID registered under the current user, which
/// this does once (HKCU only, no admin rights).
/// </summary>
internal sealed partial class WindowsNotifier
{
    private const string Aumid = "CertWatch.App";
    private readonly ToastNotifier _notifier;

    [System.Runtime.InteropServices.LibraryImport("shell32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    public WindowsNotifier()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
        {
            key.SetValue("DisplayName", "CertWatch");
        }
        SetCurrentProcessExplicitAppUserModelID(Aumid);
        _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
    }

    /// <summary>Raised (on a background thread) when the user clicks a notification.</summary>
    public event Action? Activated;

    public void Show(string title, string text, string attribution)
    {
        // Site names come from the address bar; escape everything that goes into the XML.
        var xml = new XmlDocument();
        xml.LoadXml(
            "<toast><visual><binding template=\"ToastGeneric\">" +
            $"<text>{SecurityElement.Escape(title)}</text>" +
            $"<text>{SecurityElement.Escape(text)}</text>" +
            $"<text placement=\"attribution\">{SecurityElement.Escape(attribution)}</text>" +
            "</binding></visual></toast>");
        var toast = new ToastNotification(xml);
        toast.Activated += (_, _) => Activated?.Invoke();
        _notifier.Show(toast);
    }
}

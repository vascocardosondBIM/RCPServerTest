using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Windows;

namespace RevitSketchPoC.Core
{
    /// <summary>
    /// Opens a modeless WPF <see cref="Window"/> on the next <see cref="UIApplication.Idling"/> tick.
    /// Showing synchronously from <c>IExternalCommand.Execute</c> is a common cause of Revit "irrecoverable" faults.
    /// </summary>
    public static class RevitModelessWindowHost
    {
        public static void ShowDeferred(UIApplication uiApp, Window window)
        {
            if (uiApp == null)
            {
                throw new ArgumentNullException(nameof(uiApp));
            }

            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            EventHandler<IdlingEventArgs>? handler = null;
            handler = (_, _) =>
            {
                try
                {
                    uiApp.Idling -= handler;
                    window.Show();
                    window.Activate();
                }
                catch
                {
                    try
                    {
                        uiApp.Idling -= handler;
                    }
                    catch
                    {
                        // Ignore.
                    }
                }
            };

            uiApp.Idling += handler;
        }
    }
}

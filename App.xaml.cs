using System;
using System.Windows;

namespace ClipViewer
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            string filePath = null;

            if (e.Args.Length > 0)
            {
                filePath = e.Args[0];
            }

            var mainWindow = new MainWindow(filePath);
            mainWindow.Show();
        }
    }
}

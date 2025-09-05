// Program.cs — точка входа в приложение
using System;
using System.Windows.Forms;

namespace LibraryTerminal
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
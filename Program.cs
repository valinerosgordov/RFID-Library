using System;
using System.IO;
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
            Application.Run(new MainForm()); // <- этот тип должен существовать и быть public
        }
    }
}



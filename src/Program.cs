using System;
using System.Windows.Forms;
using SoulmvKit.UI;

namespace SoulmvKit
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SoulmvKit.Core.Logger.Init(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));
            SoulmvKit.Core.ProdutoNomes.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.Navegador.Init(AppDomain.CurrentDomain.BaseDirectory);
            SoulmvKit.Core.SolicitacoesCache.Init(AppDomain.CurrentDomain.BaseDirectory);

            var win = new MainWindow();
            // Flags de desenvolvimento para visualizar o shell sem fazer login real.
            bool shellPrev = args != null && Array.IndexOf(args, "--shell-preview") >= 0;
            bool confPrev = args != null && Array.IndexOf(args, "--conf-preview") >= 0;
            bool consPrev = args != null && Array.IndexOf(args, "--consulta-preview") >= 0;
            bool solicPrev = args != null && Array.IndexOf(args, "--solic-preview") >= 0;
            if (shellPrev || confPrev || consPrev || solicPrev)
            {
                win.Usuario = "PREVIEW";
                var shell = new SoulmvKit.UI.ShellView(win);
                win.ShowView(shell);
                if (confPrev) shell.IrParaConferencia();
                else if (consPrev) shell.IrParaConsulta();
                else if (solicPrev) shell.IrParaSolicitacoes();
            }
            Application.Run(win);
        }
    }
}

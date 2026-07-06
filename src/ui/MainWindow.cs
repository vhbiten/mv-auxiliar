using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Janela única do app. Hospeda a view de topo (login OU o shell com sidebar).
    public class MainWindow : Form
    {
        public string Usuario { get; set; }
        public SoulmvKit.Core.Session Session { get; set; }

        public MainWindow()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;   // escala o layout pelo DPI real (96 = base); cobre 100/125/150%
            this.Text = SoulmvKit.AppConfig.AppName;
            try { if (Assets.AppIcon != null) this.Icon = Assets.AppIcon; } catch { }
            this.ClientSize = new Size(1180, 700);     // tela HD (1366x768) com folga
            this.MinimumSize = new Size(1000, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.AppBg;
            this.Font = Theme.Body;

            ShowView(new LoginView(this));
        }

        // Não deixar fechar a janela no meio de uma gravação de produção (mesma regra
        // da troca de módulo e do "Sair": bloqueia até a gravação terminar, sem exceção,
        // para não deixar o kit em estado indefinido nem derrubar a finalização em curso).
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (ProduzirView.ProducaoEmAndamento)
            {
                e.Cancel = true;
                MessageBox.Show(this,
                    "Uma produção está sendo gravada no servidor. Aguarde terminar para fechar o programa.",
                    "Produção em andamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            base.OnFormClosing(e);
        }

        // Sessão expirou no servidor: descarta a sessão e volta para a tela de login
        // (com aviso e o usuário já preenchido — só digitar a senha de novo).
        public void SessaoExpirou()
        {
            SoulmvKit.Core.Logger.Log("Sessão expirada — voltando para a tela de login.");
            Session = null;
            ShowView(new LoginView(this, "Sua sessão expirou. Entre novamente para continuar."));
        }

        // Troca o conteúdo de topo da janela (login <-> shell)
        public void ShowView(UserControl view)
        {
            view.Dock = DockStyle.Fill;
            this.SuspendLayout();
            for (int i = this.Controls.Count - 1; i >= 0; i--)
            {
                Control old = this.Controls[i];
                this.Controls.RemoveAt(i);
                old.Dispose();
            }
            this.Controls.Add(view);
            this.ResumeLayout();
        }
    }
}

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Janela única do app. Hospeda a view de topo (login OU o shell com sidebar).
    public class MainWindow : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public string Usuario { get; set; }
        public SoulmvKit.Core.Session Session { get; set; }

        // A janela lembra como estava (maximizada ou o tamanho) entre as sessões, em
        // dados\janela.txt. Foi o que consertou o sobressalto da atualização: o app
        // reiniciava e voltava em janela pequena no lugar da maximizada — parecia bug.
        // Desligado quando o app abre com QUALQUER argumento (previews e capturas de
        // tela precisam de tamanho controlado).
        private readonly bool _lembrarJanela;

        public MainWindow(bool lembrarJanela = false)
        {
            _lembrarJanela = lembrarJanela;
            this.AutoScaleMode = AutoScaleMode.Dpi;   // escala o layout pelo DPI real (96 = base); cobre 100/125/150%
            this.Text = SoulmvKit.AppConfig.AppName;
            try { if (Assets.AppIcon != null) this.Icon = Assets.AppIcon; } catch { }
            this.ClientSize = new Size(Theme.S(1180), Theme.S(700));     // tela HD (1366x768) com folga
            this.MinimumSize = new Size(Theme.S(1000), Theme.S(640));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.AppBg;
            this.Font = Theme.Body;
            if (_lembrarJanela) RestaurarJanela();

            ShowView(new LoginView(this));
        }

        private static string CaminhoJanela()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "janela.txt");
        }

        // "maximizada" ou "normal|largura|altura" (tamanho externo da janela). O tamanho
        // é preso à área de trabalho: um arquivo vindo de monitor maior não pode abrir
        // uma janela que não cabe na tela.
        private void RestaurarJanela()
        {
            try
            {
                if (!System.IO.File.Exists(CaminhoJanela())) return;
                string s = System.IO.File.ReadAllText(CaminhoJanela()).Trim();
                if (s == "maximizada") { this.WindowState = FormWindowState.Maximized; return; }
                var p = s.Split('|');
                int w, h;
                if (p.Length == 3 && p[0] == "normal" &&
                    int.TryParse(p[1], out w) && int.TryParse(p[2], out h))
                {
                    var area = Screen.PrimaryScreen.WorkingArea;
                    w = Math.Max(this.MinimumSize.Width, Math.Min(w, area.Width));
                    h = Math.Max(this.MinimumSize.Height, Math.Min(h, area.Height));
                    this.Size = new Size(w, h);
                }
            }
            catch { }
        }

        // Grava o estado atual. Chamado no fechamento normal E antes do reinício de
        // atualização (Environment.Exit não passa pelo FormClosed).
        public void SalvarJanela()
        {
            if (!_lembrarJanela) return;
            try
            {
                string estado;
                if (this.WindowState == FormWindowState.Maximized) estado = "maximizada";
                else if (this.WindowState == FormWindowState.Normal) estado = "normal|" + this.Width + "|" + this.Height;
                else return;   // minimizada: mantém o que estava gravado
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CaminhoJanela()));
                System.IO.File.WriteAllText(CaminhoJanela(), estado);
            }
            catch { }
        }

        // O atualizador conta os estágios ("Verificando...", "Baixando...") para a tela
        // de login, se ela estiver visível; depois do login o aviso não faz sentido (a
        // atualização já aplicada vale no próximo início) e é ignorado.
        public void MostrarStatusAtualizacao(string texto)
        {
            if (IsDisposed || Disposing) return;
            var login = this.Controls.Count > 0 ? this.Controls[0] as LoginView : null;
            if (login != null) login.MostrarAtualizacao(texto);
        }

        // Windows 11: barra de título no azul da marca (a janela vira um bloco só com a
        // sidebar). No Windows 10 a chamada só retorna erro e a barra fica a nativa.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int caption = 0x00915600;   // COLORREF é BGR: #005691 -> B=91 G=56 R=00
                    int texto = 0x00FFFFFF;
                    DwmSetWindowAttribute(this.Handle, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR
                    DwmSetWindowAttribute(this.Handle, 36, ref texto, 4);     // DWMWA_TEXT_COLOR
                }
            }
            catch { }
        }

        // Atalhos globais: F5 atualiza e Ctrl+F foca a busca da tela ativa.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var shell = this.Controls.Count > 0 ? this.Controls[0] as ShellView : null;
            if (shell != null)
            {
                if (keyData == Keys.F5) { shell.ComandoAtualizar(); return true; }
                if (keyData == (Keys.Control | Keys.F)) { shell.ComandoFocarBusca(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Não deixar fechar a janela no meio de uma gravação de produção (mesma regra
        // da troca de módulo e do "Sair": bloqueia até a gravação terminar, sem exceção,
        // para não deixar o kit em estado indefinido nem derrubar a finalização em curso).
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
            {
                e.Cancel = true;
                StyledDialog.Aviso(this, "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                        ? "Uma produção está sendo gravada no servidor. Aguarde terminar para fechar o programa."
                        : "Um pedido à CAF está sendo gravado no servidor. Aguarde terminar para fechar o programa.", "Entendi");
                return;
            }
            base.OnFormClosing(e);
        }

        // Fechou: para o monitor de solicitações antes que o loop de mensagens morra (senão a
        // varredura em voo volta para um contexto inexistente e ainda escreve no cache).
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SalvarJanela();
            SolicitacoesView.PararMonitor();
            AntimicrobianosView.PararMonitor();
            JustificativasView.PararMonitor();
            base.OnFormClosed(e);
        }

        // Sessão expirou no servidor: descarta a sessão e volta para a tela de login
        // (com aviso e o usuário já preenchido — só digitar a senha de novo).
        public void SessaoExpirou()
        {
            SoulmvKit.Core.Logger.Log("Sessão expirada — voltando para a tela de login.");
            Session = null;
            SoulmvKit.Core.PepAmf.LimparCache();   // guarda registro de documento: some junto com a sessão
            // Mesma higiene do "Sair" do shell: o pré-pedido do usuário anterior não pode
            // reaparecer pronto para enviar no login do próximo. A trava de pedido enviado FICA
            // (LimparMemoria preserva) — expiração não é um jeito de liberar reenvio.
            PedidoCafView.LimparMemoria();
            EstoqueView.LimparMemoria();
            ShowView(new LoginView(this, "Sua sessão expirou. Entre novamente para continuar."));
        }

        // Troca o conteúdo de topo da janela (login <-> shell)
        public void ShowView(UserControl view)
        {
            // O monitor de solicitações liga na 1ª visita ao módulo e vive enquanto durar a
            // sessão (não enquanto a aba está aberta). Voltar ao login encerra: para o timer,
            // invalida a varredura em voo e apaga a lista da memória — ela tem nome de paciente.
            if (!(view is ShellView))
            {
                SolicitacoesView.PararMonitor();
                AntimicrobianosView.PararMonitor();
                JustificativasView.PararMonitor();
            }

            this.AcceptButton = null;   // o botão "Entrar" do login não pode sobreviver disposed
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

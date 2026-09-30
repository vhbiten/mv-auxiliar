using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Tela de login: cartão branco centralizado, com sombra suave, sobre fundo claro.
    public class LoginView : UserControl
    {
        private readonly MainWindow _host;
        private readonly Card _card;
        private LineEdit txtUser;
        private LineEdit txtPass;
        private PrimaryButton btn;
        private Label lblStatus;
        private Label lblVersao;
        private Label lblAtualizacao;
        private readonly string _aviso;

        public LoginView(MainWindow host) : this(host, null) { }

        // aviso: mensagem inicial (ex.: "Sua sessão expirou...") mostrada no rodapé do cartão.
        public LoginView(MainWindow host, string aviso)
        {
            _host = host;
            _aviso = aviso;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.AppBg;
            this.DoubleBuffered = true;

            _card = new Card();
            _card.Size = new Size(Theme.S(380), Theme.S(450));

            var logo = new PictureBox();
            logo.Image = Assets.Logo;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            logo.SetBounds(Theme.S(110), Theme.S(26), Theme.S(160), Theme.S(150));

            var sub = new Label();
            sub.Text = "Entre com seu usuário e senha";
            sub.Font = Theme.Small;
            sub.ForeColor = Theme.TextMuted;
            sub.AutoSize = false;
            sub.TextAlign = ContentAlignment.MiddleCenter;
            sub.SetBounds(Theme.S(20), Theme.S(184), Theme.S(340), Theme.S(20));

            var lblU = MakeFieldLabel("USUÁRIO", Theme.S(40), Theme.S(216));
            txtUser = new LineEdit();
            txtUser.SetBounds(Theme.S(40), Theme.S(234), Theme.S(300), Theme.S(36));
            txtUser.Casing = CharacterCasing.Upper;

            var lblP = MakeFieldLabel("SENHA", Theme.S(40), Theme.S(286));
            txtPass = new LineEdit();
            txtPass.SetBounds(Theme.S(40), Theme.S(304), Theme.S(300), Theme.S(36));
            txtPass.Password = true;

            btn = new PrimaryButton();
            btn.Text = "Entrar";
            btn.SetBounds(Theme.S(40), Theme.S(356), Theme.S(300), Theme.S(42));
            btn.Click += new EventHandler(OnEntrar);

            lblStatus = new Label();
            lblStatus.AutoSize = false;
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.ForeColor = Theme.TextMuted;
            lblStatus.Font = Theme.Small;
            lblStatus.SetBounds(Theme.S(20), Theme.S(406), Theme.S(340), Theme.S(32));

            _card.Controls.Add(logo);
            _card.Controls.Add(sub);
            _card.Controls.Add(lblU); _card.Controls.Add(txtUser);
            _card.Controls.Add(lblP); _card.Controls.Add(txtPass);
            _card.Controls.Add(btn);
            _card.Controls.Add(lblStatus);
            this.Controls.Add(_card);

            // Versão no canto da tela: com a atualização automática, é o que diz por
            // telefone se a máquina já pegou a release nova.
            lblVersao = new Label();
            lblVersao.Text = "Versão " + SoulmvKit.AppConfig.Versao;
            lblVersao.Font = Theme.Small;
            lblVersao.ForeColor = Theme.TextSoft;
            lblVersao.BackColor = Theme.AppBg;
            lblVersao.AutoSize = true;
            this.Controls.Add(lblVersao);

            // Estágio da atualização automática ("Verificando atualizações...",
            // "Baixando a versão X..."), centralizado sob o cartão. Sem ele o reinício
            // da atualização parecia um bug — a janela fechava e voltava sem explicação.
            lblAtualizacao = new Label();
            lblAtualizacao.Font = Theme.Body;
            lblAtualizacao.ForeColor = Theme.TextMuted;
            lblAtualizacao.BackColor = Theme.AppBg;
            lblAtualizacao.AutoSize = true;
            lblAtualizacao.Visible = false;
            this.Controls.Add(lblAtualizacao);

            // Ordem de tabulação: Usuário -> Senha -> Entrar
            txtUser.Inner.TabIndex = 0;
            txtPass.Inner.TabIndex = 1;
            btn.TabIndex = 2;

            // Sessão expirada: mostra o aviso e deixa o usuário preenchido (só falta a senha)
            if (_aviso != null)
            {
                lblStatus.ForeColor = Theme.Warning;
                lblStatus.Text = _aviso;
                if (!string.IsNullOrEmpty(host.Usuario)) txtUser.Text = host.Usuario;
            }

            this.Load += new EventHandler(OnLoad);
            this.Resize += new EventHandler(delegate { CenterCard(); });
        }

        private Label MakeFieldLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.Font = Theme.Small;
            l.ForeColor = Theme.TextMuted;
            l.AutoSize = false;
            l.SetBounds(x, y, Theme.S(300), Theme.S(16));
            return l;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            CenterCard();
            if (this.ParentForm != null) this.ParentForm.AcceptButton = btn; // Enter envia
            if (txtUser.Text.Length > 0) txtPass.FocusInput();   // usuário já preenchido: vai direto à senha
            else txtUser.FocusInput();
        }

        private void CenterCard()
        {
            _card.Left = (this.ClientSize.Width - _card.Width) / 2;
            _card.Top = Math.Max(16, (this.ClientSize.Height - _card.Height) / 2);
            lblVersao.Left = this.ClientSize.Width - lblVersao.Width - Theme.S(12);
            lblVersao.Top = this.ClientSize.Height - lblVersao.Height - Theme.S(8);
            PosicionarAtualizacao();
        }

        private void PosicionarAtualizacao()
        {
            lblAtualizacao.Left = (this.ClientSize.Width - lblAtualizacao.Width) / 2;
            lblAtualizacao.Top = _card.Bottom + Theme.S(16);
        }

        // Chamado pelo MainWindow com o estágio atual da atualização; vazio = esconder.
        public void MostrarAtualizacao(string texto)
        {
            if (IsDisposed || Disposing) return;
            lblAtualizacao.Text = texto ?? "";
            lblAtualizacao.Visible = lblAtualizacao.Text.Length > 0;
            PosicionarAtualizacao();
        }

        private async void OnEntrar(object sender, EventArgs e)
        {
            string user = txtUser.Text.Trim();
            string senha = txtPass.Text;
            if (user.Length == 0 || senha.Length == 0)
            {
                lblStatus.ForeColor = Theme.Error;
                lblStatus.Text = "Informe usuário e senha.";
                return;
            }
            if (!SoulmvKit.AppConfig.HostConfigurado)
            {
                lblStatus.ForeColor = Theme.Error;
                lblStatus.Text = "Servidor não configurado. Crie o arquivo config.txt ao lado do programa com a linha host=http://IP-do-servidor.";
                return;
            }

            SetBusy(true);   // o próprio botão vira "Conectando..."
            lblStatus.Text = "";

            var res = await SoulmvKit.Core.CasClient.LoginAsync(
                SoulmvKit.AppConfig.Host, user, senha, SoulmvKit.AppConfig.Company);

            if (res.Success)
            {
                _host.Session = res.Session;
                _host.Usuario = user;
                _host.ShowView(new ShellView(_host));
                return;
            }

            SetBusy(false);
            lblStatus.ForeColor = Theme.Error;
            lblStatus.Text = res.Message;
        }

        private void SetBusy(bool busy)
        {
            btn.Enabled = !busy;
            btn.Text = busy ? "Conectando..." : "Entrar";
            txtUser.Inner.Enabled = !busy;
            txtPass.Inner.Enabled = !busy;
        }
    }
}

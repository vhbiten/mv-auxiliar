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
            _card.Size = new Size(380, 450);

            var logo = new PictureBox();
            logo.Image = Assets.Logo;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            logo.SetBounds(110, 26, 160, 150);

            var sub = new Label();
            sub.Text = "Entre com seu usuário e senha";
            sub.Font = Theme.Small;
            sub.ForeColor = Theme.TextMuted;
            sub.AutoSize = false;
            sub.TextAlign = ContentAlignment.MiddleCenter;
            sub.SetBounds(20, 184, 340, 20);

            var lblU = MakeFieldLabel("USUÁRIO", 40, 216);
            txtUser = new LineEdit();
            txtUser.SetBounds(40, 234, 300, 36);
            txtUser.Casing = CharacterCasing.Upper;

            var lblP = MakeFieldLabel("SENHA", 40, 286);
            txtPass = new LineEdit();
            txtPass.SetBounds(40, 304, 300, 36);
            txtPass.Password = true;

            btn = new PrimaryButton();
            btn.Text = "Entrar";
            btn.SetBounds(40, 356, 300, 42);
            btn.Click += new EventHandler(OnEntrar);

            lblStatus = new Label();
            lblStatus.AutoSize = false;
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.ForeColor = Theme.TextMuted;
            lblStatus.Font = Theme.Small;
            lblStatus.SetBounds(20, 406, 340, 32);

            _card.Controls.Add(logo);
            _card.Controls.Add(sub);
            _card.Controls.Add(lblU); _card.Controls.Add(txtUser);
            _card.Controls.Add(lblP); _card.Controls.Add(txtPass);
            _card.Controls.Add(btn);
            _card.Controls.Add(lblStatus);
            this.Controls.Add(_card);

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
            l.SetBounds(x, y, 300, 16);
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
            this.Invalidate(); // redesenha a sombra na nova posição
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Sombra suave atrás do cartão
            Theme.DrawSoftShadow(e.Graphics, _card.Bounds, _card.Radius, 9, 9);
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

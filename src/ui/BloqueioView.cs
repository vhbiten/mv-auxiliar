using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Tela mostrada no lugar do login quando a licença não deixa o programa abrir
    // (sem chave, chave revogada ou carência offline vencida). O botão revalida na
    // hora — resolveu (colocou o licenca.txt, voltou a internet), cai no login.
    public class BloqueioView : UserControl
    {
        private readonly MainWindow _host;
        private readonly Card _card;
        private Label lblTitulo;
        private Label lblTexto;
        private PrimaryButton btn;

        public BloqueioView(MainWindow host, SoulmvKit.Core.Licenca.Estado estado)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.AppBg;
            this.DoubleBuffered = true;

            _card = new Card();
            _card.Size = new Size(Theme.S(440), Theme.S(258));

            lblTitulo = new Label();
            lblTitulo.Font = Theme.H2;
            lblTitulo.ForeColor = Theme.TextMuted;
            lblTitulo.AutoSize = false;
            lblTitulo.TextAlign = ContentAlignment.MiddleCenter;
            lblTitulo.SetBounds(Theme.S(20), Theme.S(34), Theme.S(400), Theme.S(28));

            lblTexto = new Label();
            lblTexto.Font = Theme.Body;
            lblTexto.ForeColor = Theme.TextSoft;
            lblTexto.AutoSize = false;
            lblTexto.TextAlign = ContentAlignment.MiddleCenter;
            lblTexto.SetBounds(Theme.S(20), Theme.S(74), Theme.S(400), Theme.S(88));

            btn = new PrimaryButton();
            btn.Text = "Verificar novamente";
            btn.SetBounds(Theme.S(100), Theme.S(180), Theme.S(240), Theme.S(42));
            btn.Click += new EventHandler(OnVerificar);

            _card.Controls.Add(lblTitulo);
            _card.Controls.Add(lblTexto);
            _card.Controls.Add(btn);
            this.Controls.Add(_card);

            Mostrar(estado);

            this.Load += new EventHandler(delegate { CenterCard(); });
            this.Resize += new EventHandler(delegate { CenterCard(); });
        }

        private void Mostrar(SoulmvKit.Core.Licenca.Estado estado)
        {
            lblTitulo.Text = SoulmvKit.Core.Licenca.MensagemTitulo(estado);
            lblTexto.Text = SoulmvKit.Core.Licenca.MensagemTexto(estado);
        }

        private void CenterCard()
        {
            _card.Left = (this.ClientSize.Width - _card.Width) / 2;
            _card.Top = Math.Max(16, (this.ClientSize.Height - _card.Height) / 2);
        }

        private async void OnVerificar(object sender, EventArgs e)
        {
            btn.Enabled = false;
            btn.Text = "Verificando...";
            var estado = await SoulmvKit.Core.Licenca.ValidarAsync();
            if (estado == SoulmvKit.Core.Licenca.Estado.Ok)
            {
                _host.ShowView(new LoginView(_host));
                return;
            }
            Mostrar(estado);
            btn.Enabled = true;
            btn.Text = "Verificar novamente";
        }
    }
}

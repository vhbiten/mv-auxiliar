using System;
using System.Drawing;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Página "Configurações" (rodapé da sidebar, acima de "Sair").
    //
    // Layout de lista de configurações (padrão do Windows): cada informação é UMA linha de
    // altura fixa — nome à esquerda, valor/controle à direita, todos na mesma borda direita
    // e centrados na vertical — com separador fino entre linhas. Sem legendas: o nome da
    // linha basta. A coluna tem largura máxima, para o valor não fugir para o outro lado da
    // tela com a janela maximizada.
    //   Sessão         usuário · conectado desde · manter conectado
    //   Conexão        servidor do MV (grava no config.txt; vale no próximo login)
    //   Atualizações   versão instalada · última publicada (ícone de atualizar) · instalar
    // A estação do PEP NÃO aparece: o padrão (nome da máquina) é o que funciona; continua
    // configurável no config.txt (estacao=) para o caso raro de máquina não cadastrada.
    public class ConfiguracoesView : UserControl
    {
        private const int LarguraMax = 760;   // coluna da página (base 96 DPI)
        private const int AlturaLinha = 52;

        private readonly MainWindow _host;
        private readonly TableLayoutPanel _coluna;
        private StatusBanner banner;

        private Label lblUsuario, lblDesde, lblPublicada;
        private ToggleSwitch tglManter;
        private TextBox txtServidor;
        private IconButton btnSalvar, btnVerificar;
        private PrimaryButton btnInstalar;
        private string _versaoDisponivel;

        public ConfiguracoesView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;
            // só rolagem vertical: a largura da coluna já é ajustada à área útil (AjustarLargura)
            this.HorizontalScroll.Maximum = 0;
            this.HorizontalScroll.Enabled = false;
            this.HorizontalScroll.Visible = false;
            this.AutoScroll = true;

            _coluna = new TableLayoutPanel();
            _coluna.AutoSize = true;
            _coluna.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _coluna.BackColor = Theme.ContentBg;
            _coluna.ColumnCount = 1;
            _coluna.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _coluna.Location = new Point(0, 0);
            _coluna.Margin = new Padding(0);

            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            banner.Visible = false;   // só aparece com mensagem (Avisar)
            _coluna.Controls.Add(banner);

            _coluna.Controls.Add(Secao("Sessão", LinhasSessao()));
            _coluna.Controls.Add(Secao("Conexão com o MV", LinhasConexao()));
            _coluna.Controls.Add(Secao("Atualizações", LinhasAtualizacoes()));

            this.Controls.Add(_coluna);
            this.Resize += new EventHandler(delegate { AjustarLargura(); });
            AjustarLargura();

            AtualizarSessao();
        }

        // Largura da coluna: a área útil, com teto. A linha de cada seção estica junto
        // (Dock), então a borda direita dos valores é a mesma em todas as seções.
        private void AjustarLargura()
        {
            // reserva SEMPRE a largura da barra vertical: calcular só quando ela já está
            // visível deixava a coluna 17 px larga demais no instante em que a barra surge,
            // e aparecia uma rolagem horizontal inútil (auditoria, janela mínima 1000x640)
            int util = this.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Max(Theme.S(360), Math.Min(Theme.S(LarguraMax), util));
            _coluna.MinimumSize = new Size(w, 0);
            _coluna.MaximumSize = new Size(w, 0);
        }
        private void Avisar(StatusKind tipo, string texto)
        {
            banner.Visible = true;
            banner.SetState(tipo, texto);
        }

        // ---- Blocos de layout ----

        // Cartão de seção: título + linhas empilhadas (separador fino entre elas).
        private Control Secao(string titulo, Control[] linhas)
        {
            var card = new Card();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(16));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(4));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Top;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var t = new Label();
            t.Text = titulo; t.Font = Theme.H3; t.ForeColor = Theme.TextDark; t.BackColor = Theme.CardBg;
            t.AutoSize = true; t.Margin = new Padding(0, 0, 0, Theme.S(4));
            tl.Controls.Add(t);

            for (int i = 0; i < linhas.Length; i++)
            {
                var l = linhas[i] as LinhaConfig;
                if (l != null) l.Separador = i > 0;
                tl.Controls.Add(linhas[i]);
            }
            card.Controls.Add(tl);
            return card;
        }

        private static Label Valor(string texto)
        {
            var l = new TextoSemRecuo();
            l.Text = texto;
            l.Font = Theme.Body; l.ForeColor = Theme.TextSoft; l.BackColor = Theme.CardBg;
            l.Margin = new Padding(0);
            return l;
        }

        // Label sem o recuo interno do Windows (~3–4 px de cada lado): o texto termina
        // EXATAMENTE na borda do controle, alinhado com o interruptor e os botões à direita
        // (auditoria de 18/10/2026: texto a 24 px da borda do cartão contra 20 px dos controles).
        private class TextoSemRecuo : Label
        {
            private const TextFormatFlags Fmt = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine | TextFormatFlags.Right | TextFormatFlags.VerticalCenter;

            public TextoSemRecuo() { this.AutoSize = false; this.UseMnemonic = false; }

            protected override void OnTextChanged(EventArgs e) { Medir(); base.OnTextChanged(e); }
            protected override void OnFontChanged(EventArgs e) { Medir(); base.OnFontChanged(e); }

            private void Medir()
            {
                Size s = TextRenderer.MeasureText(this.Text ?? "", this.Font, new Size(int.MaxValue, int.MaxValue), Fmt);
                this.Size = new Size(s.Width + 1, s.Height);
                this.Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var br = new SolidBrush(this.BackColor)) e.Graphics.FillRectangle(br, this.ClientRectangle);
                TextRenderer.DrawText(e.Graphics, this.Text, this.Font, this.ClientRectangle, this.ForeColor, Fmt);
            }
        }

        // ---- Sessão ----
        private Control[] LinhasSessao()
        {
            lblUsuario = Valor("—");
            lblDesde = Valor("—");

            tglManter = new ToggleSwitch();
            tglManter.Ligado = ManterSessao.Habilitado;
            tglManter.AccessibleName = "Manter conectado";
            tglManter.AlteradoPeloUsuario += new EventHandler(delegate
            {
                ManterSessao.DefinirHabilitado(tglManter.Ligado);
                Avisar(tglManter.Ligado ? StatusKind.Success : StatusKind.Info, tglManter.Ligado
                    ? "Manter conectado ligado: a sessão é renovada a cada " + ManterSessao.IntervaloMin + " minutos."
                    : "Manter conectado desligado: o MV encerra a sessão depois de cerca de 60 minutos sem uso.");
                AtualizarSessao();
            });

            return new Control[] {
                new LinhaConfig("Usuário", lblUsuario),
                new LinhaConfig("Conectado desde", lblDesde),
                new LinhaConfig("Manter conectado", tglManter)
            };
        }

        private void AtualizarSessao()
        {
            Session s = _host.Session;
            if (s == null)
            {
                lblUsuario.Text = _host.Usuario ?? "—";
                lblDesde.Text = "—";
                return;
            }
            lblUsuario.Text = (string.IsNullOrEmpty(s.NomeCompleto) ? s.User : s.NomeCompleto) + "  ·  " + s.User;
            lblDesde.Text = Quando(s.ConectadoEm);
        }

        private static string Quando(DateTime t)
        {
            return t.Date == DateTime.Today ? "hoje, " + t.ToString("HH:mm") : t.ToString("dd/MM, HH:mm");
        }

        // ---- Conexão com o MV ----
        private Control[] LinhasConexao()
        {
            var campo = new FieldHost();
            campo.Size = new Size(Theme.S(260), Theme.S(36));
            campo.Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(8), Theme.S(6));
            txtServidor = new TextBox();
            txtServidor.BorderStyle = BorderStyle.None;
            txtServidor.Font = Theme.Body;
            txtServidor.ForeColor = Theme.TextDark;
            txtServidor.Dock = DockStyle.Fill;
            txtServidor.Text = AppConfig.HostConfigurado ? AppConfig.Host : "";
            txtServidor.AccessibleName = "Servidor do MV";
            campo.Controls.Add(txtServidor);

            btnSalvar = new IconButton("", "Salvar o servidor (vale no próximo login)");   // Save
            btnSalvar.Enabled = false;
            btnSalvar.Click += new EventHandler(OnSalvar);
            txtServidor.TextChanged += new EventHandler(delegate
            {
                string atual = AppConfig.HostConfigurado ? AppConfig.Host : "";
                btnSalvar.Enabled = txtServidor.Text.Trim().TrimEnd('/') != atual;
            });
            txtServidor.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && btnSalvar.Enabled) { e.SuppressKeyPress = true; OnSalvar(s, e); }
            });

            return new Control[] { new LinhaConfig("Servidor do MV", campo, btnSalvar) };
        }

        private void OnSalvar(object sender, EventArgs e)
        {
            string host = (txtServidor.Text ?? "").Trim().TrimEnd('/');
            bool url = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       host.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!url || host.Length < 10)
            {
                Avisar(StatusKind.Error, "Servidor inválido. Use o endereço completo, começando com http:// ou https://.");
                return;
            }
            try
            {
                AppConfig.SalvarConfig("host", host);
                txtServidor.Text = host;
                btnSalvar.Enabled = false;
                Avisar(StatusKind.Success, "Servidor salvo. Vale a partir do próximo login.");
            }
            catch (Exception ex)
            {
                Logger.Log("Configurações: falha ao gravar config.txt: " + ex.Message);
                Avisar(StatusKind.Error, "Não foi possível gravar o config.txt: " + ex.Message);
            }
        }

        // ---- Atualizações ----
        private Control[] LinhasAtualizacoes()
        {
            lblPublicada = Valor("");   // vazio até verificar (sem traço ao lado do ícone)

            btnVerificar = new IconButton("", "Verificar atualizações");   // Refresh
            btnVerificar.Click += new EventHandler(OnVerificar);

            btnInstalar = new PrimaryButton();
            btnInstalar.Text = "Instalar";
            btnInstalar.Size = new Size(Theme.S(110), Theme.S(36));
            btnInstalar.Visible = false;
            btnInstalar.Click += new EventHandler(OnInstalar);

            return new Control[] {
                new LinhaConfig("Versão instalada", Valor(AppConfig.Versao)),
                new LinhaConfig("Última versão publicada", lblPublicada, btnInstalar, btnVerificar)
            };
        }

        private async void OnVerificar(object sender, EventArgs e)
        {
            btnVerificar.Enabled = false;
            btnInstalar.Visible = false;
            lblPublicada.Text = "verificando…";
            string v = await AtualizadorApp.ConsultarVersaoAsync();
            if (IsDisposed) return;
            btnVerificar.Enabled = true;
            if (v == null)
            {
                lblPublicada.Text = "sem conexão";
                Avisar(StatusKind.Warning, "Não foi possível consultar as atualizações agora (sem internet ou serviço fora do ar).");
                return;
            }
            if (AtualizadorApp.EhMaisNova(v))
            {
                _versaoDisponivel = v;
                lblPublicada.Text = v + " disponível";
                btnInstalar.Visible = true;
            }
            else lblPublicada.Text = "A última atualização disponível já está instalada";
        }

        private async void OnInstalar(object sender, EventArgs e)
        {
            if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
            {
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento",
                    "Aguarde a gravação terminar antes de instalar a atualização.", "Entendi");
                return;
            }
            if (!StyledDialog.Confirmar(this.FindForm(), "Instalar atualização",
                    "Instalar a versão " + _versaoDisponivel + " agora? O programa vai fechar, reabrir e pedir o login de novo.",
                    "Instalar", "Agora não"))
                return;
            btnInstalar.Enabled = false; btnVerificar.Enabled = false;
            string nova = await AtualizadorApp.AtualizarAsync(delegate(string s) { if (!IsDisposed && s.Length > 0) lblPublicada.Text = s.ToLowerInvariant(); });
            if (IsDisposed) return;
            if (nova == null)
            {
                btnInstalar.Enabled = true; btnVerificar.Enabled = true;
                Avisar(StatusKind.Error, "A instalação não foi concluída. O programa continua na versão " + AppConfig.Versao + ".");
                return;
            }
            lblPublicada.Text = "reabrindo…";
            _host.SalvarJanela();
            AtualizadorApp.Reiniciar();
        }

        // ---- Linha de configuração ----
        // Nome à esquerda; à direita, os controles da direita para a esquerda (o último
        // passado fica colado na borda). Todos centrados na vertical numa altura fixa.
        private class LinhaConfig : TableLayoutPanel
        {
            private bool _separador;

            public bool Separador { set { _separador = value; this.Invalidate(); } }

            public LinhaConfig(string nome, params Control[] direita)
            {
                this.Dock = DockStyle.Top;
                this.Height = Theme.S(AlturaLinha);
                this.Margin = new Padding(0);
                this.Padding = new Padding(0);
                this.BackColor = Theme.CardBg;
                this.ColumnCount = 1 + direita.Length;
                this.RowCount = 1;
                this.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int i = 0; i < direita.Length; i++) this.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                this.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                this.SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

                var lbl = new Label();
                lbl.Text = nome;
                lbl.Font = Theme.Body; lbl.ForeColor = Theme.TextDark; lbl.BackColor = Theme.CardBg;
                lbl.AutoSize = true; lbl.UseMnemonic = false;
                lbl.Anchor = AnchorStyles.Left;          // sem Top/Bottom = centrado na vertical
                lbl.Margin = new Padding(0, 0, Theme.S(16), 0);
                this.Controls.Add(lbl, 0, 0);

                for (int i = 0; i < direita.Length; i++)
                {
                    Control c = direita[i];
                    c.Anchor = AnchorStyles.Right;
                    // vão de 8 entre controles; o último encosta na borda direita
                    c.Margin = new Padding(0, 0, i < direita.Length - 1 ? Theme.S(8) : 0, 0);
                    this.Controls.Add(c, 1 + i, 0);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!_separador) return;
                using (var pen = new Pen(Theme.GridLine))
                    e.Graphics.DrawLine(pen, 0, 0, this.Width, 0);
            }
        }
    }
}

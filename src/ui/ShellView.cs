using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Shell principal: sidebar teal (navegação entre módulos) + área de conteúdo branca.
    // Novos módulos no futuro = adicionar um NavButton + um ShowModule().
    public class ShellView : UserControl
    {
        private readonly MainWindow _host;
        private Panel _content;
        private Label _moduleTitle;
        private UserControl _viewAtual;
        private NavButton _navKits;
        private NavButton _navConf;
        private NavButton _navConsulta;
        private NavButton _navSolic;
        private NavButton _navPedido;
        private NavButton _navAtb;
        private NavButton _navJust;
        private NavButton _navEstoque;
        private System.Collections.Generic.List<NavButton> _navs = new System.Collections.Generic.List<NavButton>();

        public ShellView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;

            // Layout raiz: coluna sidebar (210) + coluna conteúdo (resto)
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(210)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            root.Controls.Add(BuildSidebar(), 0, 0);
            root.Controls.Add(BuildContent(), 1, 0);

            this.Controls.Add(root);

            SelectKits(); // módulo inicial
        }

        // ---- Sidebar ----
        private Control BuildSidebar()
        {
            var side = new TableLayoutPanel();
            side.Dock = DockStyle.Fill;
            side.BackColor = Theme.Primary;
            side.ColumnCount = 1;
            side.RowCount = 3;
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(96)));   // marca (faixa branca)
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // nav
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(76)));   // rodapé (usuário/sair)

            // Marca: faixa branca (largura toda) com favicon + "AUXILIAR".
            // TableLayoutPanel (não coordenadas absolutas) para preencher a faixa e escalar com o DPI.
            var brand = new TableLayoutPanel();
            brand.Dock = DockStyle.Fill;
            brand.BackColor = Color.White;
            brand.Padding = new Padding(Theme.S(16), 0, Theme.S(8), 0);
            brand.ColumnCount = 2;
            brand.RowCount = 1;
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(50)));
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var pic = new PictureBox();
            pic.Image = Assets.Favicon;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.White;
            pic.Dock = DockStyle.Fill;
            pic.Margin = new Padding(0, Theme.S(22), 0, Theme.S(22));

            var marca = new Label();
            marca.Text = "AUXILIAR";
            marca.Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold);
            marca.ForeColor = Theme.Primary;
            marca.Dock = DockStyle.Fill;
            marca.TextAlign = ContentAlignment.MiddleLeft;
            marca.AutoEllipsis = true;       // se faltar espaço, trunca em 1 linha (nunca quebra feio)
            marca.UseMnemonic = false;
            marca.Padding = new Padding(Theme.S(10), 0, 0, 0);

            brand.Controls.Add(pic, 0, 0);
            brand.Controls.Add(marca, 1, 0);
            side.Controls.Add(brand, 0, 0);

            // Navegação
            var nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Fill;
            nav.FlowDirection = FlowDirection.TopDown;
            nav.WrapContents = false;
            nav.BackColor = Theme.Primary;
            nav.Padding = new Padding(0, Theme.S(6), 0, 0);

            _navKits = new NavButton("Produção de Kits");
            _navKits.Selected += new EventHandler(delegate { Selecionar(_navKits, "Produção de Kits", new ProduzirView(_host)); });

            _navConf = new NavButton("Conferência de Lotes");
            _navConf.Selected += new EventHandler(delegate { Selecionar(_navConf, "Conferência de Lotes", new ConferenciaView(_host)); });

            _navConsulta = new NavButton("Consultar kits");
            _navConsulta.Selected += new EventHandler(delegate { Selecionar(_navConsulta, "Consultar kits", new ConsultarKitsView(_host)); });

            _navSolic = new NavButton("Solicitações");
            _navSolic.Selected += new EventHandler(delegate { Selecionar(_navSolic, "Solicitações", new SolicitacoesView(_host)); });

            _navAtb = new NavButton("Antimicrobianos");
            _navAtb.Selected += new EventHandler(delegate { Selecionar(_navAtb, "Antimicrobianos", new AntimicrobianosView(_host)); });

            _navJust = new NavButton("Justificativas");
            _navJust.Selected += new EventHandler(delegate { Selecionar(_navJust, "Justificativas", new JustificativasView(_host)); });

            // Reposição (Previsão de Consumo e Estoque) é decisão de farmacêutico. Para o
            // atendente de farmácia os dois botões não chegam a existir — assim não há
            // tela para alcançar por Tab nem por atalho, e nenhum item cinza sobrando no
            // menu. Ver Permissoes.
            if (SoulmvKit.Core.Permissoes.PodeVerReposicao(_host.Usuario))
            {
                _navPedido = new NavButton("Previsão de Consumo");
                _navPedido.Selected += new EventHandler(delegate { Selecionar(_navPedido, "Previsão de Consumo", new PedidoCafView(_host)); });

                _navEstoque = new NavButton("Estoque");
                _navEstoque.Selected += new EventHandler(delegate { Selecionar(_navEstoque, "Estoque", new EstoqueView(_host)); });
            }

            _navs.Add(_navKits); _navs.Add(_navConf); _navs.Add(_navConsulta); _navs.Add(_navSolic); _navs.Add(_navAtb); _navs.Add(_navJust);
            if (_navPedido != null) _navs.Add(_navPedido);
            if (_navEstoque != null) _navs.Add(_navEstoque);

            // Ordem do menu: os dois módulos de KIT ficam juntos (produzir e consultar), depois
            // conferência e solicitações, os dois de antimicrobiano juntos (a lista de uso e as
            // justificativas se leem em par) e, por último, o par de reposição: Previsão de
            // Consumo (Central pede à CAF) e Estoque (CAF decide o que comprar).
            nav.Controls.Add(_navKits);
            nav.Controls.Add(_navConsulta);
            nav.Controls.Add(_navConf);
            nav.Controls.Add(_navSolic);
            nav.Controls.Add(_navAtb);
            nav.Controls.Add(_navJust);
            if (_navPedido != null) nav.Controls.Add(_navPedido);
            if (_navEstoque != null) nav.Controls.Add(_navEstoque);
            side.Controls.Add(nav, 0, 1);

            // Rodapé: usuário (ícone de perfil + nome) + sair (ícone de desligar + "Sair")
            var footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Theme.NavActive;
            footer.ColumnCount = 2;
            footer.RowCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(38)));   // ícone
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // texto
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            // linha do usuário — só o primeiro nome (ex.: "VICTOR")
            string nomeUsuario = _host.Usuario;
            if (_host.Session != null && !string.IsNullOrEmpty(_host.Session.NomeCompleto))
            {
                nomeUsuario = _host.Session.NomeCompleto.Trim();
                int esp = nomeUsuario.IndexOf(' ');
                if (esp > 0) nomeUsuario = nomeUsuario.Substring(0, esp);
            }
            var icUser = RodapeIcone("");   // Contact (pessoa)
            var lblUser = new Label();
            lblUser.Text = nomeUsuario;
            lblUser.Font = Theme.BodyBold;
            lblUser.ForeColor = Color.White;
            lblUser.BackColor = Theme.NavActive;
            lblUser.Dock = DockStyle.Fill;
            lblUser.TextAlign = ContentAlignment.MiddleLeft;
            lblUser.AutoEllipsis = true;   // nome longo trunca com "…"
            lblUser.Padding = new Padding(0, 0, 8, 0);
            footer.Controls.Add(icUser, 0, 0);
            footer.Controls.Add(lblUser, 1, 0);

            // linha do sair — um ÚNICO painel (ícone + "Sair") p/ o realce cobrir a linha
            // inteira; selecionável para o teclado alcançar (Tab + Enter).
            var sairRow = new PainelAcionavel();
            sairRow.Dock = DockStyle.Fill;
            sairRow.BackColor = Theme.NavActive;
            sairRow.Cursor = Cursors.Hand;

            var sairTl = new TableLayoutPanel();
            sairTl.Dock = DockStyle.Fill;
            sairTl.BackColor = Theme.NavActive;
            sairTl.ColumnCount = 2; sairTl.RowCount = 1;
            sairTl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(38)));
            sairTl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sairTl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var icSair = RodapeIcone("");   // PowerButton (desligar)
            var lblSair = new Label();
            lblSair.Text = "Sair";
            lblSair.Font = Theme.Body;
            lblSair.ForeColor = Color.White;
            lblSair.BackColor = Theme.NavActive;
            lblSair.Dock = DockStyle.Fill;
            lblSair.TextAlign = ContentAlignment.MiddleLeft;
            sairTl.Controls.Add(icSair, 0, 0);
            sairTl.Controls.Add(lblSair, 1, 0);
            sairRow.Controls.Add(sairTl);

            EventHandler sair = new EventHandler(delegate
            {
                if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
                {
                    StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                            ? "Aguarde a produção terminar antes de sair."
                            : "Aguarde o pedido à CAF terminar de ser enviado antes de sair.", "Entendi");
                    return;
                }
                // O pré-pedido em memória é do usuário que está saindo — não pode reaparecer
                // (pronto para enviar) no login do próximo. A trava do pedido enviado FICA.
                PedidoCafView.LimparMemoria();
                EstoqueView.LimparMemoria();   // a análise em disco fica (só dados de produto)
                _host.Usuario = null;
                _host.ShowView(new LoginView(_host));
            });
            EventHandler entra = new EventHandler(delegate
            {
                sairRow.BackColor = Theme.Primary; sairTl.BackColor = Theme.Primary;
                icSair.BackColor = Theme.Primary; lblSair.BackColor = Theme.Primary;
            });
            EventHandler sai = new EventHandler(delegate
            {
                var pt = sairRow.PointToClient(Cursor.Position);
                if (sairRow.ClientRectangle.Contains(pt)) return;
                sairRow.BackColor = Theme.NavActive; sairTl.BackColor = Theme.NavActive;
                icSair.BackColor = Theme.NavActive; lblSair.BackColor = Theme.NavActive;
            });
            Control[] alvos = new Control[] { sairRow, sairTl, icSair, lblSair };
            foreach (Control c in alvos) { c.MouseEnter += entra; c.MouseLeave += sai; c.Click += sair; }
            sairRow.Acionado += sair;   // Enter/Espaço quando focado por Tab
            sairRow.GotFocus += entra;  // foco por teclado usa o mesmo realce do hover
            sairRow.LostFocus += sai;

            footer.Controls.Add(sairRow, 0, 1);
            footer.SetColumnSpan(sairRow, 2);
            side.Controls.Add(footer, 0, 2);

            return side;
        }

        // Ícone (glifo Segoe MDL2) do rodapé da sidebar.
        private Label RodapeIcone(string glyph)
        {
            var l = new Label();
            l.Text = glyph;
            l.Font = Theme.Icon;
            l.ForeColor = Color.White;
            l.BackColor = Theme.NavActive;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            return l;
        }

        // ---- Conteúdo (header + corpo) ----
        private Control BuildContent()
        {
            var wrap = new TableLayoutPanel();
            wrap.Dock = DockStyle.Fill;
            wrap.BackColor = Theme.ContentBg;
            wrap.ColumnCount = 1;
            wrap.RowCount = 2;
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(64)));  // header
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // corpo

            var header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Theme.CardBg;   // barra de título branca (app-bar) sobre o corpo cinza-azulado
            header.Paint += new PaintEventHandler(delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            });

            _moduleTitle = new Label();
            _moduleTitle.Font = Theme.H2;
            _moduleTitle.ForeColor = Theme.TextDark;
            _moduleTitle.Dock = DockStyle.Fill;
            _moduleTitle.TextAlign = ContentAlignment.MiddleLeft;
            _moduleTitle.Padding = new Padding(Theme.S(28), 0, 0, 0);
            header.Controls.Add(_moduleTitle);

            _content = new Panel();
            _content.Dock = DockStyle.Fill;
            _content.BackColor = Theme.ContentBg;
            _content.Padding = new Padding(Theme.S(28), Theme.S(22), Theme.S(28), Theme.S(22));
            _content.AutoScroll = true;   // se a janela ficar muito baixa, rola em vez de cortar

            wrap.Controls.Add(header, 0, 0);
            wrap.Controls.Add(_content, 0, 1);
            return wrap;
        }

        private void ShowModule(string titulo, UserControl view)
        {
            _moduleTitle.Text = titulo;
            view.Dock = DockStyle.Fill;
            _content.SuspendLayout();
            for (int i = _content.Controls.Count - 1; i >= 0; i--)
            {
                Control old = _content.Controls[i];
                _content.Controls.RemoveAt(i);
                old.Dispose();
            }
            _content.Controls.Add(view);
            _content.ResumeLayout();
            _viewAtual = view;
        }

        // Atalhos globais (F5 / Ctrl+F) roteados pela MainWindow para a tela ativa.
        public void ComandoAtualizar()
        {
            var c = _viewAtual as IComandosDeTela;
            if (c != null) c.ComandoAtualizar();
        }

        public void ComandoFocarBusca()
        {
            var c = _viewAtual as IComandosDeTela;
            if (c != null) c.ComandoFocarBusca();
        }

        private void SelectKits()
        {
            Selecionar(_navKits, "Produção de Kits", new ProduzirView(_host));
        }

        // usado por flag de desenvolvimento (--conf-preview)
        public void IrParaConferencia()
        {
            Selecionar(_navConf, "Conferência de Lotes", new ConferenciaView(_host));
        }

        // usado por flag de desenvolvimento (--consulta)
        public void IrParaConsulta()
        {
            Selecionar(_navConsulta, "Consultar kits", new ConsultarKitsView(_host));
        }

        // usado por flag de desenvolvimento (--solic-preview)
        public void IrParaSolicitacoes()
        {
            Selecionar(_navSolic, "Solicitações", new SolicitacoesView(_host));
        }

        // usado por flag de desenvolvimento (--pedido-preview)
        public void IrParaJustificativas()
        {
            Selecionar(_navJust, "Justificativas", new JustificativasView(_host));
        }

        public void IrParaAntimicrobianos()
        {
            Selecionar(_navAtb, "Antimicrobianos", new AntimicrobianosView(_host));
        }

        // Os dois módulos de reposição não existem na sessão de um atendente: sem botão
        // no menu, o atalho de desenvolvimento também não abre a tela.
        public void IrParaEstoque()
        {
            if (_navEstoque == null) return;
            Selecionar(_navEstoque, "Estoque", new EstoqueView(_host));
        }

        public void IrParaPedidoCaf()
        {
            if (_navPedido == null) return;
            Selecionar(_navPedido, "Previsão de Consumo", new PedidoCafView(_host));
        }

        // Ativa o item de menu escolhido (desativa os outros) e troca o módulo.
        private void Selecionar(NavButton nav, string titulo, UserControl view)
        {
            // Trava de segurança: não trocar de módulo (nem descartar a view) enquanto
            // uma produção está sendo gravada no servidor — evita criar kit duplicado
            // e o crash por descartar a view durante o await da finalização.
            if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
            {
                view.Dispose();
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                        ? "Aguarde a produção terminar antes de trocar de módulo."
                        : "Aguarde o pedido à CAF terminar de ser enviado antes de trocar de módulo.", "Entendi");
                return;
            }
            foreach (var nb in _navs) nb.Active = (nb == nav);
            ShowModule(titulo, view);
        }

        // Panel que participa do Tab e dispara Acionado com Enter/Espaço (o "Sair").
        private class PainelAcionavel : Panel
        {
            public event EventHandler Acionado;

            public PainelAcionavel()
            {
                this.SetStyle(ControlStyles.Selectable, true);
                this.TabStop = true;
            }

            protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    if (Acionado != null) Acionado(this, EventArgs.Empty);
                    return;
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (this.Focused)
                    using (var pen = new Pen(Color.White, 1f))
                        e.Graphics.DrawRectangle(pen, 2, 2, this.Width - 5, this.Height - 5);
            }
        }
    }
}

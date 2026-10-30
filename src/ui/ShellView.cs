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
        private NavButton _navPam;
        private NavButton _navAtender;
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
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(108)));   // rodapé (usuário/configurações/sair)

            // Marca: faixa branca com símbolo + "AUXILIAR" centralizados como um bloco (BrandHeader).
            var brand = new BrandHeader();
            brand.Dock = DockStyle.Fill;
            side.Controls.Add(brand, 0, 0);

            // Navegação
            var nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Fill;
            nav.FlowDirection = FlowDirection.TopDown;
            nav.WrapContents = false;
            nav.BackColor = Theme.Primary;
            nav.Padding = new Padding(0, Theme.S(6), 0, 0);
            // Janela baixa com todos os grupos abertos: rola na vertical em vez de esconder
            // o último item atrás do rodapé. A largura dos itens acompanha a área útil para
            // a barra de rolagem não puxar uma horizontal junto.
            // Só vertical: desliga a horizontal ANTES de ligar o AutoScroll (sem isso o
            // WinForms mostrava as duas barras na janela mínima).
            nav.HorizontalScroll.Maximum = 0;
            nav.HorizontalScroll.Enabled = false;
            nav.HorizontalScroll.Visible = false;
            nav.AutoScroll = true;
            nav.ClientSizeChanged += new EventHandler(delegate { AjustarLarguras(nav); });

            _navKits = new NavButton("Produção de Kits");
            _navKits.Selected += new EventHandler(delegate { Selecionar(_navKits, "Produção de Kits", new ProduzirView(_host)); });

            _navConf = new NavButton("Conferência de Lotes");
            _navConf.Selected += new EventHandler(delegate { Selecionar(_navConf, "Conferência de Lotes", new ConferenciaView(_host)); });

            _navConsulta = new NavButton("Consultar kits");
            _navConsulta.Selected += new EventHandler(delegate { Selecionar(_navConsulta, "Consultar kits", new ConsultarKitsView(_host)); });

            _navSolic = new NavButton("Solicitações");
            _navSolic.Selected += new EventHandler(delegate { Selecionar(_navSolic, "Solicitações", new SolicitacoesView(_host)); });

            // Atender uma solicitação: bipa o nº, os itens pedidos vêm prontos; lote e quantidade.
            _navAtender = new NavButton("Atender");
            _navAtender.Selected += new EventHandler(delegate { Selecionar(_navAtender, "Atender solicitação", new AtenderView(_host)); });

            // Pacientes do PAM e a situação da prescrição de cada um (para levar tudo de uma vez).
            _navPam = new NavButton("Urgência (PAM)");
            _navPam.Selected += new EventHandler(delegate { Selecionar(_navPam, "Urgência (PAM)", new EntregasPamView(_host)); });

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

                _navEstoque = new NavButton("Compras CAF");
                _navEstoque.Selected += new EventHandler(delegate { Selecionar(_navEstoque, "Compras CAF", new EstoqueView(_host)); });
            }

            _navs.Add(_navKits); _navs.Add(_navConf); _navs.Add(_navConsulta); _navs.Add(_navAtender); _navs.Add(_navSolic); _navs.Add(_navPam); _navs.Add(_navAtb); _navs.Add(_navJust);
            if (_navPedido != null) _navs.Add(_navPedido);
            if (_navEstoque != null) _navs.Add(_navEstoque);

            // Menu em grupos recolhíveis (o estado aberto/fechado fica em dados\menu.txt):
            //   KITS: produzir e consultar;
            //   SOLICITAÇÕES: Atender primeiro (é o que o atendente mais usa);
            //   ANTIMICROBIANOS: a lista de uso e as justificativas se leem em par;
            //   ESTOQUE: Conferência de Lotes (físico x eletrônico) e o par de reposição —
            //   Previsão de Consumo (Central pede à CAF) e Compras CAF (a CAF decide o que comprar;
            //   era a aba "Estoque", renomeada para não repetir o nome do grupo).
            // Para o atendente o grupo ESTOQUE fica só com a Conferência (ver Permissoes).
            _recolhidos = CarregarRecolhidos();
            AdicionarGrupo(nav, "kits", "Kits", "\uE7B8", _navKits, _navConsulta);              // caixa
            AdicionarGrupo(nav, "solicitacoes", "Solicitações", "\uEADF", _navAtender, _navSolic, _navPam);     // prancheta
            AdicionarGrupo(nav, "antimicrobianos", "Antimicrobianos", null, _navAtb, _navJust);    // cápsula (desenhada)
            AdicionarGrupo(nav, "estoque", "Estoque", "\uE81E", _navConf, _navPedido, _navEstoque);  // camadas
            AjustarLarguras(nav);
            side.Controls.Add(nav, 0, 1);

            // Rodapé: usuário (perfil + nome) · Configurações (engrenagem) · Sair (desligar)
            var footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Theme.NavActive;
            footer.ColumnCount = 2;
            footer.RowCount = 3;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(38)));   // ícone
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // texto
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

            // linha do usuário — só o primeiro nome (ex.: "VICTOR")
            string nomeUsuario = _host.Usuario;
            if (_host.Session != null && !string.IsNullOrEmpty(_host.Session.NomeCompleto))
            {
                nomeUsuario = _host.Session.NomeCompleto.Trim();
                int esp = nomeUsuario.IndexOf(' ');
                if (esp > 0) nomeUsuario = nomeUsuario.Substring(0, esp);
            }
            var icUser = RodapeIcone("\uE77B");   // Contact (pessoa)
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

            // Configurações: abre a página no lugar do módulo atual (como as outras telas)
            _cfgRow = LinhaAcao("\uE713", "Configurações", new EventHandler(delegate { AbrirConfiguracoes(); }));
            footer.Controls.Add(_cfgRow, 0, 1);
            footer.SetColumnSpan(_cfgRow, 2);

            EventHandler sair = new EventHandler(delegate
            {
                if (AtenderView.GravacaoEmAndamento)
                {
                    StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", "Aguarde a baixa da solicitação terminar de ser gravada no MV antes de sair.", "Entendi");
                    return;
                }
                if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
                {
                    StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                            ? "Aguarde a produção terminar antes de sair."
                            : "Aguarde o pedido à CAF terminar de ser enviado antes de sair.", "Entendi");
                    return;
                }
                if (!PodeSairDoModulo()) return;
                // O pré-pedido em memória é do usuário que está saindo — não pode reaparecer
                // (pronto para enviar) no login do próximo. A trava do pedido enviado FICA.
                PedidoCafView.LimparMemoria();
                EstoqueView.LimparMemoria();   // a análise em disco fica (só dados de produto)
                _host.Usuario = null;
                _host.ShowView(new LoginView(_host));
            });
            var sairRow = LinhaAcao("\uE7E8", "Sair", sair);   // PowerButton (desligar)
            footer.Controls.Add(sairRow, 0, 2);
            footer.SetColumnSpan(sairRow, 2);
            side.Controls.Add(footer, 0, 2);

            return side;
        }

        // Linha clicável do rodapé (ícone + texto) — um ÚNICO painel para o realce cobrir a
        // linha inteira; selecionável para o teclado alcançar (Tab + Enter/Espaço).
        private PainelAcionavel LinhaAcao(string glifo, string texto, EventHandler acao)
        {
            var row = new PainelAcionavel();
            row.Dock = DockStyle.Fill;
            row.Margin = new Padding(0);
            row.BackColor = Theme.NavActive;
            row.Cursor = Cursors.Hand;

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.NavActive;
            tl.ColumnCount = 2; tl.RowCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(38)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var ic = RodapeIcone(glifo);
            var lbl = new Label();
            lbl.Text = texto;
            lbl.Font = Theme.Body;
            lbl.ForeColor = Color.White;
            lbl.BackColor = Theme.NavActive;
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.UseMnemonic = false;
            tl.Controls.Add(ic, 0, 0);
            tl.Controls.Add(lbl, 1, 0);
            row.Controls.Add(tl);

            Control[] partes = new Control[] { row, tl, ic, lbl };
            EventHandler entra = new EventHandler(delegate { foreach (Control c in partes) c.BackColor = Theme.Primary; });
            EventHandler sai = new EventHandler(delegate
            {
                var pt = row.PointToClient(Cursor.Position);
                if (row.ClientRectangle.Contains(pt) || row.Focused) return;
                foreach (Control c in partes) c.BackColor = Theme.NavActive;
            });
            foreach (Control c in partes) { c.MouseEnter += entra; c.MouseLeave += sai; c.Click += acao; }
            row.Acionado += acao;      // Enter/Espaço quando focado por Tab
            row.GotFocus += entra;     // foco por teclado usa o mesmo realce do hover
            row.LostFocus += sai;
            return row;
        }

        private PainelAcionavel _cfgRow;

        // Configurações: página no lugar do módulo atual; nenhuma aba fica ativa.
        public void AbrirConfiguracoes()
        {
            if (AtenderView.GravacaoEmAndamento)
            {
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", "Aguarde a baixa da solicitação terminar de ser gravada no MV antes de trocar de tela.", "Entendi");
                return;
            }
            if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
            {
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                        ? "Aguarde a produção terminar antes de trocar de tela."
                        : "Aguarde o pedido à CAF terminar de ser enviado antes de trocar de tela.", "Entendi");
                return;
            }
            if (!PodeSairDoModulo()) return;
            foreach (var nb in _navs) nb.Active = false;
            foreach (var g in _grupos) g.Titulo.ContemAtivo = false;
            ShowModule("Configurações", new ConfiguracoesView(_host));
        }

        // A aba Atender com lotes digitados pergunta antes de descartar (sair dela solta a
        // solicitação reservada no MV).
        private bool PodeSairDoModulo()
        {
            var atender = _viewAtual as AtenderView;
            return atender == null || atender.PodeSair();
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

        // usado por flag de desenvolvimento (--atender-preview)
        public void IrParaAtender()
        {
            Selecionar(_navAtender, "Atender solicitação", new AtenderView(_host));
        }

        // usado por flag de desenvolvimento (--pam-preview)
        public void IrParaEntregasPam()
        {
            Selecionar(_navPam, "Urgência (PAM)", new EntregasPamView(_host));
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
            Selecionar(_navEstoque, "Compras CAF", new EstoqueView(_host));
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
            if (AtenderView.GravacaoEmAndamento)
            {
                view.Dispose();
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", "Aguarde a baixa da solicitação terminar de ser gravada no MV antes de trocar de módulo.", "Entendi");
                return;
            }
            if (ProduzirView.ProducaoEmAndamento || PedidoCafView.EnvioEmAndamento)
            {
                view.Dispose();
                StyledDialog.Aviso(this.FindForm(), "Gravação em andamento", ProduzirView.ProducaoEmAndamento
                        ? "Aguarde a produção terminar antes de trocar de módulo."
                        : "Aguarde o pedido à CAF terminar de ser enviado antes de trocar de módulo.", "Entendi");
                return;
            }
            if (!PodeSairDoModulo()) { view.Dispose(); return; }
            foreach (var nb in _navs) nb.Active = (nb == nav);
            foreach (var g in _grupos)
            {
                bool contem = g.Itens.Contains(nav);
                g.Titulo.ContemAtivo = contem;
                // módulo aberto por atalho/flag dentro de um grupo fechado: abre o grupo
                if (contem && !g.Titulo.Expanded) DefinirAberto(g, true);
            }
            ShowModule(titulo, view);
        }

        // ---- Grupos recolhíveis da sidebar ----
        private class GrupoNav
        {
            public string Chave;
            public NavGroupHeader Titulo;
            public readonly System.Collections.Generic.List<NavButton> Itens = new System.Collections.Generic.List<NavButton>();
        }

        private readonly System.Collections.Generic.List<GrupoNav> _grupos = new System.Collections.Generic.List<GrupoNav>();
        private System.Collections.Generic.HashSet<string> _recolhidos = new System.Collections.Generic.HashSet<string>();

        // Cria o título do grupo e coloca os itens embaixo. Item nulo (módulo sem
        // permissão para o usuário) é ignorado; grupo sem nenhum item não aparece.
        private void AdicionarGrupo(FlowLayoutPanel nav, string chave, string titulo, string glifo, params NavButton[] itens)
        {
            var g = new GrupoNav();
            g.Chave = chave;
            foreach (var it in itens) if (it != null) g.Itens.Add(it);
            if (g.Itens.Count == 0) return;

            g.Titulo = glifo != null ? new NavGroupHeader(titulo, glifo)
                : new NavGroupHeader(titulo, new Action<Graphics, Rectangle, Color>(NavGroupHeader.DesenharCapsula));
            g.Titulo.Primeiro = (_grupos.Count == 0);
            g.Titulo.Toggled += new EventHandler(delegate
            {
                DefinirAberto(g, !g.Titulo.Expanded);
            });
            nav.Controls.Add(g.Titulo);
            foreach (var it in g.Itens) nav.Controls.Add(it);
            _grupos.Add(g);

            bool aberto = !_recolhidos.Contains(chave);
            g.Titulo.Expanded = aberto;
            foreach (var it in g.Itens) it.Visible = aberto;
        }

        private static void AjustarLarguras(FlowLayoutPanel nav)
        {
            int w = nav.ClientSize.Width;
            if (w <= 0) return;
            foreach (Control c in nav.Controls) if (c.Width != w) c.Width = w;
        }

        private void DefinirAberto(GrupoNav g, bool aberto)
        {
            g.Titulo.Expanded = aberto;
            foreach (var it in g.Itens) it.Visible = aberto;
            if (aberto) _recolhidos.Remove(g.Chave); else _recolhidos.Add(g.Chave);
            SalvarRecolhidos();
        }

        private static string CaminhoMenu()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "menu.txt");
        }

        // Uma chave de grupo fechado por linha. Arquivo ausente = tudo aberto.
        private static System.Collections.Generic.HashSet<string> CarregarRecolhidos()
        {
            var h = new System.Collections.Generic.HashSet<string>();
            try
            {
                if (System.IO.File.Exists(CaminhoMenu()))
                    foreach (string l in System.IO.File.ReadAllLines(CaminhoMenu()))
                        if (l.Trim().Length > 0) h.Add(l.Trim());
            }
            catch { }
            return h;
        }

        private void SalvarRecolhidos()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CaminhoMenu()));
                System.IO.File.WriteAllLines(CaminhoMenu(), new System.Collections.Generic.List<string>(_recolhidos).ToArray());
            }
            catch { }
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

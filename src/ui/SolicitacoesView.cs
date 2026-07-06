using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Item genérico de combo (rótulo + valor).
    internal class OpcaoItem
    {
        public string Nome; public string Valor;
        public OpcaoItem(string nome, string valor) { Nome = nome; Valor = valor; }
        public override string ToString() { return Nome; }
    }
    internal class PeriodoItem
    {
        public string Nome; public int Dias;   // dias para trás (0 = hoje)
        public PeriodoItem(string nome, int dias) { Nome = nome; Dias = dias; }
        public override string ToString() { return Nome; }
    }

    // Módulo "Solicitações": acompanha (READ-ONLY) as solicitações feitas ao estoque 5
    // (Farmácia Central). Serve para não perder pedidos se a impressora da unidade falhar.
    // Lê o form M_BAIXASOL sem NUNCA dar baixa.
    public class SolicitacoesView : UserControl
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbSituacao;
        private StyledComboBox cmbPeriodo;
        private StyledComboBox cmbOrigem;
        private PrimaryButton btnAtualizar;
        private PrimaryButton btnRecarregar;
        private TextBox txtBusca;
        private StatusBanner banner;
        private DataGridView grid;
        private Label lblVazio;
        private Label lblResumo;

        private const int Estoque = 5;      // Farmácia Central
        private const int TetoJanelas = 40; // até ~2000 solicitações por consulta

        // Estado em memória (a atualização é MANUAL: leve no "Atualizar", completa no "Recarregar tudo")
        private readonly List<Solicitacao> _itens = new List<Solicitacao>();
        private readonly HashSet<long> _numeros = new HashSet<long>();
        private readonly HashSet<long> _novos = new HashSet<long>();       // realçar recém-chegadas (azul)
        private readonly HashSet<long> _atendidas = new HashSet<long>();   // realçar recém-atendidas (verde)
        private long _maxNumero;
        private string _sit = "", _orig = "";
        private int _dias;
        private bool _busy;
        private DateTime _ultimaAtualizacao;

        // Snapshot em memória: sobrevive à troca de aba na mesma sessão (reabertura instantânea).
        private static SolicitacoesCache.Snapshot _memoria;

        public SolicitacoesView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152)); // cartão de filtros
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // resultado

            root.Controls.Add(BuildFiltroCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultCard(), 0, 2);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
        }

        private Control BuildFiltroCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 14);
            card.Padding = new Padding(20, 16, 20, 18);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 6;
            tl.RowCount = 3;
            // Orçamento: janela padrão 1180 - sidebar 210 - paddings ≈ 858px úteis no cartão.
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); // situação
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); // período
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); // origem
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); // Atualizar (leve)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); // Recarregar tudo
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titulo = new Label();
            titulo.Text = "Solicitações para a Farmácia Central";
            titulo.Font = Theme.H3; titulo.ForeColor = Theme.TextDark; titulo.BackColor = Theme.CardBg;
            titulo.AutoSize = true; titulo.Margin = new Padding(0, 0, 0, 12);
            tl.Controls.Add(titulo, 0, 0); tl.SetColumnSpan(titulo, 6);

            tl.Controls.Add(MicroLabel("SITUAÇÃO"), 0, 1);
            tl.Controls.Add(MicroLabel("PERÍODO"), 1, 1);
            tl.Controls.Add(MicroLabel("ORIGEM"), 2, 1);

            cmbSituacao = NovoCombo(new object[] {
                new OpcaoItem("Todas", ""), new OpcaoItem("Pendentes", "P"), new OpcaoItem("Atendidas", "S") });
            tl.Controls.Add(cmbSituacao, 0, 2);

            cmbPeriodo = NovoCombo(new object[] {
                new PeriodoItem("Hoje", 0), new PeriodoItem("Últimos 3 dias", 2) });
            tl.Controls.Add(cmbPeriodo, 1, 2);

            cmbOrigem = NovoCombo(new object[] {
                new OpcaoItem("Todas", ""), new OpcaoItem("Prescrição médica", "PRE"),
                new OpcaoItem("Material", "AVU"), new OpcaoItem("Transferência", "TRA"),
                new OpcaoItem("Devolução", "DEV") });
            tl.Controls.Add(cmbOrigem, 2, 2);

            btnAtualizar = new PrimaryButton();
            btnAtualizar.Text = "Atualizar";
            btnAtualizar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnAtualizar.Height = 40;
            btnAtualizar.Margin = new Padding(0, 0, 12, 0);
            btnAtualizar.Click += new EventHandler(OnAtualizar);
            tl.Controls.Add(btnAtualizar, 3, 2);

            btnRecarregar = new PrimaryButton();
            btnRecarregar.Text = "Recarregar tudo";
            btnRecarregar.Ghost = true;
            btnRecarregar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnRecarregar.Height = 40;
            btnRecarregar.Margin = new Padding(0, 0, 0, 0);
            btnRecarregar.Click += new EventHandler(OnRecarregar);
            tl.Controls.Add(btnRecarregar, 4, 2);

            var dica = new ToolTip();
            dica.SetToolTip(btnAtualizar, "Busca as novas solicitações e atualiza a situação das pendentes (rápido)");
            dica.SetToolTip(btnRecarregar, "Refaz a consulta completa no MV Soul (demora mais)");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });   // ToolTip é Component: não é descartado com a árvore de controles

            card.Controls.Add(tl);
            return card;
        }

        private StyledComboBox NovoCombo(object[] itens)
        {
            var c = new StyledComboBox();
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            c.Margin = new Padding(0, 0, 14, 0);
            c.Items.AddRange(itens);
            c.SelectedIndex = 0;
            return c;
        }

        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true; l.Margin = new Padding(2, 0, 0, 5);
            return l;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, 14);
            return banner;
        }

        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(18, 14, 18, 16);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3; tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // título
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // busca
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // contagem
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titulo = new Label();
            titulo.Text = "Resultado"; titulo.Font = Theme.H3; titulo.ForeColor = Theme.TextDark;
            titulo.BackColor = Theme.CardBg; titulo.AutoSize = true;
            titulo.Margin = new Padding(0, 6, 0, 10); titulo.Anchor = AnchorStyles.Left;
            tl.Controls.Add(titulo, 0, 0);

            tl.Controls.Add(BuildBusca(), 1, 0);

            lblResumo = new Label();
            lblResumo.AutoSize = true; lblResumo.Font = Theme.BodyBold; lblResumo.ForeColor = Theme.TextSoft;
            lblResumo.BackColor = Theme.CardBg; lblResumo.Anchor = AnchorStyles.Right;
            lblResumo.Margin = new Padding(0, 8, 0, 0); lblResumo.Text = "";
            tl.Controls.Add(lblResumo, 2, 0);

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Escolha os filtros e clique em “Atualizar”.";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 1); tl.SetColumnSpan(host, 3);   // grade ocupa a largura toda do cartão

            card.Controls.Add(tl);
            return card;
        }

        // Barra de busca: filtra a grade ENQUANTO o usuário digita (sem Enter), por
        // nº da solicitação, paciente, atendimento, solicitante ou setor — ignorando
        // maiúsculas e acentos. A busca é local (dados já em memória): instantânea.
        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left;
            hostBusca.Width = 360; hostBusca.Height = 34;
            hostBusca.Margin = new Padding(18, 2, 12, 10);
            hostBusca.Padding = new Padding(10, 7, 8, 6);

            var lupa = new Label();
            lupa.Text = "";                       // glifo "Search" (Segoe MDL2 Assets)
            lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
            lupa.Dock = DockStyle.Left; lupa.Width = 24;
            lupa.TextAlign = ContentAlignment.MiddleLeft;

            txtBusca = new TextBox();
            txtBusca.BorderStyle = BorderStyle.None;
            txtBusca.Font = Theme.Body; txtBusca.ForeColor = Theme.TextDark; txtBusca.BackColor = Color.White;
            txtBusca.Dock = DockStyle.Fill;
            // Debounce curto: filtra "enquanto digita" sem redesenhar a grade a CADA tecla
            // (com a consulta no teto, ~2000 linhas, o redesenho por tecla travaria a digitação).
            var espera = new System.Windows.Forms.Timer();
            espera.Interval = 180;
            espera.Tick += new EventHandler(delegate { espera.Stop(); RenderGrid(); });
            txtBusca.TextChanged += new EventHandler(delegate { espera.Stop(); espera.Start(); });
            this.Disposed += new EventHandler(delegate { espera.Dispose(); });
            txtBusca.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape && txtBusca.Text.Length > 0)
                { txtBusca.Text = ""; ke.SuppressKeyPress = true; }
            });
            // Placeholder nativo (some ao digitar): EM_SETCUEBANNER; wParam=1 mantém com foco.
            txtBusca.HandleCreated += new EventHandler(delegate
            {
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar paciente, atendimento ou nº");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            return hostBusca;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private DataGridView NovaGrid()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill; g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.None;
            g.RowHeadersVisible = false; g.AllowUserToAddRows = false; g.AllowUserToResizeRows = false;
            g.AllowUserToResizeColumns = false; g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
            g.AllowUserToOrderColumns = false; g.EnableHeadersVisualStyles = false;
            g.GridColor = Theme.BorderSoft; g.Font = Theme.Body;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Primary;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34; g.RowTemplate.Height = 28;
            g.DefaultCellStyle.Padding = new Padding(8, 0, 6, 0);
            g.DefaultCellStyle.SelectionBackColor = Theme.Light;
            g.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
            g.DefaultCellStyle.ForeColor = Theme.TextDark;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            // Grade redesenhada por completo a cada busca — buffer duplo evita o pisca
            // (propriedade protegida; via reflexão, truque padrão do WinForms).
            typeof(DataGridView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(g, true, null);

            g.Columns.Add(Col("numero", "Nº", 90, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("dthr", "Data / hora", 140, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("situacao", "Situação", 108, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("origem", "Origem", 132, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("paciente", "Paciente", 185, false, DataGridViewContentAlignment.MiddleLeft));
            var setor = Col("setor", "Setor solicitante", 160, true, DataGridViewContentAlignment.MiddleLeft);
            setor.MinimumWidth = 140;   // em tela estreita: rola na horizontal, não colapsa
            g.Columns.Add(setor);
            g.Columns.Add(Col("solicitante", "Solicitante", 170, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("atend", "Atend.", 68, false, DataGridViewContentAlignment.MiddleLeft));

            var imp = new DataGridViewButtonColumn();
            imp.Name = "imprimir"; imp.HeaderText = ""; imp.Width = 46;
            imp.Text = ""; imp.UseColumnTextForButtonValue = true;   // glifo "Print" (Segoe MDL2 Assets)
            imp.SortMode = DataGridViewColumnSortMode.NotSortable;
            imp.FlatStyle = FlatStyle.Flat;
            imp.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            imp.DefaultCellStyle.Font = Theme.Icon;
            imp.DefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
            imp.DefaultCellStyle.ForeColor = Theme.Primary;
            imp.DefaultCellStyle.SelectionForeColor = Theme.Primary;
            imp.DefaultCellStyle.BackColor = Color.White;
            imp.DefaultCellStyle.SelectionBackColor = Theme.GridZebra;
            g.Columns.Add(imp);

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellContentClick += new DataGridViewCellEventHandler(Grid_Click);
            return g;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = w;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        // Colore a situação e destaca as urgentes.
        private void Grid_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var row = grid.Rows[e.RowIndex];
            var tag = row.Tag as Solicitacao;
            if (tag == null) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "situacao")
            {
                if (tag.Situacao == "P") e.CellStyle.ForeColor = Theme.Warning;
                else if (tag.Situacao == "S") e.CellStyle.ForeColor = Theme.Success;
                else e.CellStyle.ForeColor = Theme.Primary;
                e.CellStyle.Font = Theme.BodyBold;
            }
            if (col == "numero" && tag.Urgente)
            {
                // Entrega imediata: marca com ⚠ (sem vermelho).
                e.Value = "⚠ " + tag.Numero;
                e.CellStyle.Font = Theme.BodyBold;
                e.FormattingApplied = true;
            }
        }

        // Clique no botão de imprimir de uma linha → gera o comprovante e abre p/ impressão.
        private void Grid_Click(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "imprimir") return;
            var s = grid.Rows[e.RowIndex].Tag as Solicitacao;
            if (s == null) return;
            ImprimirSolicitacao(s);
        }

        // Imprime a solicitação como o próprio sistema faz: gera o relatório PDF
        // (R_SOLSAI_PRO) no servidor e abre a URL numa aba do navegador. READ-ONLY,
        // não dá baixa. O usuário imprime pelo próprio PDF.
        private bool _imprimindo;   // trava contra cliques repetidos no botão da linha
        private async void ImprimirSolicitacao(Solicitacao s)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_imprimindo || _busy) return;
            _imprimindo = true;
            banner.SetState(StatusKind.Busy, "Gerando o PDF da solicitação " + s.Numero + "...");
            try
            {
                string url = await MorphisClient.GerarPdfSolicitacaoAsync(_host.Session, s.Numero);
                if (IsDisposed || Disposing) return;
                Navegador.Abrir(url);   // abre o PDF numa aba (Cent Browser -> Edge -> padrão)
                banner.SetState(StatusKind.Success, "PDF da solicitação " + s.Numero + " aberto no navegador.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error, "Não consegui gerar o PDF da solicitação: " + ex.Message);
            }
            finally { _imprimindo = false; }
        }

        private void OnLoad(object sender, EventArgs e)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Warning, "Faça login para consultar as solicitações."); return; }

            // Reabertura instantânea: usa o snapshot em memória (mesma sessão) ou o disco,
            // e volta os COMBOS para o filtro que gerou o snapshot (a view é recriada a cada
            // visita; sem isso o combo resetado invalidaria o cache e dispararia uma carga
            // completa que o usuário não pediu). Nada é consultado sozinho na reabertura.
            var snap = _memoria != null ? _memoria : SolicitacoesCache.Carregar();
            if (snap != null && snap.Itens.Count > 0 && snap.Quando.Date == DateTime.Today
                && RestaurarFiltros(snap.FiltroKey))
            {
                LerFiltros();
                _itens.Clear(); _numeros.Clear(); _novos.Clear(); _atendidas.Clear(); _maxNumero = snap.MaxNumero;
                foreach (var s in snap.Itens) if (_numeros.Add(s.Numero)) _itens.Add(s);
                _ultimaAtualizacao = snap.Quando;
                OrdenarDesc(); RenderGrid();
                AtualizarBanner(false);
            }
            else
                CarregarTudo();             // primeira carga do dia (com indicador)
        }

        // Aplica nos combos o filtro gravado no snapshot ("sit|orig|dias"). false se não der.
        private bool RestaurarFiltros(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string[] p = key.Split('|');
            if (p.Length != 3) return false;
            int iSit = IndiceOpcao(cmbSituacao, p[0]);
            int iOrig = IndiceOpcao(cmbOrigem, p[1]);
            int iDias = -1; int dias;
            if (int.TryParse(p[2], out dias))
                for (int i = 0; i < cmbPeriodo.Items.Count; i++)
                    if (((PeriodoItem)cmbPeriodo.Items[i]).Dias == dias) { iDias = i; break; }
            if (iSit < 0 || iOrig < 0 || iDias < 0) return false;
            cmbSituacao.SelectedIndex = iSit; cmbOrigem.SelectedIndex = iOrig; cmbPeriodo.SelectedIndex = iDias;
            return true;
        }
        private int IndiceOpcao(StyledComboBox cmb, string valor)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                var op = cmb.Items[i] as OpcaoItem;
                if (op != null && op.Valor == valor) return i;
            }
            return -1;
        }

        // "Atualizar" (leve): busca as novas + sincroniza a situação. Se o usuário mudou
        // algum filtro (ou não há nada carregado), a sincronização incremental não vale —
        // cai na consulta completa.
        private void OnAtualizar(object sender, EventArgs e)
        {
            string sitNovo = (cmbSituacao.SelectedItem as OpcaoItem).Valor;
            string origNovo = (cmbOrigem.SelectedItem as OpcaoItem).Valor;
            int diasNovo = (cmbPeriodo.SelectedItem as PeriodoItem).Dias;
            bool mesmaConsulta = _itens.Count > 0 && _maxNumero > 0
                && sitNovo == _sit && origNovo == _orig && diasNovo == _dias
                && _ultimaAtualizacao.Date == DateTime.Today;   // virou o dia: recarrega
            if (mesmaConsulta) SincronizarLeve();
            else CarregarTudo();
        }

        // "Recarregar tudo" (agressivo): refaz a varredura completa do zero.
        private void OnRecarregar(object sender, EventArgs e)
        {
            CarregarTudo();
        }

        private void LerFiltros()
        {
            _sit = (cmbSituacao.SelectedItem as OpcaoItem).Valor;
            _orig = (cmbOrigem.SelectedItem as OpcaoItem).Valor;
            _dias = (cmbPeriodo.SelectedItem as PeriodoItem).Dias;
        }
        private string FiltroKey() { return _sit + "|" + _orig + "|" + _dias; }

        // Carga COMPLETA (varredura no servidor, ~15-20s). Limpa a grade e mostra o indicador.
        private async void CarregarTudo()
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy) return;
            _busy = true;
            LerFiltros();
            DateTime de = DateTime.Today.AddDays(-_dias);

            TravarControles("Buscando...");
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true;
            lblVazio.Text = "Carregando solicitações..."; lblResumo.Text = "";
            banner.SetState(StatusKind.Busy, "Carregando solicitações...");

            try
            {
                var res = await MorphisClient.RastrearSolicitacoesAsync(
                    _host.Session, Estoque, _sit.Length == 0 ? null : _sit, _orig.Length == 0 ? null : _orig, de, null, TetoJanelas);
                if (IsDisposed || Disposing) return;

                _itens.Clear(); _numeros.Clear(); _novos.Clear(); _atendidas.Clear(); _maxNumero = 0;
                foreach (var s in res.Itens)
                {
                    if (s.Numero > _maxNumero) _maxNumero = s.Numero;   // marca d'água sobre TUDO que veio
                    if (!PassaOrigem(s)) continue;                       // refino de origem no cliente
                    if (_numeros.Add(s.Numero)) _itens.Add(s);
                }
                OrdenarDesc();
                _ultimaAtualizacao = DateTime.Now;
                SalvarCache();
                RenderGrid();
                AtualizarBanner(res.AtingiuTeto);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Erro ao buscar as solicitações: " + ex.Message);
                lblVazio.Text = "Falha na busca. Tente “Atualizar”.";
            }
            finally
            {
                if (!IsDisposed && !Disposing) DestravarControles();
                _busy = false;
            }
        }

        // ATUALIZAÇÃO LEVE (botão "Atualizar", ~2-6s): (A) busca as NOVAS (número > último
        // visto) e (B) sincroniza a SITUAÇÃO — consulta os pendentes atuais e, por diferença,
        // reclassifica quem saiu de Pendente (virou "Em atendimento" ou "Atendida"; a consulta
        // de "A" só é feita quando é preciso distinguir). Se QUALQUER consulta bater o teto de
        // janelas (resultado incompleto), NADA é aplicado — senão o diff mentiria, marcando
        // como atendida solicitação ainda pendente.
        private async void SincronizarLeve()
        {
            if (_busy || _host.Session == null || ProduzirView.ProducaoEmAndamento || _maxNumero <= 0) return;
            if (IsDisposed || Disposing) return;
            _busy = true;
            TravarControles("Atualizando...");
            banner.SetState(StatusKind.Busy, "Atualizando...");
            try
            {
                string sit = _sit.Length == 0 ? null : _sit;
                string orig = _orig.Length == 0 ? null : _orig;
                DateTime deLimite = DateTime.Today.AddDays(-_dias);

                // (A) novas acima do último número
                var novas = await MorphisClient.RastrearNovasAsync(_host.Session, Estoque, sit, orig, _maxNumero);
                if (IsDisposed || Disposing) return;
                if (novas.AtingiuTeto) { AvisarRecarregar(); return; }

                // (B) conjunto de PENDENTES atuais, a partir do menor item local ainda "aberto"
                //     (P ou A). Só quando o filtro pode conter pendentes.
                long minRef = long.MaxValue; bool temA = false;
                foreach (var it in _itens)
                    if (it.Situacao == "P" || it.Situacao == "A")
                    {
                        if (it.Numero < minRef) minRef = it.Numero;
                        if (it.Situacao == "A") temA = true;
                    }
                SolicitacoesResult pend = null;
                HashSet<long> pendentesAgora = null;
                if (minRef != long.MaxValue && _sit != "S")
                {
                    pend = await MorphisClient.RastrearNovasAsync(_host.Session, Estoque, "P", orig, minRef - 1);
                    if (IsDisposed || Disposing) return;
                    if (pend.AtingiuTeto) { AvisarRecarregar(); return; }
                    pendentesAgora = new HashSet<long>();
                    foreach (var pp in pend.Itens) pendentesAgora.Add(pp.Numero);
                }

                int add = 0, mudou = 0;

                // aplica novas (e pendentes recém-vistos, se houver)
                add += Incorporar(novas.Itens, deLimite);
                if (pend != null) add += Incorporar(pend.Itens, deLimite);

                // reclassifica quem saiu de Pendente (e re-checa os "Em atendimento" locais)
                if (pendentesAgora != null)
                {
                    var sairamDeP = new List<Solicitacao>();
                    foreach (var it in _itens)
                    {
                        if (it.Situacao != "P" || it.Numero < minRef) continue;
                        if (pendentesAgora.Contains(it.Numero)) continue;   // ainda pendente
                        sairamDeP.Add(it);
                    }

                    // "A" (Em atendimento) só precisa ser consultado quando é preciso
                    // distinguir A de S na tela (filtro "Todas") ou re-checar A locais.
                    HashSet<long> emAtendimentoAgora = null;
                    if (_sit.Length == 0 && (temA || sairamDeP.Count > 0))
                    {
                        var ea = await MorphisClient.RastrearNovasAsync(_host.Session, Estoque, "A", orig, minRef - 1);
                        if (IsDisposed || Disposing) return;
                        if (ea.AtingiuTeto) { AvisarRecarregar(); return; }
                        emAtendimentoAgora = new HashSet<long>();
                        foreach (var aa in ea.Itens) emAtendimentoAgora.Add(aa.Numero);
                    }

                    var remover = new List<Solicitacao>();
                    foreach (var it in sairamDeP)
                    {
                        mudou++;
                        _novos.Remove(it.Numero);
                        if (_sit == "P") remover.Add(it);   // filtro Pendentes: sai da lista
                        else
                        {
                            it.Situacao = (emAtendimentoAgora != null && emAtendimentoAgora.Contains(it.Numero)) ? "A" : "S";
                            _atendidas.Add(it.Numero);       // realce verde = situação mudou
                        }
                    }
                    // A -> S: item local "Em atendimento" que sumiu do conjunto A do servidor
                    if (emAtendimentoAgora != null)
                        foreach (var it in _itens)
                        {
                            if (it.Situacao != "A" || it.Numero < minRef) continue;
                            if (emAtendimentoAgora.Contains(it.Numero)) continue;
                            mudou++;
                            it.Situacao = pendentesAgora.Contains(it.Numero) ? "P" : "S";   // voltou ou concluiu
                            _atendidas.Add(it.Numero);
                        }
                    foreach (var it in remover) { _itens.Remove(it); _numeros.Remove(it.Numero); _atendidas.Remove(it.Numero); }
                }

                // O que mudou fica visível na grade (realce azul = nova, verde = mudou de
                // situação) e na contagem do cabeçalho; o banner mostra a hora.
                _ultimaAtualizacao = DateTime.Now;
                if (add > 0 || mudou > 0) { OrdenarDesc(); RenderGrid(); }
                SalvarCache();   // sempre: a hora do cache acompanha a do banner
                AtualizarBanner(false);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error, "Erro ao atualizar: " + ex.Message);
            }
            finally
            {
                if (!IsDisposed && !Disposing) DestravarControles();
                _busy = false;
            }
        }

        // Alguma consulta incremental veio truncada: aplicar seria mentir. Nada foi gravado.
        private void AvisarRecarregar()
        {
            banner.SetState(StatusKind.Warning,
                "Muita coisa mudou desde a última atualização — use “Recarregar tudo”.");
        }

        // Durante uma consulta, tudo que muda o QUE está sendo mostrado fica travado
        // (botões, filtros e busca) — senão a grade termina com dados de um filtro sob
        // os rótulos de outro, ou a busca redesenha por cima do "Carregando...".
        private void TravarControles(string textoAtualizar)
        {
            btnAtualizar.Enabled = false; btnAtualizar.Text = textoAtualizar;
            btnRecarregar.Enabled = false;
            cmbSituacao.Enabled = false; cmbPeriodo.Enabled = false; cmbOrigem.Enabled = false;
            txtBusca.Enabled = false;
        }
        private void DestravarControles()
        {
            btnAtualizar.Enabled = true; btnAtualizar.Text = "Atualizar";
            btnRecarregar.Enabled = true;
            cmbSituacao.Enabled = true; cmbPeriodo.Enabled = true; cmbOrigem.Enabled = true;
            txtBusca.Enabled = true;
        }

        // Incorpora solicitações ainda não conhecidas (dentro do período e da origem
        // escolhida), realçando-as. Devolve quantas entraram.
        private int Incorporar(List<Solicitacao> lista, DateTime deLimite)
        {
            int n = 0;
            foreach (var s in lista)
            {
                if (s.Numero > _maxNumero) _maxNumero = s.Numero;
                if (_numeros.Contains(s.Numero)) continue;
                if (!PassaOrigem(s)) continue;
                DateTime dt;
                if (TryDataView(s.Data, out dt) && dt.Date < deLimite.Date) continue;
                _numeros.Add(s.Numero); _itens.Add(s); _novos.Add(s.Numero); n++;
            }
            return n;
        }

        // Refino de ORIGEM no cliente: "Material" (AVU) inclui os pedidos de setor (SET),
        // que chegam com a origem vazia; os demais valores comparam com a origem efetiva.
        private bool PassaOrigem(Solicitacao s)
        {
            if (_orig.Length == 0) return true;
            string oe = s.OrigemEfetiva;
            if (_orig == "AVU") return oe == "AVU" || oe == "SET";
            return oe == _orig;
        }

        private void OrdenarDesc()
        {
            _itens.Sort(delegate(Solicitacao a, Solicitacao b) { return b.Numero.CompareTo(a.Numero); });
        }

        // Redesenha a grade aplicando a BUSCA (se houver texto no campo) e atualiza a
        // contagem do cabeçalho ("N de M" quando a busca está filtrando).
        private void RenderGrid()
        {
            string busca = txtBusca != null ? txtBusca.Text : "";
            var mostrar = _itens;
            if (busca != null && busca.Trim().Length > 0)
            {
                mostrar = new List<Solicitacao>();
                foreach (var s in _itens)
                    if (BuscaSolicitacao.Corresponde(s, busca)) mostrar.Add(s);
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var s in mostrar)
            {
                int r = grid.Rows.Add(
                    s.Numero.ToString(),
                    s.Data.Length >= 10 ? s.Data.Substring(0, 10) + "  " + s.Hora : s.Data + "  " + s.Hora,
                    s.SituacaoLabel, s.OrigemLabel,
                    string.IsNullOrEmpty(s.Paciente) ? "—" : s.Paciente,
                    s.Setor, s.Solicitante, s.Atendimento);
                grid.Rows[r].Tag = s;
                grid.Rows[r].Cells["imprimir"].ToolTipText = "Abrir o PDF da solicitação";
                if (_novos.Contains(s.Numero)) grid.Rows[r].DefaultCellStyle.BackColor = Theme.InfoBg;          // nova (azul)
                else if (_atendidas.Contains(s.Numero)) grid.Rows[r].DefaultCellStyle.BackColor = Theme.SuccessBg; // recém-atendida (verde)
            }

            bool filtrando = mostrar != _itens;
            int imediatas = 0; foreach (var s in mostrar) if (s.Urgente) imediatas++;
            string contagem = filtrando
                ? mostrar.Count + " de " + _itens.Count + " solicitações"
                : _itens.Count + (_itens.Count == 1 ? " solicitação" : " solicitações");
            lblResumo.Text = contagem
                + (imediatas > 0 ? "  ·  " + imediatas + (imediatas == 1 ? " entrega imediata" : " entregas imediatas") : "");

            bool tem = mostrar.Count > 0;
            lblVazio.Text = (filtrando && _itens.Count > 0)
                ? "Nenhuma solicitação corresponde à busca."
                : "Nenhuma solicitação encontrada para os filtros escolhidos.";
            grid.Visible = tem; lblVazio.Visible = !tem;
            grid.ClearSelection();
            grid.ResumeLayout();
        }

        // Banner enxuto e fiel: só o estado — a hora da última atualização (a contagem
        // fica no cabeçalho e o que mudou aparece realçado na grade).
        private void AtualizarBanner(bool teto)
        {
            if (teto)
                banner.SetState(StatusKind.Warning,
                    "Muitas solicitações no período — mostrando as " + _itens.Count + " mais recentes. Reduza o período ou filtre a situação.");
            else if (_itens.Count > 0)
                banner.SetState(StatusKind.Info, "Atualizado às " + _ultimaAtualizacao.ToString("HH:mm") + ".");
            else
                banner.SetState(StatusKind.Info, "Nenhuma solicitação para os filtros escolhidos.");
        }

        private void SalvarCache()
        {
            var snap = new SolicitacoesCache.Snapshot();
            snap.FiltroKey = FiltroKey(); snap.Quando = _ultimaAtualizacao; snap.MaxNumero = _maxNumero;
            snap.Itens = new List<Solicitacao>(_itens);
            _memoria = snap;
            SolicitacoesCache.Salvar(snap.FiltroKey, snap.Itens, snap.MaxNumero, snap.Quando);
        }

        private static bool TryDataView(string dt, out DateTime val)
        {
            val = DateTime.MinValue;
            if (string.IsNullOrEmpty(dt)) return false;
            string d = dt.Length >= 10 ? dt.Substring(0, 10) : dt;
            return DateTime.TryParseExact(d, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out val);
        }
    }
}

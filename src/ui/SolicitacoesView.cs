using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
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

    // Módulo "Solicitações": acompanha (READ-ONLY) as solicitações feitas à Farmácia
    // Central (estoque 5) ou à CAF (estoque 2), escolhido no seletor ESTOQUE. Serve para
    // não perder pedidos se a impressora da unidade falhar. Lê o form M_BAIXASOL sem NUNCA
    // dar baixa. Cada estoque tem seu próprio estado/cache (troca instantânea entre eles).
    public class SolicitacoesView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbEstoque;
        private StyledComboBox cmbSituacao;
        private StyledComboBox cmbOrigem;
        private PrimaryButton btnRecarregar;
        private TextBox txtBusca;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;

        private int _estoque = 5;           // estoque atual (5 = Farmácia Central, 2 = CAF)


        // Estado em memória (a lista se atualiza sozinha a cada 30s; "Recarregar tudo" refaz do zero)
        private readonly List<Solicitacao> _itens = new List<Solicitacao>();
        private readonly HashSet<long> _numeros = new HashSet<long>();
        private readonly HashSet<long> _novos = new HashSet<long>();       // realçar recém-chegadas (azul)
        private readonly HashSet<long> _atendidas = new HashSet<long>();   // realçar recém-atendidas (verde)
        private long _maxNumero;
        private string _sit = "", _orig = "";
        private bool _busy;
        private bool _trocandoUI;   // suprime o handler do combo durante ajustes programáticos
        private DateTime _ultimaAtualizacao;
        private bool _renderPendente;      // atualização chegou com o mouse sobre a grade
        private DateTime _adiadoDesde = DateTime.MinValue;

        // Snapshot em memória POR ESTOQUE: sobrevive à troca de aba na mesma sessão
        // (reabertura instantânea), e guarda os dois estoques ao mesmo tempo.
        private static readonly Dictionary<int, SolicitacoesCache.Snapshot> _memoria =
            new Dictionary<int, SolicitacoesCache.Snapshot>();
        private static int _ultimoEstoque = 5;   // estoque reaberto ao recriar a view

        // Estoques e janela de tempo. A Farmácia Central tem centenas de solicitações por dia
        // (só HOJE já é muita coisa); a CAF recebe poucas, então lá a lista abrange 7 DIAS —
        // senão a tela vive quase vazia e um pedido de anteontem some do acompanhamento.
        private const int EstoqueCaf = 2;
        private const int DiasCaf = 7;

        // Realces (o que chegou / o que foi atendido) por estoque, com a HORA da marcação: eles
        // duram alguns minutos em vez de sumirem na varredura seguinte — com atualização a cada
        // 30s, um realce de um ciclo só seria invisível para quem não estivesse olhando na hora.
        private const int MinutosDeRealce = 10;
        private static readonly Dictionary<int, Dictionary<long, DateTime>> _memNovos =
            new Dictionary<int, Dictionary<long, DateTime>>();
        private static readonly Dictionary<int, Dictionary<long, DateTime>> _memAtendidas =
            new Dictionary<int, Dictionary<long, DateTime>>();

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
            // A coluna única é Percent: sem isso o TableLayoutPanel deixa a coluna crescer
            // até o "preferido" dos cartões e a barra estoura o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // AutoSize, não altura fixa: com altura em px o conteúdo cabia num DPI e era
            // cortado embaixo em outro, porque a fonte cresce mais que a linha.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cartão de filtros (compacto, 1 linha)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // resultado (o principal: fica com o resto)

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
            // AutoSize, não altura fixa: com altura em px o conteúdo cabia num DPI e era
            // cortado embaixo em outro, porque a fonte cresce mais que a linha.
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(14));

            // Barra compacta de 1 linha: ESTOQUE, SITUAÇÃO, ORIGEM e a BUSCA à esquerda,
            // com a ação de recarga na última coluna, colada à direita — mesma posição das
            // outras abas, para o olho aprender um lugar só. Sem título (redundante com o
            // cabeçalho do módulo) p/ sobrar o máximo de espaço à lista.
            //
            // Quem ESTICA é a coluna da BUSCA, não uma coluna vazia: quando falta espaço,
            // quem cede é a busca (ainda usável menor) e o botão fica sempre inteiro.
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 5;
            tl.RowCount = 2;
            // Orçamento p/ janela mínima (base 96 DPI): padding do cartão 40 + fixos
            // 166+164+124+136 = 590 + margens 30 + busca nascendo em 40+10 ≤ ~672 úteis —
            // nada é cortado, "Farmácia Central" cabe sem reticências e a busca ainda fica
            // usável no pior caso, crescendo até 420 quando a janela dá espaço.
            // SITUAÇÃO precisa de 164: em 96 DPI (monitor da farmácia) a fonte rende mais
            // larga e a face fechada com "Em atendimento" saía cortada com 140 (a elipse
            // dispara pela largura MEDIDA do texto, com a folga do GDI, não pelos glifos).
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(166))); // estoque ("Farmácia Central")
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(164))); // situação ("Em atendimento")
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(124))); // origem ("Prescrição")
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));           // busca (elástica)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(136))); // Recarregar tudo
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // micro rótulos
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // combos + busca + botão

            tl.Controls.Add(MicroLabel("ESTOQUE"), 0, 0);
            tl.Controls.Add(MicroLabel("SITUAÇÃO"), 1, 0);
            tl.Controls.Add(MicroLabel("ORIGEM"), 2, 0);
            tl.Controls.Add(MicroLabel("BUSCAR"), 3, 0);

            // Alterna a view inteira entre Farmácia Central (5) e CAF (2).
            cmbEstoque = NovoCombo(new object[] {
                new OpcaoItem("Farmácia Central", "5"), new OpcaoItem("CAF", "2") });
            cmbEstoque.SelectedIndexChanged += new EventHandler(OnEstoqueMudou);
            tl.Controls.Add(cmbEstoque, 0, 1);

            cmbSituacao = NovoCombo(new object[] {
                new OpcaoItem("Todas", ""), new OpcaoItem("Pendentes", "P"),
                new OpcaoItem("Em atendimento", "A"), new OpcaoItem("Atendidas", "S") });
            tl.Controls.Add(cmbSituacao, 1, 1);

            cmbOrigem = NovoCombo(new object[] {
                new OpcaoItem("Todas", ""), new OpcaoItem("Prescrição", "PRE"),
                new OpcaoItem("Material", "AVU"), new OpcaoItem("Transferência", "TRA"),
                new OpcaoItem("Devolução", "DEV") });
            tl.Controls.Add(cmbOrigem, 2, 1);

            tl.Controls.Add(BuildBusca(), 3, 1);

            // Único botão da barra. A lista se atualiza sozinha a cada 30s, então não existe
            // mais "Atualizar": este aqui é o reset de emergência, para quando algum registro
            // parecer fora do lugar. Fica em estilo secundário — é ação rara, não a principal.
            btnRecarregar = new PrimaryButton();
            btnRecarregar.Text = "Recarregar tudo";
            btnRecarregar.Ghost = true;
            btnRecarregar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnRecarregar.Height = Theme.S(40);
            btnRecarregar.Margin = new Padding(0);
            btnRecarregar.Click += new EventHandler(OnRecarregar);
            tl.Controls.Add(btnRecarregar, 4, 1);

            var dica = new ToolTip();
            dica.SetToolTip(btnRecarregar, "Refaz a consulta completa no MV Soul, do zero (demora mais)");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });   // ToolTip é Component: não é descartado com a árvore de controles

            card.Controls.Add(tl);
            return card;
        }

        private StyledComboBox NovoCombo(object[] itens)
        {
            var c = new StyledComboBox();
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            c.Margin = new Padding(0, 0, Theme.S(10), 0);
            c.Items.AddRange(itens);
            c.SelectedIndex = 0;
            return c;
        }

        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true; l.Margin = new Padding(2, 0, 0, Theme.S(5));
            return l;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            return banner;
        }

        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            // A busca mora na barra de filtros (com as demais abas); título e contagem
            // saíram a pedido — o cartão é a grade, direto.
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1; tl.RowCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Carregando as solicitações...";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 0);

            card.Controls.Add(tl);
            return card;
        }

        // Barra de busca: filtra a grade ENQUANTO o usuário digita (sem Enter), por
        // nº da solicitação, paciente, atendimento, solicitante ou setor — ignorando
        // maiúsculas e acentos. A busca é local (dados já em memória): instantânea.
        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            // Elástica com teto: encolhe se a janela apertar (protegendo o botão ao lado
            // de ser cortado) e para de crescer em 420px, para não virar um campo enorme.
            // A largura INICIAL é mínima de propósito: é ela que entra na conta de
            // "tamanho preferido" do TableLayoutPanel — grande demais, a barra estoura
            // na janela mínima em vez de encolher.
            hostBusca.Width = Theme.S(40);
            hostBusca.MaximumSize = new Size(Theme.S(420), 0);
            hostBusca.Height = Theme.S(34);
            hostBusca.Margin = new Padding(0, 3, Theme.S(10), 3);   // centra contra o botão de 40px
            hostBusca.Padding = new Padding(Theme.S(10), Theme.S(7), Theme.S(8), Theme.S(6));

            var lupa = new Label();
            lupa.Text = "";                       // glifo "Search" (Segoe MDL2 Assets)
            lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
            lupa.Dock = DockStyle.Left; lupa.Width = Theme.S(24);
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
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar paciente, setor ou nº");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            return hostBusca;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private GridPro NovaGrid()
        {
            // GridPro traz o comportamento comum (double buffer, cabeçalho claro, hover,
            // seleção discreta, sem foco pontilhado); aqui fica só o que é desta tela.
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;
            // Saiu de cima da grade: aplica a atualização que ficou pendente (ver AbsorverAtualizacao).
            g.MouseLeave += new EventHandler(delegate
            {
                if (!_renderPendente) return;
                _renderPendente = false; _adiadoDesde = DateTime.MinValue;
                RenderGrid();
            });

            // Orçamento de largura (valores base em 96 DPI, escalados por S), conferido por
            // screenshot em 96 DPI — a fonte lá rende mais larga que em 125%, e é o DPI do
            // monitor da farmácia. As fixas somam 592 e as três elásticas (paciente/setor/
            // solicitante) têm mínimo de 270: total 848 ≤ ~850 úteis MEDIDOS na janela
            // padrão (margens de cartão comem mais do que a conta de padding sugere); em
            // janela maior (farmácia maximizada) as elásticas ficam com toda a sobra.
            // Larguras que NÃO podem encolher (valores medidos em 96 DPI):
            //  - Nº 106: "⚠ 1834512" das urgentes cortava com 100;
            //  - Data/hora 112: "29/08  14:35" cortava com 98;
            //  - Situação 132: "Em atendimento" em negrito cortava com 100;
            //  - Atend. 88: 7 dígitos cortavam com 76;
            //  - Origem 100: "Transferência" corta com menos.
            g.Columns.Add(Col("numero", "Nº", 106, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("dthr", "Data / hora", 112, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("situacao", "Situação", 132, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("origem", "Origem", 100, false, DataGridViewContentAlignment.MiddleCenter));
            var paciente = Col("paciente", "Paciente", 120, true, DataGridViewContentAlignment.MiddleLeft);
            paciente.FillWeight = 60; paciente.MinimumWidth = Theme.S(100);
            g.Columns.Add(paciente);
            var setor = Col("setor", "Setor", 100, true, DataGridViewContentAlignment.MiddleLeft);
            setor.FillWeight = 30;
            setor.MinimumWidth = Theme.S(76);   // em tela estreita: rola na horizontal, não colapsa
            g.Columns.Add(setor);
            // Solicitante também é elástico (peso menor): em janela apertada ele cede junto
            // com paciente/setor em vez de roubar espaço das colunas de dados fixos; em
            // janela larga, nomes de solicitante aparecem inteiros.
            var solicitante = Col("solicitante", "Solicitante", 100, true, DataGridViewContentAlignment.MiddleLeft);
            solicitante.FillWeight = 25;
            solicitante.MinimumWidth = Theme.S(94);   // 94 = o cabeçalho "Solicitante" inteiro
            g.Columns.Add(solicitante);
            g.Columns.Add(Col("atend", "Atend.", 88, false, DataGridViewContentAlignment.MiddleCenter));

            var imp = new DataGridViewButtonColumn();
            imp.Name = "imprimir"; imp.HeaderText = ""; imp.Width = Theme.S(40);
            imp.Text = ""; imp.UseColumnTextForButtonValue = true;   // glifo "Print" (Segoe MDL2 Assets)
            imp.SortMode = DataGridViewColumnSortMode.NotSortable;
            imp.FlatStyle = FlatStyle.Flat;
            imp.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            imp.DefaultCellStyle.Font = Theme.Icon;
            imp.DefaultCellStyle.Padding = new Padding(Theme.S(4), 3, Theme.S(4), 3);
            imp.DefaultCellStyle.ForeColor = Theme.Primary;
            imp.DefaultCellStyle.SelectionForeColor = Theme.Primary;
            // Sem cores próprias de fundo: a célula acompanha a zebra, os realces e a
            // faixa de seleção da linha (senão vira um retângulo claro destoando na ponta).
            imp.DefaultCellStyle.SelectionBackColor = Theme.SelBg;
            // Última coluna, a pedido (antes ficava colada ao número via DisplayIndex).
            g.Columns.Add(imp);

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellContentClick += new DataGridViewCellEventHandler(Grid_Click);
            return g;
        }

        // Abreviações de leitura rápida, SÓ na exibição (o dado original segue no Tag,
        // na busca e no tooltip da célula): a UTI vem do MV como "UTI - UNIDADE DE
        // TERAPIA INTENSIVA LADO B" e comia a coluna inteira; o Pronto Atendimento é
        // "PAM" na boca da equipe.
        private static string SetorCurto(string setor)
        {
            string s = (setor ?? "").Trim();
            if (s.StartsWith("UTI", StringComparison.OrdinalIgnoreCase)) return "UTI";
            if (s.StartsWith("PRONTO ATENDIMENTO", StringComparison.OrdinalIgnoreCase)) return "PAM";
            return setor;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            // Cabeçalho acompanha a célula: coluna centralizada com título à esquerda
            // continuava parecendo torta.
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
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
            bool tenho = false;
            try
            {
                // Gerar o PDF abre outro form no MV: pega a vez no portão, senão as duas
                // conversas se atropelam na mesma sessão.
                tenho = await MvGate.EntrarAsync("imprimir-solicitacao", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente imprimir de novo em instantes.");
                    return;
                }

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
                    banner.SetState(StatusKind.Error, "Não foi possível gerar o PDF da solicitação: " + ex.Message);
            }
            finally { if (tenho) MvGate.Sair(); _imprimindo = false; }
        }

        // ===== MONITOR EM SEGUNDO PLANO =====
        // A lista se atualiza sozinha a cada 30s ENQUANTO O APP ESTIVER ABERTO, mesmo com outro
        // módulo na tela: a view é destruída ao trocar de aba, mas o timer é estático e continua.
        // O resultado vai para o snapshot/cache do estoque; se a aba estiver aberta, cai direto
        // na grade. Assim ninguém perde uma solicitação por estar em outra tela.
        public const int IntervaloMonitorMs = 30000;
        // A varredura de 30s cobre só HOJE (1 consulta, barata). De tempos em tempos refaz o
        // período inteiro — é o que pega mudança de situação em solicitação de dias anteriores.
        private const int TicksParaVarreduraCompleta = 10;   // 10 x 30s = 5 min

        private static System.Windows.Forms.Timer _monitor;
        private static MainWindow _monitorHost;
        private static int _monitorTicks;
        private static int _sessaoId;                       // muda no logout: descarta varredura órfã
        private static DateTime _diaDaUltimaVarredura;      // vira o dia -> força varredura completa
        private static SolicitacoesView _viva;              // instância na tela (null = aba fechada)
        private static string _monSit = "", _monOrig = "";  // filtros que o monitor aplica

        // Ligado na PRIMEIRA abertura do módulo (não no login): quem nunca usa Solicitações não
        // paga o custo. Uma vez ligado, continua rodando com a aba fechada — que é o pedido.
        public static void IniciarMonitor(MainWindow host)
        {
            _monitorHost = host;
            if (_monitor == null)
            {
                _monitor = new System.Windows.Forms.Timer();
                _monitor.Interval = IntervaloMonitorMs;
                _monitor.Tick += new EventHandler(MonitorTick);
            }
            if (_monitor.Enabled) return;   // já ligado nesta sessão
            _monitorTicks = 0;
            _monitor.Start();
            Logger.Log("Solicitações: monitor em segundo plano ligado (a cada " + (IntervaloMonitorMs / 1000) + "s).");
        }

        // Fim de sessão (logout, expiração, fechamento). Invalida a varredura em voo e apaga o
        // que está em memória: a lista tem nome de paciente e não pode reaparecer no login seguinte.
        public static void PararMonitor()
        {
            bool estava = _monitor != null && _monitor.Enabled;
            if (_monitor != null) _monitor.Stop();
            _monitorHost = null;
            _sessaoId++;
            _memoria.Clear(); _memNovos.Clear(); _memAtendidas.Clear();
            if (estava) Logger.Log("Solicitações: monitor em segundo plano desligado.");
        }

        private static async void MonitorTick(object sender, EventArgs e)
        {
            if (_monitorHost == null || _monitorHost.Session == null) return;
            if (MvGate.Ocupado) return;                        // usuário (ou a varredura anterior) no MV
            if (_viva != null && (_viva.IsDisposed || _viva.Disposing)) _viva = null;
            // Preview de kit aberto = há um form vivo no servidor esperando o "Confirmar":
            // não é hora de abrir outro. Janela minimizada também não precisa de 30s.
            if (ProduzirView.OcupadoNoMv) return;
            if (_monitorHost.WindowState == FormWindowState.Minimized) return;

            if (!MvGate.Entrar("monitor-solicitacoes")) return;

            // O intervalo passa a contar a partir do FIM da varredura (a da Farmácia Central
            // leva ~10s): sem isso o monitor ficaria 1/3 do tempo falando com o servidor.
            _monitor.Stop();
            int sessao = _sessaoId;
            try
            {
                _monitorTicks++;
                bool completo = (_monitorTicks % TicksParaVarreduraCompleta) == 1
                                || _diaDaUltimaVarredura.Date != DateTime.Today;   // virou o dia
                await AtualizarEstoqueAsync(_ultimoEstoque, completo, false, sessao);
            }
            catch (SessaoExpiradaException)
            {
                // Em segundo plano não se arranca o usuário do que ele está fazendo: apenas
                // para o monitor. A próxima ação dele encontra a sessão morta e vai ao login.
                Logger.Log("Solicitações: sessão expirada — monitor parado.");
                if (sessao == _sessaoId) PararMonitor();
                return;
            }
            catch (Exception ex) { Logger.Log("Solicitações: falha no monitor: " + ex.Message); }
            finally
            {
                MvGate.Sair();
                // Só rearma se ainda for a mesma sessão (senão o logout já parou de propósito).
                if (sessao == _sessaoId && _monitor != null && _monitorHost != null) _monitor.Start();
            }
        }

        // Consulta o servidor e atualiza o snapshot do estoque (e a grade, se a aba estiver
        // aberta). completo=false varre só HOJE e preserva os dias anteriores já carregados.
        // ignorarDiff: recarga do zero — não realçar nada como "novo".
        // Quem chama JÁ TEM que estar segurando o MvGate.
        private static async Task AtualizarEstoqueAsync(int estoque, bool completo, bool ignorarDiff, int sessao)
        {
            var host = _monitorHost;
            if (host == null || host.Session == null) return;

            // Congela filtros e o dia: são lidos dos dois lados de um await de ~10s. Sem isto
            // uma varredura que atravessa a meia-noite pula um dia inteiro da janela da CAF.
            DateTime hoje = DateTime.Today;
            string mSit = _monSit, mOrig = _monOrig;
            string sit = mSit.Length == 0 ? null : mSit;
            string orig = mOrig.Length == 0 ? null : mOrig;
            string chave = ChaveFiltro(mSit, mOrig);

            var res = await BuscarAsync(host.Session, estoque, sit, orig, completo, hoje);
            if (sessao != _sessaoId) return;   // logout durante a varredura: descarta tudo

            // A base anterior serve para DUAS coisas distintas:
            //  - preservar os dias anteriores na varredura parcial (sempre vale);
            //  - comparar situação/novidade (só vale se for do MESMO dia e do MESMO filtro,
            //    senão o cache de ontem faria a lista inteira nascer marcada como "nova").
            var anterior = SnapshotEstatico(estoque);
            bool podeDiff = !ignorarDiff && anterior != null
                && anterior.Quando.Date == hoje && anterior.FiltroKey == chave;
            var antes = new Dictionary<long, string>();
            if (podeDiff) foreach (var it in anterior.Itens) antes[it.Numero] = it.Situacao;
            bool tinhaAntes = podeDiff && anterior.Itens.Count > 0;

            var itens = new List<Solicitacao>();
            var vistos = new HashSet<long>();
            long maxNumero = anterior != null ? anterior.MaxNumero : 0;
            foreach (var s in res.Itens)
            {
                if (s.Numero > maxNumero) maxNumero = s.Numero;   // marca d'água sobre TUDO que veio
                if (!PassaOrigemDe(s, mOrig)) continue;
                if (vistos.Add(s.Numero)) itens.Add(s);
            }
            // Varredura parcial: só hoje veio do servidor, então os dias ANTERIORES DA JANELA
            // continuam valendo (senão a lista de 7 dias da CAF encolheria para 1 a cada 30s).
            // Só a CAF tem janela: na Farmácia Central preservar isso faria as solicitações de
            // ontem sobreviverem para sempre depois da virada do dia.
            if (!completo && anterior != null && estoque == EstoqueCaf)
                foreach (var s in anterior.Itens)
                    if (DentroDaJanela(s, hoje) && vistos.Add(s.Numero)) itens.Add(s);

            var novos = Marcas(_memNovos, estoque);
            var atendidas = Marcas(_memAtendidas, estoque);
            DateTime agora = DateTime.Now;
            foreach (var s in itens)
            {
                string sitAnt;
                bool existia = antes.TryGetValue(s.Numero, out sitAnt);
                if (!existia) { if (tinhaAntes) novos[s.Numero] = agora; }
                else if (sitAnt != s.Situacao)
                {
                    novos.Remove(s.Numero);
                    // Verde = ATENDIDA. "Em atendimento" (A) segue como novidade, não como pronta.
                    if (s.Situacao == "S") atendidas[s.Numero] = agora;
                    else novos[s.Numero] = agora;
                }
            }
            ExpirarMarcas(novos, vistos, agora);
            ExpirarMarcas(atendidas, vistos, agora);

            itens.Sort(delegate(Solicitacao a, Solicitacao b) { return b.Numero.CompareTo(a.Numero); });
            GravarSnapshot(estoque, itens, maxNumero, agora, chave, anterior);
            _diaDaUltimaVarredura = hoje;

            if (_viva != null && !_viva.IsDisposed && !_viva.Disposing && _viva._estoque == estoque)
                _viva.AbsorverAtualizacao(itens, maxNumero, agora, res.AtingiuTeto);
        }

        private static string ChaveFiltro(string sit, string orig) { return sit + "|" + orig + "|0"; }

        // Busca do período do estoque: Farmácia Central = HOJE; CAF = últimos 7 dias (dia a dia,
        // porque o filtro de data por IGUALDADE é o único que pagina de forma estável no MV —
        // com ">=" o servidor espalha os registros e a varredura pula linhas).
        // 'hoje' vem de fora e não é relido: a varredura leva ~10s e pode cruzar a meia-noite.
        private static async Task<SolicitacoesResult> BuscarAsync(
            Session s, int estoque, string sit, string orig, bool completo, DateTime hoje)
        {
            int dias = (estoque == EstoqueCaf && completo) ? DiasCaf : 1;
            if (dias <= 1)
                return await MorphisClient.ListarDiaAsync(s, estoque, sit, orig, hoje);

            var res = new SolicitacoesResult();
            var vistos = new HashSet<long>();
            for (int i = dias - 1; i >= 0; i--)
            {
                var dia = await MorphisClient.ListarDiaAsync(s, estoque, sit, orig, hoje.AddDays(-i));
                foreach (var it in dia.Itens) if (vistos.Add(it.Numero)) res.Itens.Add(it);
                if (dia.AtingiuTeto) res.AtingiuTeto = true;
            }
            res.TotalServidor = res.Itens.Count;
            return res;
        }

        // A solicitação está DENTRO da janela de 7 dias e é de um dia ANTERIOR a hoje (hoje veio
        // do servidor agora). Sem data legível não preserva: o certo é deixar a varredura
        // completa recolocá-la, e não carregar para sempre um item que ninguém mais atualiza.
        private static bool DentroDaJanela(Solicitacao s, DateTime hoje)
        {
            DateTime d;
            if (!TentarData(s.Data, out d)) return false;
            return d < hoje && d >= hoje.AddDays(-(DiasCaf - 1));
        }
        private static bool TentarData(string dt, out DateTime val)
        {
            val = DateTime.MinValue;
            if (string.IsNullOrEmpty(dt)) return false;
            string d = dt.Length >= 10 ? dt.Substring(0, 10) : dt;
            return DateTime.TryParseExact(d, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out val);
        }

        private static bool PassaOrigemDe(Solicitacao s, string orig)
        {
            if (orig == null || orig.Length == 0) return true;
            string oe = s.OrigemEfetiva;
            if (orig == "AVU") return oe == "AVU" || oe == "SET";
            return oe == orig;
        }

        private static Dictionary<long, DateTime> Marcas(Dictionary<int, Dictionary<long, DateTime>> mapa, int estoque)
        {
            Dictionary<long, DateTime> h;
            if (!mapa.TryGetValue(estoque, out h) || h == null) { h = new Dictionary<long, DateTime>(); mapa[estoque] = h; }
            return h;
        }
        // Tira as marcas que saíram da lista e as que já passaram do tempo de realce.
        private static void ExpirarMarcas(Dictionary<long, DateTime> marcas, HashSet<long> naLista, DateTime agora)
        {
            var fora = new List<long>();
            foreach (var kv in marcas)
                if (!naLista.Contains(kv.Key) || (agora - kv.Value).TotalMinutes > MinutosDeRealce)
                    fora.Add(kv.Key);
            foreach (var n in fora) marcas.Remove(n);
        }

        private static SolicitacoesCache.Snapshot SnapshotEstatico(int estoque)
        {
            SolicitacoesCache.Snapshot s;
            if (_memoria.TryGetValue(estoque, out s) && s != null) return s;
            return SolicitacoesCache.Carregar(estoque);
        }

        // Foto de HOJE que este módulo mantém, para quem precisa da lista de pacientes do
        // hospital (a tela de Antimicrobianos). É melhor que uma varredura solta: acumula
        // todas as passadas do dia, então não perde quem apareceu de manhã e já foi atendido.
        // null = ainda não há foto de hoje; quem chamou que faça a própria busca.
        // Só vale se a foto foi tirada SEM filtro: com "Pendentes" ou uma origem escolhida
        // ela mostra um recorte, e quem a usasse como lista de pacientes perderia gente
        // sem perceber. Nesse caso devolve null e quem chamou faz a própria busca.
        public static List<Solicitacao> FotoDeHoje(int estoque)
        {
            var s = SnapshotEstatico(estoque);
            if (s == null || s.Itens.Count == 0 || s.Quando.Date != DateTime.Today) return null;
            if (s.FiltroKey != ChaveFiltro("", "")) return null;
            return new List<Solicitacao>(s.Itens);
        }

        // A mesma foto, só se for RECENTE: quem precisa da SITUAÇÃO de cada pedido quase em
        // tempo real (a aba Urgência (PAM)) não pode usar uma foto de minutos atrás — o monitor
        // pode estar parado, minimizado ou varrendo outro estoque. null = faça a própria busca.
        public static List<Solicitacao> FotoDeHoje(int estoque, int segundosMax)
        {
            var s = SnapshotEstatico(estoque);
            if (s == null || (DateTime.Now - s.Quando).TotalSeconds > segundosMax) return null;
            return FotoDeHoje(estoque);
        }

        // Grava em memória sempre; em DISCO só quando o conteúdo mudou de verdade — a varredura
        // é de 30 em 30 segundos e reescrever ~20 KB o dia inteiro sem necessidade trava a UI
        // em máquina lenta (e o arquivo pode estar em pasta de rede).
        private static void GravarSnapshot(int estoque, List<Solicitacao> itens, long maxNumero,
            DateTime quando, string chave, SolicitacoesCache.Snapshot anterior)
        {
            var snap = new SolicitacoesCache.Snapshot();
            snap.FiltroKey = chave; snap.Quando = quando; snap.MaxNumero = maxNumero;
            snap.Itens = new List<Solicitacao>(itens);
            _memoria[estoque] = snap;
            if (!MesmoConteudo(anterior, snap))
                SolicitacoesCache.Salvar(estoque, snap.FiltroKey, snap.Itens, snap.MaxNumero, snap.Quando);
        }

        private static bool MesmoConteudo(SolicitacoesCache.Snapshot a, SolicitacoesCache.Snapshot b)
        {
            if (a == null || b == null) return false;
            if (a.FiltroKey != b.FiltroKey || a.Itens.Count != b.Itens.Count) return false;
            var sitAnt = new Dictionary<long, string>();
            foreach (var s in a.Itens) sitAnt[s.Numero] = s.Situacao;
            foreach (var s in b.Itens)
            {
                string v;
                if (!sitAnt.TryGetValue(s.Numero, out v) || v != s.Situacao) return false;
            }
            return true;
        }

        // A view viva recebe o resultado da varredura de fundo e se redesenha.
        private void AbsorverAtualizacao(List<Solicitacao> itens, long maxNumero, DateTime quando, bool teto)
        {
            _itens.Clear(); _numeros.Clear();
            foreach (var s in itens) if (_numeros.Add(s.Numero)) _itens.Add(s);
            _maxNumero = maxNumero;
            _ultimaAtualizacao = quando;
            AbsorverMarcas();
            OrdenarDesc();

            // Não redesenhar com o MOUSE EM CIMA da grade: o clique no botão de imprimir de uma
            // linha é resolvido por índice, e uma varredura caindo entre o apontar e o clicar
            // faria imprimir a solicitação errada. Fica pendente e desenha ao sair da grade.
            // Sem adiamento ativo (_renderPendente=false) a guarda entra SEMPRE que o mouse
            // está lá; o teto de 2 min só derruba um adiamento já em curso, para o mouse
            // esquecido sobre a grade não congelar a tela para sempre.
            if (grid.Visible && MouseSobreAGrade() &&
                (!_renderPendente || (DateTime.Now - _adiadoDesde).TotalMinutes < 2))
            {
                if (!_renderPendente) { _renderPendente = true; _adiadoDesde = DateTime.Now; }
                AtualizarBanner(teto);
                return;
            }
            _renderPendente = false;
            RenderGrid();
            AtualizarBanner(teto);
        }

        private bool MouseSobreAGrade()
        {
            try { return grid.ClientRectangle.Contains(grid.PointToClient(Cursor.Position)); }
            catch { return false; }
        }

        // Copia para a tela os realces ainda VÁLIDOS (marcados nos últimos MinutosDeRealce).
        // Não zera: quem não estava olhando na hora ainda vê o que chegou há pouco, e quem volta
        // de outra aba encontra destacado o que entrou na ausência.
        private void AbsorverMarcas()
        {
            _novos.Clear(); _atendidas.Clear();
            DateTime agora = DateTime.Now;
            foreach (var kv in Marcas(_memNovos, _estoque))
                if (_numeros.Contains(kv.Key) && (agora - kv.Value).TotalMinutes <= MinutosDeRealce)
                    _novos.Add(kv.Key);
            foreach (var kv in Marcas(_memAtendidas, _estoque))
                if (_numeros.Contains(kv.Key) && (agora - kv.Value).TotalMinutes <= MinutosDeRealce)
                    _atendidas.Add(kv.Key);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            _viva = this;
            this.Disposed += new EventHandler(delegate { if (_viva == this) _viva = null; });
            if (_host.Session == null)
            {
                if (_host.Usuario == "PREVIEW") { CarregarExemplo(); return; }
                banner.SetState(StatusKind.Warning, "Faça login para consultar as solicitações.");
                lblVazio.Text = "Faça login para ver as solicitações.";
                return;
            }
            IniciarMonitor(_host);   // a partir da 1ª visita ao módulo, atualiza sozinho até o logout

            // Reabre no último estoque visto e restaura seu snapshot (memória da sessão ou
            // disco), voltando os COMBOS para o filtro que o gerou (a view é recriada a cada
            // visita; sem isso o combo resetado invalidaria o cache e dispararia uma carga
            // completa que o usuário não pediu). Nada é consultado sozinho na reabertura.
            _estoque = _ultimoEstoque;
            _trocandoUI = true; cmbEstoque.SelectedIndex = IndiceEstoque(_estoque); _trocandoUI = false;

            var snap = SnapshotDoEstoque(_estoque);
            if (snap != null && snap.Itens.Count > 0 && snap.Quando.Date == DateTime.Today
                && RestaurarFiltros(snap.FiltroKey))
            {
                LerFiltros();
                RestaurarDoSnapshot(snap);
                AtualizarBanner(false);
            }
            else
                CarregarTudo();             // primeira carga do dia (com indicador)
        }

        // Pré-visualização (--solic-preview): a grade com pacientes fictícios, sem tocar o
        // MV — mesmo padrão da aba Justificativas. Os exemplos cobrem de propósito os
        // valores mais largos de cada coluna (urgente com ⚠, "Em atendimento",
        // "Transferência", atendimento de 7 dígitos), que é o que o laço de screenshot
        // usa para conferir que nada trunca.
        private void CarregarExemplo()
        {
            _itens.Clear(); _numeros.Clear();
            _itens.Add(Ex(1834512, "P", "PRE", "UTI - UNIDADE DE TERAPIA INTENSIVA LADO B", "MARIA EXEMPLO DA SILVA", "Rosangela Exemplo Aparecida", "5300123", true));
            _itens.Add(Ex(1834508, "A", "TRA", "3º ANDAR - CLINICA MEDICA", "JOAO EXEMPLO PEREIRA", "Beatriz Exemplo Nunes", "5300087", false));
            _itens.Add(Ex(1834501, "A", "AVU", "PRONTO ATENDIMENTO MUNICIPAL", "ANA EXEMPLO SOUZA", "Carlos Exemplo Prado", "5299994", false));
            _itens.Add(Ex(1834496, "S", "PRE", "ALA PEDIATRICA", "CARLOS EXEMPLO LIMA", "Fernanda Exemplo Reis", "5299950", false));
            _itens.Add(Ex(1834490, "S", "DEV", "4º ANDAR - ORTOPEDIA", "PEDRO EXEMPLO ROCHA", "Julia Exemplo Costa", "5299911", false));
            foreach (var s in _itens) _numeros.Add(s.Numero);
            _ultimaAtualizacao = DateTime.Now;
            OrdenarDesc();
            RenderGrid();
            banner.SetState(StatusKind.Info, "Pré-visualização: pacientes fictícios, sem consultar o MV.");
        }

        private static Solicitacao Ex(long numero, string sit, string orig, string setor,
            string paciente, string solicitante, string atend, bool urgente)
        {
            return new Solicitacao { Numero = numero, Estoque = 5, Situacao = sit, Origem = orig,
                Setor = setor, Data = DateTime.Today.ToString("dd/MM/yyyy"),
                Hora = DateTime.Now.ToString("HH:mm:ss"), Urgente = urgente,
                Solicitante = solicitante, Atendimento = atend, Paciente = paciente, TpSol = "P" };
        }

        // Troca de estoque (Farmácia Central ↔ CAF): mantém os filtros atuais e mostra o
        // cache do novo estoque se casar; senão faz a consulta completa. Cada estoque tem
        // seu próprio estado em memória, então a volta é instantânea.
        private void OnEstoqueMudou(object sender, EventArgs e)
        {
            if (_trocandoUI || _busy) return;
            int novo = EstoqueSelecionado();
            if (novo == _estoque) return;
            _estoque = novo; _ultimoEstoque = novo;

            LerFiltros();
            var snap = SnapshotDoEstoque(_estoque);
            if (snap != null && snap.Itens.Count > 0 && snap.Quando.Date == DateTime.Today && snap.FiltroKey == FiltroKey())
            {
                RestaurarDoSnapshot(snap);
                AtualizarBanner(false);
            }
            else
                CarregarTudo();
        }

        private int EstoqueSelecionado()
        {
            var op = cmbEstoque.SelectedItem as OpcaoItem;
            int v; return (op != null && int.TryParse(op.Valor, out v)) ? v : 5;
        }
        private int IndiceEstoque(int estoque)
        {
            for (int i = 0; i < cmbEstoque.Items.Count; i++)
            {
                var op = cmbEstoque.Items[i] as OpcaoItem;
                int v;
                if (op != null && int.TryParse(op.Valor, out v) && v == estoque) return i;
            }
            return 0;
        }
        private SolicitacoesCache.Snapshot SnapshotDoEstoque(int estoque) { return SnapshotEstatico(estoque); }

        // Zera o estado local e o repovoa a partir de um snapshot (memória ou disco). Os realces
        // vêm das marcas do estoque: quem volta ao módulo vê o que chegou enquanto estava fora.
        private void RestaurarDoSnapshot(SolicitacoesCache.Snapshot snap)
        {
            _itens.Clear(); _numeros.Clear(); _maxNumero = snap.MaxNumero;
            foreach (var s in snap.Itens) if (_numeros.Add(s.Numero)) _itens.Add(s);
            AbsorverMarcas();
            _ultimaAtualizacao = snap.Quando;
            OrdenarDesc(); RenderGrid();
        }

        // Aplica nos combos o filtro gravado no snapshot ("sit|orig|dias"; período fixo em
        // Hoje, então o 3º campo é ignorado). false se não der.
        private bool RestaurarFiltros(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string[] p = key.Split('|');
            if (p.Length != 3) return false;
            int iSit = IndiceOpcao(cmbSituacao, p[0]);
            int iOrig = IndiceOpcao(cmbOrigem, p[1]);
            if (iSit < 0 || iOrig < 0) return false;
            cmbSituacao.SelectedIndex = iSit; cmbOrigem.SelectedIndex = iOrig;
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

        // "Recarregar tudo" (reset de emergência): joga fora o que está na tela — itens, realces
        // e marca d'água — e refaz a varredura completa do zero. É a saída para quando algum
        // registro parecer inconsistente; o dia a dia é a atualização automática de 30s.
        private void OnRecarregar(object sender, EventArgs e)
        {
            CarregarTudo();
        }

        // ===== ATALHOS GLOBAIS (F5 / Ctrl+F, roteados pela MainWindow) =====
        // F5 dispara a MESMA varredura leve do monitor (só hoje, sem limpar a tela nem os
        // realces), na hora — não o "Recarregar tudo" completo, que continua no botão.
        public void ComandoAtualizar()
        {
            AtualizarAgora();
        }

        public void ComandoFocarBusca()
        {
            if (txtBusca == null || !txtBusca.Enabled) return;
            txtBusca.Focus();
            txtBusca.SelectAll();
        }

        private bool _atualizandoF5;   // reentrância: a tecla repete se ficar pressionada
        private async void AtualizarAgora()
        {
            if (_busy || _imprimindo || _atualizandoF5) return;
            if (_host.Session == null)
            {
                banner.SetState(StatusKind.Warning, "Faça login para consultar as solicitações.");
                return;
            }
            // Se a sessão expirou em segundo plano, o monitor parou e zerou o host — sem
            // religar aqui, a varredura abaixo retornaria muda e o banner ficaria preso em
            // "Atualizando...". Religar é idempotente e devolve o host à varredura; se a
            // sessão estiver morta mesmo, a exceção sobe e cai no catch, que leva ao login.
            IniciarMonitor(_host);
            if (!MvGate.Entrar("solicitacoes-f5"))
            {
                banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes.");
                return;
            }
            _atualizandoF5 = true;
            banner.SetState(StatusKind.Busy, "Atualizando as solicitações...");
            int sessao = _sessaoId;
            try
            {
                // Igual ao monitor: varredura barata de HOJE, com os filtros já aplicados;
                // completa só se o dia virou. O banner volta ao normal na absorção.
                bool completo = _diaDaUltimaVarredura.Date != DateTime.Today;
                await AtualizarEstoqueAsync(_estoque, completo, false, sessao);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error, "Erro ao atualizar as solicitações: " + ex.Message);
            }
            finally { MvGate.Sair(); _atualizandoF5 = false; }
        }

        private void LerFiltros()
        {
            _sit = (cmbSituacao.SelectedItem as OpcaoItem).Valor;
            _orig = (cmbOrigem.SelectedItem as OpcaoItem).Valor;
            // Sem seletor de período: Farmácia Central = hoje, CAF = 7 dias (ver BuscarAsync).
            // O monitor de fundo passa a usar os mesmos filtros (ele roda sem a view na tela).
            _monSit = _sit; _monOrig = _orig;
        }
        // MESMO formato que o monitor grava no snapshot (ChaveFiltro): se divergirem, o cache
        // nunca casa e toda abertura do módulo dispara varredura completa sem necessidade.
        private string FiltroKey() { return ChaveFiltro(_sit, _orig); }

        // Carga COMPLETA, com feedback na tela: refaz o período inteiro do estoque (7 dias na
        // CAF) do zero. Reusa exatamente o motor do monitor de fundo, para a grade e o snapshot
        // nunca divergirem conforme quem disparou a busca. A varredura LEVE (só hoje, sem
        // limpar a tela) é a do monitor de 30s, que chama AtualizarEstoqueAsync direto.
        private async void CarregarTudo()
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy || _imprimindo) return;
            _busy = true;
            LerFiltros();
            TravarControles("Carregando...");
            banner.SetState(StatusKind.Busy, "Carregando solicitações...");

            // Pega a vez ANTES de mexer na tela: se o servidor estiver ocupado com outra
            // consulta, a grade não pode ficar vazia com "Carregando..." congelado.
            bool tenho = await MvGate.EntrarAsync("solicitacoes", 60000);
            if (!tenho || IsDisposed || Disposing)
            {
                // Soltar o portão SEMPRE que ele foi obtido: sair daqui segurando (view
                // descartada no meio) mataria a atualização automática em silêncio.
                if (tenho) MvGate.Sair();
                _busy = false;
                if (!IsDisposed && !Disposing)
                {
                    DestravarControles();
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes.");
                }
                return;
            }

            // Zera tela e estado ANTES da consulta: se ela falhar (servidor lento), não pode
            // sobrar estado do estoque anterior — senão a varredura de 30s veria _itens/_maxNumero
            // antigos e sincronizaria contra o estoque errado.
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true;
            lblVazio.Text = "Carregando solicitações...";
            _itens.Clear(); _numeros.Clear(); _novos.Clear(); _atendidas.Clear();
            _maxNumero = 0; _ultimaAtualizacao = DateTime.MinValue;
            Marcas(_memNovos, _estoque).Clear(); Marcas(_memAtendidas, _estoque).Clear();

            try
            {
                await AtualizarEstoqueAsync(_estoque, true /*completo*/, true /*ignorarDiff*/, _sessaoId);
                if (IsDisposed || Disposing) return;
                if (_itens.Count == 0) AtualizarBanner(false);   // sem itens não há absorção
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
                lblVazio.Text = "Falha na busca. Tente “Recarregar tudo”.";
            }
            finally
            {
                MvGate.Sair();
                _busy = false;
                if (!IsDisposed && !Disposing) DestravarControles();
            }
        }

        // Durante uma consulta, tudo que muda o QUE está sendo mostrado fica travado
        // (o botão, os filtros e a busca) — senão a grade termina com dados de um filtro sob
        // os rótulos de outro, ou a busca redesenha por cima do "Carregando...".
        private void TravarControles(string texto)
        {
            btnRecarregar.Enabled = false; btnRecarregar.Text = texto;
            cmbEstoque.Enabled = false;
            cmbSituacao.Enabled = false; cmbOrigem.Enabled = false;
            txtBusca.Enabled = false;
        }
        private void DestravarControles()
        {
            btnRecarregar.Enabled = true; btnRecarregar.Text = "Recarregar tudo";
            cmbEstoque.Enabled = true;
            cmbSituacao.Enabled = true; cmbOrigem.Enabled = true;
            txtBusca.Enabled = true;
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

            // Guarda onde a pessoa estava, pelo NÚMERO da solicitação e não pelo índice: as
            // novas entram no TOPO (ordem decrescente) e empurram tudo para baixo, então
            // restaurar o índice deixaria o conteúdo deslizar sob os olhos de quem está lendo.
            long topoNumero = 0;
            try
            {
                int i0 = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
                if (i0 >= 0 && i0 < grid.Rows.Count)
                {
                    var topo = grid.Rows[i0].Tag as Solicitacao;
                    if (topo != null) topoNumero = topo.Numero;
                }
            }
            catch { }
            long selecionadaAntes = 0;
            // Só seleção REAL: o CurrentRow existe mesmo sem seleção (célula corrente que
            // sobra da 1ª exibição) e restaurá-lo pintaria uma linha que ninguém escolheu.
            if (grid.CurrentRow != null && grid.CurrentRow.Selected)
            {
                var sel = grid.CurrentRow.Tag as Solicitacao;
                if (sel != null) selecionadaAntes = sel.Numero;
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var s in mostrar)
            {
                // (SetorCurto abrevia SÓ a exibição; o dado cru segue no Tag e na busca.)
                // "dd/MM HH:mm": o ano não ajuda numa janela de no máximo 7 dias, e a
                // coluna encurtada devolve a largura para paciente e setor.
                string hora = (s.Hora != null && s.Hora.Length > 5) ? s.Hora.Substring(0, 5) : s.Hora;
                int r = grid.Rows.Add(
                    s.Numero.ToString(),
                    (s.Data != null && s.Data.Length >= 10 ? s.Data.Substring(0, 5) : s.Data) + "  " + hora,
                    s.SituacaoLabel, s.OrigemLabel,
                    string.IsNullOrEmpty(s.Paciente) ? "—" : s.Paciente,
                    SetorCurto(s.Setor), s.Solicitante, s.Atendimento);
                grid.Rows[r].Tag = s;
                grid.Rows[r].Cells["imprimir"].ToolTipText = "Abrir o PDF da solicitação";
                // O nome COMPLETO do setor sobrevive no tooltip (e a busca continua
                // enxergando o texto original, que vem do Tag, não da célula).
                if (SetorCurto(s.Setor) != s.Setor)
                    grid.Rows[r].Cells["setor"].ToolTipText = s.Setor;
                if (_novos.Contains(s.Numero)) grid.Rows[r].DefaultCellStyle.BackColor = Theme.InfoBg;          // nova (azul)
                else if (_atendidas.Contains(s.Numero)) grid.Rows[r].DefaultCellStyle.BackColor = Theme.SuccessBg; // recém-atendida (verde)
            }

            bool filtrando = mostrar != _itens;
            bool tem = mostrar.Count > 0;
            lblVazio.Text = (filtrando && _itens.Count > 0)
                ? "Nada encontrado para essa busca."
                : "Nenhuma solicitação para os filtros escolhidos.";
            grid.Visible = tem; lblVazio.Visible = !tem;
            grid.LimparSelecao();   // ClearSelection sozinho deixaria a célula corrente fantasma
            grid.ResumeLayout();

            // Devolve a posição de leitura: mesma linha selecionada e a MESMA solicitação no
            // topo da área visível (assim o que está na tela não se mexe quando chegam novas).
            if (tem)
            {
                try
                {
                    if (selecionadaAntes != 0)
                        foreach (DataGridViewRow r in grid.Rows)
                        {
                            var s = r.Tag as Solicitacao;
                            if (s != null && s.Numero == selecionadaAntes)
                            { grid.CurrentCell = r.Cells[0]; break; }
                        }
                    if (topoNumero != 0)
                        foreach (DataGridViewRow r in grid.Rows)
                        {
                            var s = r.Tag as Solicitacao;
                            if (s != null && s.Numero == topoNumero)
                            { grid.FirstDisplayedScrollingRowIndex = r.Index; break; }
                        }
                }
                catch { }
            }
        }

        // Banner enxuto e fiel: só a hora da última atualização — as contagens saíram a
        // pedido (o que chegou ou mudou já aparece realçado na própria grade). O verde
        // continua marcando a varredura que trouxe novidade.
        private void AtualizarBanner(bool teto)
        {
            string periodo = _estoque == EstoqueCaf ? "últimos " + DiasCaf + " dias" : "hoje";

            if (teto)
                banner.SetState(StatusKind.Warning,
                    "Muitas solicitações no período — mostrando as " + _itens.Count + " mais recentes. Filtre a situação.");
            else if (_itens.Count > 0)
                banner.SetState(_novos.Count > 0 || _atendidas.Count > 0 ? StatusKind.Success : StatusKind.Info,
                    "Atualizado às " + _ultimaAtualizacao.ToString("HH:mm:ss") + " (" + periodo + ").");
            else
                banner.SetState(StatusKind.Info, "Nenhuma solicitação (" + periodo + ") para os filtros escolhidos.");
        }

    }
}

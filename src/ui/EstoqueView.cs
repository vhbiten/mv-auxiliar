using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Estoque": acompanhamento do estoque da CAF para decidir o pedido de COMPRAS
    // (que sai por planilha ao setor de compras — fora do MV). 100% LEITURA: gera relatórios
    // no MV (movimentações + posição por setor + cadastro de críticos), cruza tudo e mostra
    //   o que saiu da CAF no período · saldo atual · cobertura em dias · saldo nos setores,
    // classificado por criticidade (régua do CFF p/ ciclo semanal + mínimo/PP do MV) e
    // listado em ordem alfabética (pedido do dono em 04/09/2026).
    // A decisão de compra acontece olhando a grade (a exportação CSV foi aposentada a
    // pedido do farmacêutico em 23/08/2026). Nada aqui grava no MV.
    public class EstoqueView : UserControl, IComandosDeTela
    {
        private const int EstoqueCaf = 2;

        // Orçamento da barra p/ janela mínima (base 96 DPI): padding do cartão 40 +
        // 156+190+114+136 = 596 fixos → 636 ≤ ~709 úteis (folga desde que a exportação
        // CSV foi aposentada). As larguras têm sobra de propósito: no monitor da
        // farmácia (96 DPI) a fonte rende ~8% mais larga que em 125% e, no aperto,
        // "Crítico + atenção" e "30 dias" encostavam no chevron. TIPO comporta
        // "Medicamentos" inteiro na face fechada; SITUAÇÃO, "Crítico + atenção".
        private const int LarguraGerar = 136;

        private readonly MainWindow _host;
        private StyledComboBox cmbTipo;
        private StyledComboBox cmbSituacao;
        private StyledComboBox cmbPeriodo;
        private PrimaryButton btnGerar;
        private TextBox txtBusca;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;

        private bool _busy;
        // O banner está exibindo o aviso de divergência do período? Só assim dá para
        // DESFAZÊ-LO quando o combo volta a bater com a análise, sem atropelar o
        // "Atualizado às ..." de quem acabou de gerar e nunca mexeu no período.
        private bool _avisoPeriodo;
        private System.Windows.Forms.Timer _esperaBusca;
        private AnaliseResultado _resultado;
        private readonly List<ProdutoEstoque> _visiveis = new List<ProdutoEstoque>();
        // Setores que viraram colunas da grade (ordem do arquivo NO MOMENTO da construção).
        // Um cache salvo com outra lista/ordem é casado por CÓDIGO no RenderGrid — nunca
        // por índice, senão o saldo de um setor sairia sob o cabeçalho de outro.
        private List<SetorComparacao> _setoresGrade;

        // A análise sobrevive à troca de módulo (gerar custa ~30s). O cache em DISCO cobre
        // reabrir o app; isto aqui cobre só a navegação entre abas na mesma sessão.
        // Padrão de abertura definido pelo dono (31/08/2026): Todos · Crítico + atenção ·
        // 7 dias — a tela abre já na lista de trabalho da compra da semana.
        private static AnaliseResultado _memResultado;
        private static string _memTipo = "T";
        private static string _memSituacao = "CA";
        private static int _memPeriodo = 7;

        public static void LimparMemoria()
        {
            _memResultado = null;
            _memTipo = "T"; _memSituacao = "CA"; _memPeriodo = 7;
        }

        public EstoqueView(MainWindow host)
        {
            _host = host;
            this.Dock = DockStyle.Fill;
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 3;
            // Percent: sem isso a coluna cresce até o "preferido" e estoura o viewport.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            root.Controls.Add(BuildOpcoesCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultCard(), 0, 2);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
        }

        // ---- Cartão de opções ----
        private Control BuildOpcoesCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(14));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 5;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(156))); // tipo ("Medicamentos")
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(190))); // situação ("Crítico + atenção")
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(114))); // período ("30 dias" + seta)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(LarguraGerar)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tl.Controls.Add(MicroLabel("TIPO"), 0, 0);
            tl.Controls.Add(MicroLabel("SITUAÇÃO"), 1, 0);
            tl.Controls.Add(MicroLabel("PERÍODO"), 2, 0);

            cmbTipo = NovoCombo(new object[] {
                new OpcaoItem("Todos", "T"),
                new OpcaoItem("Medicamentos", "1"),
                new OpcaoItem("Materiais", "2") });
            // A escolha é gravada AQUI (e não no RenderGrid, que sai cedo sem análise): os
            // três campos da barra têm que sobreviver à troca de aba do mesmo jeito, com ou
            // sem análise em tela — o ShellView recria a view a cada visita.
            cmbTipo.SelectedIndexChanged += new EventHandler(delegate
            {
                _memTipo = (cmbTipo.SelectedItem as OpcaoItem).Valor;
                if (!_busy) RenderGrid();
            });
            tl.Controls.Add(cmbTipo, 0, 1);

            // Mesmos nomes da coluna Situação da grade — um vocabulário só. As faixas que
            // viram pedido vêm primeiro (a de cima é o padrão de abertura) e "Sem giro"
            // saiu a pedido do dono em 31/08/2026; "Todas" FICA, no fim, como a saída para
            // achar um produto que está OK — sem ela a busca não alcançaria 60% da análise.
            cmbSituacao = NovoCombo(new object[] {
                new OpcaoItem("Crítico + atenção", "CA"),
                new OpcaoItem("Crítico", "C"),
                new OpcaoItem("Atenção", "A"),
                new OpcaoItem("Todas", "T") });
            // Sem DropDownWidth: a face fechada tem 176 (coluna 190 − margem 14) e o item
            // mais largo, "Crítico + atenção", pede 135 com o recuo do OnDrawItem — o ajuste
            // que existia aqui pedia 170 e o Windows o limita à largura do próprio controle,
            // então não fazia nada desde que a coluna passou de 176 para 190.
            cmbSituacao.SelectedIndexChanged += new EventHandler(delegate
            {
                _memSituacao = (cmbSituacao.SelectedItem as OpcaoItem).Valor;
                if (!_busy) RenderGrid();
            });
            tl.Controls.Add(cmbSituacao, 1, 1);

            // 7 dias é o padrão (a compra da CAF é decidida por rodada semanal); os prazos
            // longos ficam para conferir a tendência de item de baixo giro. Quem não sai
            // na janela curta continua classificado pela média de 4 meses (AnaliseEstoque),
            // então o filtro de 7 dias não some com o item de saída esporádica.
            cmbPeriodo = NovoCombo(new object[] {
                new OpcaoItem("7 dias", "7"),
                new OpcaoItem("30 dias", "30"),
                new OpcaoItem("60 dias", "60"),
                new OpcaoItem("90 dias", "90") });
            // Mudar o período NÃO refiltra (a análise em tela foi calculada com o dela): só
            // guarda a escolha e reescreve o banner.
            cmbPeriodo.SelectedIndexChanged += new EventHandler(delegate
            {
                int novo;
                if (!int.TryParse((cmbPeriodo.SelectedItem as OpcaoItem).Valor,
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out novo)) return;
                // Antes do return de baixo: a escolha do período tem que sobreviver à troca
                // de aba como as de TIPO e SITUAÇÃO (o ShellView recria a view a cada visita).
                _memPeriodo = novo;
                if (_busy || _resultado == null) return;
                // Quem redige é sempre o BannerIdade — um vocabulário só, e o aviso de
                // divergência SOME sozinho quando o combo volta a bater com a análise (sem
                // isto o banner ficava acusando divergência já resolvida e mandando gerar
                // 30s à toa). Só reescreve quando a divergência entra ou sai de cena, para
                // não apagar o "Atualizado às ..." de quem acabou de gerar.
                if (novo != _resultado.DiasPeriodo || _avisoPeriodo) BannerIdade("Análise de ");
            });
            tl.Controls.Add(cmbPeriodo, 2, 1);

            btnGerar = new PrimaryButton();
            btnGerar.Text = "Gerar análise";
            btnGerar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnGerar.Height = Theme.S(40);
            btnGerar.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnGerar.Click += new EventHandler(OnGerar);
            tl.Controls.Add(btnGerar, 3, 1);

            var dica = new ToolTip();
            // Vocabulário do dono (29/08/2026), sem o "clique em Gerar análise" que a versão
            // anterior repetia em cima do próprio botão: aqui o texto diz o que a ação FAZ e
            // quanto custa esperar. O detalhe do que fica de fora da análise (não
            // padronizados, 4 meses sem movimento, estoque-excluidos.txt) continua fora:
            // era jargão para quem opera a tela.
            // "movimentações dos setores" era falso: dos setores a análise lê SALDO
            // (R_POS_EST_S), não movimentação. Só a CAF tem relatório de movimentação aqui.
            dica.SetToolTip(btnGerar, "Verifica no MV Soul o que saiu da CAF no período e os saldos atuais da CAF e dos setores. Leva menos de um minuto.");
            // A espécie vem do CD_ESPECIE do cadastro, lido pelos relatórios filtrados por
            // espécie (AnaliseEstoque.MarcarEspecie); "outra espécie" = as 10 que não são
            // medicamento nem material (sacos plásticos, reagentes, papel grau cirúrgico...).
            dica.SetToolTip(cmbTipo, "Espécie do produto no cadastro do MV. Produto de outra espécie, ou sem espécie nesta análise, aparece só em “Todos”.");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });

            card.Controls.Add(tl);
            return card;
        }

        private StyledComboBox NovoCombo(object[] itens)
        {
            var c = new StyledComboBox();
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            c.Margin = new Padding(0, 0, Theme.S(14), 0);
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

        // ---- Cartão do resultado ----
        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            // Sem o título "Estoque da CAF" (a pedido): a linha de cima é só a busca.
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1; tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            tl.Controls.Add(BuildBusca(), 0, 0);

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            // O banner logo acima já dá a instrução ("clique em Gerar análise"); aqui o
            // texto conta o ESTADO e o custo da espera, para os dois não dizerem o mesmo.
            lblVazio.Text = "Nenhuma análise nesta tela ainda. A consulta ao MV Soul leva menos de um minuto.";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 1);

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            // Elástica (padrão das outras telas): nasce estreita para a coluna Percent não
            // inflar o "preferido" da barra e estica até 420 quando há espaço.
            hostBusca.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            hostBusca.Width = Theme.S(40); hostBusca.Height = Theme.S(34);
            hostBusca.MaximumSize = new Size(Theme.S(420), Theme.S(34));
            hostBusca.Margin = new Padding(Theme.S(18), 2, Theme.S(12), Theme.S(10));
            hostBusca.Padding = new Padding(Theme.S(10), Theme.S(7), Theme.S(8), Theme.S(6));

            var lupa = new Label();
            lupa.Text = ((char)0xE721).ToString();   // Search (Segoe MDL2 Assets)
            lupa.Font = Theme.Icon; lupa.ForeColor = Theme.TextSoft; lupa.BackColor = Color.White;
            lupa.Dock = DockStyle.Left; lupa.Width = Theme.S(24);
            lupa.TextAlign = ContentAlignment.MiddleLeft;

            txtBusca = new TextBox();
            txtBusca.BorderStyle = BorderStyle.None;
            txtBusca.Font = Theme.Body; txtBusca.ForeColor = Theme.TextDark; txtBusca.BackColor = Color.White;
            txtBusca.Dock = DockStyle.Fill;
            _esperaBusca = new System.Windows.Forms.Timer();
            _esperaBusca.Interval = 180;
            _esperaBusca.Tick += new EventHandler(delegate { _esperaBusca.Stop(); if (!_busy) RenderGrid(); });
            txtBusca.TextChanged += new EventHandler(delegate { _esperaBusca.Stop(); _esperaBusca.Start(); });
            this.Disposed += new EventHandler(delegate { _esperaBusca.Dispose(); });
            txtBusca.KeyDown += new KeyEventHandler(delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape && txtBusca.Text.Length > 0)
                { txtBusca.Text = ""; ke.SuppressKeyPress = true; }
            });
            txtBusca.HandleCreated += new EventHandler(delegate
            {
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar produto ou código");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            return hostBusca;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private GridPro NovaGrid()
        {
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;

            // Larguras BASE (96 DPI), medidas por screenshot na janela MÍNIMA (1000px), onde
            // a área de coluna da grade é de exatamente 641px (já descontada a barra de
            // rolagem vertical). As colunas numéricas usam padding S(2) (o S(6) do GridPro,
            // em colunas estreitas, desperdiçava dezenas de px e clipava os cabeçalhos):
            // fixas 536 + produto mín. 102 = 638 cabem sem scroll lateral.
            // Cada largura foi conferida contra o texto MAIS LARGO que a coluna pode
            // receber, medido com TextRenderer em Segoe UI 10 sobre a análise real de
            // 29/08 (763 itens) — largura útil = largura - 4 (o padding dos dois lados).
            // A sobra é de propósito: no monitor da farmácia (96 DPI) a fonte rende ~8%
            // mais larga que em 125%.
            // A coluna Produto é Fill e fica com o que sobra — mas ela NÃO usa o padding
            // S(2) daqui e sim o S(6) do GridPro, então a largura útil dela é a largura
            // menos 12. Medido: na janela padrão (1180) sobram 285px → 273 úteis, e ~1/3
            // dos nomes do filtro Crítico + atenção ainda passam disso (o mais longo, um
            // cateter port-a-cath, pede 410px; em "Todas" o teto sobe para 478px). Não vale
            // perseguir a largura que não trunca — por isso o nome inteiro abre o tooltip.
            // Mais que 4 setores em dados\estoque-setores.txt estoura na janela mínima
            // (382 fixos fora setor + 64n ≤ 641 → n = 4): o 5º já vira scroll lateral lá.
            // Ordem e formato do farmacêutico (revisados em 29/08/2026): Saiu por ÚLTIMO
            // (depois da Situação) e em NEGRITO; sem coluna de cobertura (a régua segue
            // viva na Situação e no tooltip); números CENTRALIZADOS.
            // 72, não 66: em 96 DPI (monitor da farmácia) códigos de 7 dígitos ("9037803",
            // 65px) saíam "90378…" com 66.
            g.Columns.Add(Col("codigo", "Código", 72, false, DataGridViewContentAlignment.MiddleCenter));
            var prod = Col("produto", "Produto", 200, true, DataGridViewContentAlignment.MiddleLeft);
            // 102, não 120: o que as colunas numéricas ganharam saiu daqui. Na janela mínima
            // (641px de coluna) a Fill ainda devolve 105px, então este mínimo NÃO chega a
            // morder — ele é a rede para arredondamento de DPI.
            prod.MinimumWidth = Theme.S(102);
            g.Columns.Add(prod);
            // 60, não 54: saldo de 5 dígitos ("10.600"/"25.651", 52px) não cabia nos 50px
            // úteis e saía "10.60…". Teto: 5 dígitos — "100.000" (60px) passa dos 56 úteis.
            var caf = Col("caf", "CAF", 60, false, DataGridViewContentAlignment.MiddleCenter);
            caf.HeaderCell.ToolTipText = "Saldo atual na CAF.";
            g.Columns.Add(caf);

            // Colunas de setor: uma por linha de dados\estoque-setores.txt (ordem do arquivo).
            _setoresGrade = EstoqueSetores.Listar();
            for (int i = 0; i < _setoresGrade.Count; i++)
            {
                var c = Col("setor" + i, _setoresGrade[i].Nome, 64, false, DataGridViewContentAlignment.MiddleCenter);
                // Texto base; o RenderGrid acrescenta o motivo quando a coluna sai com "n/d".
                c.HeaderCell.ToolTipText = DicaSetor(_setoresGrade[i]);
                g.Columns.Add(c);
            }

            // 82, não 72: o texto mais largo é "● Sem giro" (75px, ainda alcançável por
            // "Todas") e sobravam 68 — "● Atenção", 71px, já não cabia no filtro padrão.
            var sit = Col("situacao", "Situação", 82, false, DataGridViewContentAlignment.MiddleCenter);
            // Uma faixa por linha: as quatro definições em fila, separadas por um símbolo,
            // viravam um parágrafo só que ninguém lia até o fim. E a última linha tem que
            // dizer a verdade sobre o AnaliseEstoque.Nivel: item com Saiu 0 vira Crítico ou
            // Atenção quando o cadastro do MV acusa falta ou quando a média de 4 meses
            // aperta a cobertura. A versão anterior prometia que Saiu 0 era sempre Sem giro
            // e 139 das 763 linhas da análise real desmentiam o texto.
            sit.HeaderCell.ToolTipText =
                "Crítico: o saldo está zerado, cobre menos de " + (int)AnaliseEstoque.CoberturaCritica +
                    " dias ou está abaixo do mínimo do MV.\n" +
                "Atenção: o saldo cobre menos de " + (int)AnaliseEstoque.CoberturaAtencao +
                    " dias ou chegou ao ponto de pedido do MV.\n" +
                "OK: o saldo cobre " + (int)AnaliseEstoque.CoberturaAtencao + " dias ou mais.\n" +
                "Sem giro: nada saiu no período e nada acusa falta.\n" +
                "Sem saída no período, a cobertura vem da média dos últimos 4 meses.";
            g.Columns.Add(sit);
            // Saiu por último e em negrito (pedido de 29/08/2026): é o número que decide a
            // compra, e fechando a linha ele fica ao lado da Situação que o resume.
            // 66, não 56: em negrito "11.750"/"14.851" pedem 53px e sobravam 52 — era o
            // "11.7…" que o dono viu (31/08/2026). Com 62 úteis vai até 6 dígitos
            // ("999.999" = 61px em negrito); o máximo real da base é "14.851".
            var saiu = Col("saiu", "Saiu", 66, false, DataGridViewContentAlignment.MiddleCenter);
            saiu.HeaderCell.ToolTipText = "Quanto saiu da CAF no período, somando as transferências aos setores e as saídas.";
            saiu.DefaultCellStyle.Font = Theme.BodyBold;
            g.Columns.Add(saiu);
            return g;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill,
            DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            // Cabeçalho acompanha a célula: coluna centralizada com título à esquerda
            // continuava parecendo torta.
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            c.ReadOnly = true;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else
            {
                // Colunas estreitas: padding mínimo (o S(6) do GridPro clipava os cabeçalhos).
                var pad = new Padding(Theme.S(2), 0, Theme.S(2), 0);
                c.HeaderCell.Style.Padding = pad;
                c.DefaultCellStyle.Padding = pad;
            }
            return c;
        }

        // ---- Estado inicial ----
        private void OnLoad(object sender, EventArgs e)
        {
            Selecionar(cmbTipo, _memTipo);
            Selecionar(cmbSituacao, _memSituacao);
            Selecionar(cmbPeriodo, _memPeriodo.ToString(CultureInfo.InvariantCulture));

            if (_memResultado == null)
            {
                var cache = EstoqueCache.Carregar();
                if (cache != null) _memResultado = cache;
            }
            if (_memResultado != null)
            {
                _resultado = _memResultado;
                // O combo PERÍODO fica no padrão/na escolha da sessão — ele é o parâmetro
                // da PRÓXIMA geração, não um rótulo do que está na grade. Quem conta a
                // verdade sobre a análise em tela é o banner (BannerIdade sempre imprime
                // "(N dias)" e avisa quando o combo diverge). Deixar o cache mandar no
                // combo, como era antes, fazia o padrão de 7 dias nunca aparecer em quem
                // já tinha uma análise de 30 dias salva em disco — e um clique em "Gerar
                // análise" regravava 30, fechando o ciclo.
                RenderGrid();
                BannerIdade("Análise de ");
                return;
            }
            // Instrução direta no lugar da explicação do cruzamento (decisão do dono em
            // 29/08/2026). Só "movimentações dos setores" mudou, porque a análise lê o
            // SALDO dos setores, não a movimentação deles.
            banner.SetState(StatusKind.Info,
                "Clique em “Gerar análise” para verificar as saídas da CAF e os saldos dos setores.");
        }

        // Aviso de que o campo PERÍODO não é o da análise em tela ("" quando batem). Vive
        // separado porque três banners precisam dele (idade, servidor ocupado e falha que
        // devolve a análise anterior) e porque é ele que liga/desliga o _avisoPeriodo.
        // O nome do campo aparece em CAIXA ALTA para casar com o micro-rótulo da barra.
        private string AlertaPeriodo()
        {
            _avisoPeriodo = false;
            if (_resultado == null || cmbPeriodo == null || cmbPeriodo.SelectedItem == null) return "";
            int sel;
            if (!int.TryParse((cmbPeriodo.SelectedItem as OpcaoItem).Valor,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out sel)) return "";
            if (sel == _resultado.DiasPeriodo) return "";
            _avisoPeriodo = true;
            return " O campo PERÍODO está em " + sel + " dias. Gere de novo para aplicar.";
        }

        // Idade da análise em tela + o aviso de divergência. Desde que o combo deixou de ser
        // sincronizado com o cache, este banner é o único lugar que amarra os dois números —
        // e "período de N dias" (em vez de "(N dias)" logo depois da hora) evita a leitura
        // errada de "análise de N dias atrás".
        private void BannerIdade(string prefixo)
        {
            if (_resultado == null) return;
            string texto = prefixo + _resultado.Quando.ToString("dd/MM 'às' HH:mm") +
                ", período de " + _resultado.DiasPeriodo + " dias.";
            bool velha = _resultado.Quando.Date != DateTime.Today;
            // O aviso de fonte que faltou vivia só no instante da geração (ResumoBanner):
            // ao reabrir a tela, uma análise parcial passava por completa. O cache guarda
            // CadastroMvOk, então dá para repetir o aviso sempre que a análise estiver em
            // tela. Setor que falhou não precisa entrar aqui: a coluna já sai com "n/d" e o
            // cabeçalho dela explica o motivo.
            if (!_resultado.CadastroMvOk)
                texto += " Nesta análise não veio o cadastro de mínimos do MV, então a coluna Situação saiu só pela cobertura em dias.";
            // Também vale para o cache salvo pela versão anterior a 04/09/2026, que nunca
            // buscou a espécie: o aviso explica o produto que falta em "Medicamentos" e o
            // "Gere de novo" do fim resolve.
            if (!_resultado.EspeciesOk)
                texto += " Parte dos produtos está sem espécie nesta análise e aparece só com TIPO em “Todos”.";
            if (velha) texto += " Os saldos podem ter mudado.";
            string alerta = AlertaPeriodo();
            texto += alerta.Length > 0 ? alerta : " Gere de novo para atualizar.";
            bool aviso = velha || !_resultado.CadastroMvOk || !_resultado.EspeciesOk;
            banner.SetState(aviso ? StatusKind.Warning : StatusKind.Info, texto);
        }

        private static void Selecionar(StyledComboBox cmb, string valor)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                var oi = cmb.Items[i] as OpcaoItem;
                if (oi != null && oi.Valor == valor) { cmb.SelectedIndex = i; return; }
            }
        }

        // ---- Geração ----
        private async void OnGerar(object sender, EventArgs e)
        {
            if (_host.Session == null)
            { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy) return;
            _busy = true;
            Travar();

            int dias = int.Parse((cmbPeriodo.SelectedItem as OpcaoItem).Valor, CultureInfo.InvariantCulture);
            DateTime de = DateTime.Today.AddDays(-dias);
            var setores = EstoqueSetores.Listar();
            // 8 = movimentação do período, 4 meses, posição da CAF, 4 relatórios por
            // espécie (medicamentos e materiais: movimento + posição) e o cadastro de críticos.
            int total = 8 + setores.Count;
            int passo = 0;

            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("estoque-analise", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    // A grade continua com a análise anterior: o alerta de período vem junto
                    // para o combo não ficar "solto" em cima de uma grade de outro período.
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes."
                        + AlertaPeriodo());
                    return;
                }

                grid.Visible = false; lblVazio.Visible = true;
                lblVazio.Text = "Consultando o MV Soul…";

                // Espécie 0 = todas: a CAF movimenta mais que medicamento/material, e um
                // item fora das duas espécies clássicas viraria "sem giro" falso.
                // soPadronizados=true em TODOS os relatórios: o flag do cadastro filtra na
                // origem (o marcador "(NÃO PADRÃO)" no nome é cortado depois, no Montar).
                Progresso(++passo, total, "o que saiu da CAF em " + dias + " dias");
                var mov = await MorphisClient.GerarMovimentacaoAsync(_host.Session, EstoqueCaf, 0, de, null, true);
                if (IsDisposed || Disposing) return;

                // Regra do farmacêutico: sem movimento nos últimos 4 meses = descontinuado
                // na prática, fora da análise. Este relatório é só o teste de existência.
                Progresso(++passo, total, "movimento dos últimos 4 meses");
                var mov4m = await MorphisClient.GerarMovimentacaoAsync(_host.Session, EstoqueCaf, 0,
                    DateTime.Today.AddDays(-120), null, true);
                if (IsDisposed || Disposing) return;

                Progresso(++passo, total, "saldo atual da CAF");
                var posCaf = await MorphisClient.GerarPosicaoPorEstoqueAsync(_host.Session, EstoqueCaf, 0, 0, true);
                if (IsDisposed || Disposing) return;

                // Espécie de cada produto, pelos mesmos dois relatórios FILTRADOS por espécie
                // (ver AnaliseEstoque.MarcarEspecie): o cadastro de críticos logo abaixo só
                // conhece produto com classificação ABC/XYZ, e sem isto 1 em 5 produtos
                // sumia do filtro "Medicamentos"/"Materiais". Enriquecimento: relatório que
                // falhar deixa esses produtos com espécie 0 e o banner avisa. Sem o filtro
                // de padronizados, como nos setores: o corte já aconteceu na base.
                var especies = new Dictionary<int, int>();
                bool especiesOk = true;
                foreach (int esp in new int[] { 1, 2 })
                {
                    string nomeEsp = esp == 1 ? "medicamentos" : "materiais";
                    List<MovProduto> movEsp = null;
                    List<SaldoProduto> posEsp = null;
                    Progresso(++passo, total, nomeEsp + " que saíram nos últimos 4 meses");
                    try
                    {
                        movEsp = (await MorphisClient.GerarMovimentacaoAsync(_host.Session, EstoqueCaf, esp,
                            DateTime.Today.AddDays(-120))).Produtos;
                    }
                    catch (SessaoExpiradaException) { throw; }
                    catch (Exception ex)
                    {
                        especiesOk = false;
                        Logger.Log("Estoque: movimentação de " + nomeEsp + " (espécie) falhou: " + ex.Message);
                    }
                    if (IsDisposed || Disposing) return;
                    Progresso(++passo, total, nomeEsp + " com saldo na CAF");
                    try
                    {
                        posEsp = (await MorphisClient.GerarPosicaoPorEstoqueAsync(_host.Session, EstoqueCaf, esp, 0)).Produtos;
                    }
                    catch (SessaoExpiradaException) { throw; }
                    catch (Exception ex)
                    {
                        especiesOk = false;
                        Logger.Log("Estoque: posição de " + nomeEsp + " (espécie) falhou: " + ex.Message);
                    }
                    if (IsDisposed || Disposing) return;
                    AnaliseEstoque.MarcarEspecie(especies, esp, movEsp, posEsp);
                }

                // Cadastro de mínimos/ponto de pedido: enriquecimento — a análise sobrevive sem ele.
                List<ProdCritItem> cadastro = null;
                Progresso(++passo, total, "cadastro de mínimos e ponto de pedido");
                try
                {
                    var pc = await MorphisClient.GerarProdutosCriticosAsync(_host.Session, EstoqueCaf, true);
                    cadastro = pc.Itens;
                }
                catch (SessaoExpiradaException) { throw; }
                catch (Exception ex) { Logger.Log("Estoque: cadastro de críticos indisponível: " + ex.Message); }
                if (IsDisposed || Disposing) return;

                var posSetores = new List<SaldoProduto>[setores.Count];
                var setorOk = new bool[setores.Count];
                var codigos = new int[setores.Count];
                var nomes = new string[setores.Count];
                for (int i = 0; i < setores.Count; i++)
                {
                    codigos[i] = setores[i].Codigo; nomes[i] = setores[i].Nome;
                    Progresso(++passo, total, "saldo em " + setores[i].Nome);
                    try
                    {
                        // Sem o filtro de padronizados: a coluna do setor é só consulta de
                        // saldo — e o corte de não-padronizados já aconteceu na base.
                        var ps = await MorphisClient.GerarPosicaoPorEstoqueAsync(_host.Session, setores[i].Codigo, 0, 0);
                        posSetores[i] = ps.Produtos; setorOk[i] = true;
                    }
                    catch (SessaoExpiradaException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Log("Estoque: posição do setor " + setores[i].Codigo + " falhou: " + ex.Message);
                        posSetores[i] = null; setorOk[i] = false;
                    }
                    if (IsDisposed || Disposing) return;
                }

                var r = AnaliseEstoque.Montar(mov.Produtos, posCaf.Produtos,
                    cadastro, codigos, nomes, posSetores, setorOk, dias, mov4m.Produtos, especies);
                // O Montar só sabe se recebeu o mapa; quem sabe se algum dos quatro
                // relatórios falhou é este laço.
                r.EspeciesOk = especiesOk;

                _resultado = r; _memResultado = r; _memPeriodo = dias;
                EstoqueCache.Salvar(r);

                RenderGrid();
                ResumoBanner(r, cadastro != null, setorOk);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                Logger.Log("Estoque: análise falhou: " + ex);
                if (!IsDisposed && !Disposing)
                {
                    if (_resultado != null)
                    {
                        // Devolve a análise anterior à tela — sem isto ficava "Consultando o
                        // MV Soul..." congelado com a grade oculta (e o Exportar exportando
                        // uma lista invisível).
                        RenderGrid();
                        banner.SetState(StatusKind.Error, "Não foi possível gerar a análise: " + Frase(ex.Message) +
                            " Mostrando a análise anterior, de " + _resultado.Quando.ToString("dd/MM 'às' HH:mm") +
                            ", período de " + _resultado.DiasPeriodo + " dias." + AlertaPeriodo());
                    }
                    else
                    {
                        banner.SetState(StatusKind.Error, "Não foi possível gerar a análise: " + Frase(ex.Message));
                        lblVazio.Text = "Clique em “Gerar análise” para tentar de novo.";
                    }
                }
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _busy = false;
                if (!IsDisposed && !Disposing) Destravar();
            }
        }

        private void Progresso(int passo, int total, string oQue)
        {
            banner.SetState(StatusKind.Busy, "Gerando análise (" + passo + " de " + total + "): " + oQue + "…");
        }

        // Só a hora, a pedido: as contagens saíram do banner (a grade e o filtro de
        // situação já contam). Os avisos de fonte que falhou ficam — sem eles a análise
        // parcial passaria por completa.
        private void ResumoBanner(AnaliseResultado r, bool cadastroOk, bool[] setorOk)
        {
            // Acabou de gerar: o combo e a análise batem por construção, então não há
            // divergência pendente para um clique no período ter que desfazer depois.
            _avisoPeriodo = false;
            // As fontes que falharam entram como uma lista de coisas que "não vieram", em
            // vez de repetir "não veio" em cada item separado por ponto-e-vírgula.
            var falhas = new List<string>();
            if (!cadastroOk) falhas.Add("o cadastro de mínimos do MV");
            for (int i = 0; i < setorOk.Length; i++)
                if (!setorOk[i]) falhas.Add("o saldo de " + r.SetorNomes[i]);
            // Por último na lista: é o único item que precisa de uma oração explicativa.
            if (!r.EspeciesOk) falhas.Add("a espécie de parte dos produtos, que por isso aparecem só com TIPO em “Todos”");

            string texto = "Atualizado às " + DateTime.Now.ToString("HH:mm:ss") + " (hoje).";
            if (falhas.Count == 1)
                banner.SetState(StatusKind.Warning, texto + " Aviso: não veio " + falhas[0] + ".");
            else if (falhas.Count > 1)
                banner.SetState(StatusKind.Warning, texto + " Aviso: não vieram " + Juntar(falhas) + ".");
            else
                banner.SetState(StatusKind.Success, texto);
        }

        private void Travar()
        {
            btnGerar.Enabled = false;
            cmbTipo.Enabled = false; cmbSituacao.Enabled = false; cmbPeriodo.Enabled = false;
            txtBusca.Enabled = false;
            _esperaBusca.Stop();
        }

        private void Destravar()
        {
            btnGerar.Enabled = true;
            cmbTipo.Enabled = true; cmbSituacao.Enabled = true; cmbPeriodo.Enabled = true;
            txtBusca.Enabled = true;
        }

        // ---- Render ----
        private void RenderGrid()
        {
            if (_resultado == null) return;

            // Os três _mem* são gravados nos handlers dos combos (interação do usuário);
            // o filtro lê direto da barra, que é a fonte da verdade do que está em tela.
            var r = _resultado;
            // O cabeçalho de Saiu tem que dizer de quantos dias é o número que decide a
            // compra: depois de gerar, o banner mostra só a hora e o combo não vale como
            // rótulo (ele é o parâmetro da PRÓXIMA geração).
            grid.Columns["saiu"].HeaderCell.ToolTipText = "Quanto saiu da CAF nos últimos " +
                r.DiasPeriodo + " dias, somando as transferências aos setores e as saídas.";
            var pt = CultureInfo.GetCultureInfo("pt-BR");
            string tipo = (cmbTipo.SelectedItem as OpcaoItem).Valor;
            string sit = (cmbSituacao.SelectedItem as OpcaoItem).Valor;
            string busca = txtBusca.Text.Trim();

            // Os três testes são métodos porque o texto de "nada encontrado" precisa
            // reaplicá-los de dois em dois para descobrir QUAL filtro escondeu o produto.
            _visiveis.Clear();
            foreach (var p in r.Itens)
            {
                if (!PassaTipo(p, tipo)) continue;
                if (!PassaSituacao(p, sit, r.DiasPeriodo)) continue;
                if (!CorrespondeBusca(p, busca)) continue;
                _visiveis.Add(p);
            }
            // Alfabética (pedido do dono em 04/09/2026). O Montar já entrega nessa ordem,
            // mas o cache salvo pela versão anterior vem por criticidade — reordenar aqui
            // é o que garante a grade certa em qualquer caso.
            _visiveis.Sort(AnaliseEstoque.CompararNomes);

            grid.SuspendLayout();
            grid.Rows.Clear();
            // A grade nasce com os setores do ARQUIVO; um cache antigo pode ter outra
            // lista/ordem. Casa POR CÓDIGO: mapa[i] = posição do setor da coluna i dentro
            // do resultado (-1 = o resultado não tem esse setor, a coluna sai "n/d").
            int nColSet = _setoresGrade.Count;
            var mapa = new int[nColSet];
            for (int i = 0; i < nColSet; i++)
            {
                mapa[i] = -1;
                for (int j = 0; j < r.SetorCodigos.Length; j++)
                    if (r.SetorCodigos[j] == _setoresGrade[i].Codigo) { mapa[i] = j; break; }
                // "n/d" sozinho na célula não diz se o setor falhou ou se ficou de fora da
                // análise: o motivo vai para o cabeçalho da coluna.
                string dica = DicaSetor(_setoresGrade[i]);
                if (mapa[i] < 0)
                    dica += " Esta análise não inclui o setor. Gere de novo para trazer a coluna.";
                else if (r.SetorOk != null && mapa[i] < r.SetorOk.Length && !r.SetorOk[mapa[i]])
                    dica += " O relatório deste setor não veio nesta análise, por isso a coluna está com n/d.";
                grid.Columns["setor" + i].HeaderCell.ToolTipText = dica;
            }

            foreach (var p in _visiveis)
            {
                int nivel = AnaliseEstoque.Nivel(p, r.DiasPeriodo);
                // Ordem FÍSICA das colunas: código, produto, CAF, setores, situação, Saiu
                // (o Saiu fecha a linha, em negrito — pedido de 29/08/2026).
                var vals = new object[4 + nColSet + 1];
                vals[0] = p.Codigo;
                vals[1] = p.Nome;
                vals[2] = Fmt(p.SaldoCaf, pt);
                for (int i = 0; i < nColSet; i++)
                {
                    int j = mapa[i];
                    vals[3 + i] = (j < 0 || (r.SetorOk != null && j < r.SetorOk.Length && !r.SetorOk[j]))
                        ? "n/d" : Fmt(p.SaldoSetores[j], pt);
                }
                int idxSit = 3 + nColSet;
                vals[idxSit] = "● " + AnaliseEstoque.NomeNivel(nivel);
                vals[idxSit + 1] = Fmt(p.Consumo, pt);

                int row = grid.Rows.Add(vals);
                var cor = CorNivel(nivel);
                grid.Rows[row].Cells[idxSit].Style.ForeColor = cor;
                grid.Rows[row].Cells[idxSit].Style.SelectionForeColor = cor;
                string dica = TooltipLinha(p, nivel, r);
                grid.Rows[row].Cells[1].ToolTipText = dica;
                grid.Rows[row].Cells[idxSit].ToolTipText = dica;
            }
            grid.ResumeLayout();

            bool tem = _visiveis.Count > 0;
            grid.Visible = tem;
            lblVazio.Visible = !tem;
            if (!tem)
            {
                if (r.Itens.Count == 0)
                    lblVazio.Text = "A análise não trouxe produtos.";
                else if (!r.CadastroMvOk && !r.EspeciesOk && tipo != "T")
                    // Sem o R_PROD_CRIT e sem os relatórios por espécie ninguém tem
                    // espécie: o filtro de tipo fica cego.
                    lblVazio.Text = "Nesta análise não veio a espécie dos produtos, então não dá para separar por tipo. Escolha “Todos” em TIPO.";
                else
                    lblVazio.Text = TextoNadaEncontrado(r, tipo, sit, busca);
            }
            if (tem) grid.LimparSelecao();
        }

        private static bool PassaTipo(ProdutoEstoque p, string tipo)
        {
            return tipo == "T" || p.Especie.ToString(CultureInfo.InvariantCulture) == tipo;
        }

        // "Todas" passa tudo; OK e Sem giro só chegam à grade por ele.
        private static bool PassaSituacao(ProdutoEstoque p, string sit, int diasPeriodo)
        {
            if (sit == "T") return true;
            int nivel = AnaliseEstoque.Nivel(p, diasPeriodo);
            if (sit == "C") return nivel == AnaliseEstoque.NivelCritico;
            if (sit == "A") return nivel == AnaliseEstoque.NivelAtencao;
            return nivel == AnaliseEstoque.NivelCritico || nivel == AnaliseEstoque.NivelAtencao;
        }

        private static bool CorrespondeBusca(ProdutoEstoque p, string busca)
        {
            if (busca.Length == 0) return true;
            return BuscaSolicitacao.CorrespondeTexto(p.Nome, busca) ||
                   p.Codigo.ToString(CultureInfo.InvariantCulture).Contains(busca);
        }

        // Grade vazia. "Nenhum produto passa nos filtros escolhidos" deixava o farmacêutico
        // sem saber se o produto não está na análise ou se está escondido atrás do filtro de
        // SITUAÇÃO, que é o caso comum: ele busca um item que está OK com o filtro no padrão
        // "Crítico + atenção". Aqui o texto diz qual filtro segurou a linha e o que mexer.
        private string TextoNadaEncontrado(AnaliseResultado r, string tipo, string sit, string busca)
        {
            string nomeSit = NomeOpcao(cmbSituacao);
            string nomeTipo = NomeOpcao(cmbTipo);
            if (busca.Length == 0)
                return "Nenhum produto em “" + nomeSit + "” nesta análise.";

            string termo = busca.Length > 40 ? busca.Substring(0, 40) + "…" : busca;
            int comTexto = 0, soSituacaoSegura = 0, soTipoSegura = 0;
            ProdutoEstoque unico = null;
            foreach (var p in r.Itens)
            {
                if (!CorrespondeBusca(p, busca)) continue;
                comTexto++;
                if (PassaTipo(p, tipo)) { soSituacaoSegura++; unico = p; }
                if (PassaSituacao(p, sit, r.DiasPeriodo)) soTipoSegura++;
            }
            if (comTexto == 0)
                return "Nenhum produto com “" + termo + "” nesta análise. Confira a escrita ou busque pelo código.";
            if (soSituacaoSegura == 1)
                return unico.Nome + " está nesta análise com situação " +
                       AnaliseEstoque.NomeNivel(AnaliseEstoque.Nivel(unico, r.DiasPeriodo)) +
                       ". Escolha “Todas” em SITUAÇÃO para ver a linha.";
            if (soSituacaoSegura > 1)
                return "“" + termo + "” aparece nesta análise, mas nenhum resultado está em “" + nomeSit +
                       "”. Escolha “Todas” em SITUAÇÃO para ver as linhas.";
            if (soTipoSegura > 0)
                return "“" + termo + "” aparece nesta análise, mas nenhum resultado é de “" + nomeTipo +
                       "”. Escolha “Todos” em TIPO para ver as linhas.";
            return "“" + termo + "” aparece nesta análise, mas nenhum resultado está em “" + nomeTipo +
                   "” com “" + nomeSit + "”. Escolha “Todos” em TIPO e “Todas” em SITUAÇÃO para ver as linhas.";
        }

        private static string NomeOpcao(StyledComboBox cmb)
        {
            var oi = cmb.SelectedItem as OpcaoItem;
            return oi == null ? "" : oi.Nome;
        }

        private static Color CorNivel(int nivel)
        {
            if (nivel == AnaliseEstoque.NivelCritico) return Theme.Error;
            if (nivel == AnaliseEstoque.NivelAtencao) return Theme.Warning;
            if (nivel == AnaliseEstoque.NivelOk) return Theme.Success;
            return Theme.TextSoft;
        }

        private static string Fmt(decimal v, CultureInfo pt)
        {
            return v == Math.Truncate(v) ? v.ToString("N0", pt) : v.ToString("N2", pt);
        }

        private static string TooltipLinha(ProdutoEstoque p, int nivel, AnaliseResultado r)
        {
            var pt = CultureInfo.GetCultureInfo("pt-BR");
            var sb = new System.Text.StringBuilder();
            // O nome abre o tooltip: a coluna Produto trunca em boa parte das linhas mesmo
            // na janela padrão, e este é o único lugar onde o nome inteiro é legível
            // (definir ToolTipText na célula desliga o tooltip automático de texto cortado).
            if (p.Nome.Length > 0) sb.Append(p.Nome).Append('\n');
            if (p.Unidade.Length > 0) sb.Append("Unidade: ").Append(p.Unidade).Append('\n');
            // A cobertura saiu da grade (pedido do farmacêutico) mas continua aqui: é a
            // conta que sustenta a Situação. Vai em FRASE PRÓPRIA, e não colada por
            // travessão, porque é a informação que o farmacêutico procura no tooltip.
            string cobTxt = TextoCobertura(p, r.DiasPeriodo, pt);
            if (p.Consumo > 0)
                sb.Append("Saiu da CAF ").Append(Fmt(p.Consumo, pt)).Append(" em ").Append(r.DiasPeriodo)
                  // 2 casas, não 1: com 1 casa o Fmt ainda imprimia N2 e saía "1,40" para
                  // 1,43 (o leitor multiplica pelos dias e a conta não fecha), e 1 unidade
                  // em 30 dias virava "média de 0 por dia" na mesma frase que diz que saiu 1.
                  .Append(" dias, média de ").Append(Fmt(Math.Round(p.Consumo / Math.Max(1, r.DiasPeriodo), 2), pt))
                  .Append(" por dia.").Append(cobTxt).Append('\n');
            else if (p.Consumo4m > 0)
                sb.Append("Nada saiu no período. Nos últimos 4 meses saíram ")
                  .Append(Fmt(p.Consumo4m, pt)).Append('.').Append(cobTxt).Append('\n');
            else
                sb.Append("Nada saiu da CAF no período.\n");
            if (p.EstoqueMinimo > 0 || p.PontoPedido > 0)
            {
                var campos = new List<string>();
                if (p.EstoqueMinimo > 0) campos.Add("mínimo " + Fmt(p.EstoqueMinimo, pt));
                if (p.PontoPedido > 0) campos.Add("ponto de pedido " + Fmt(p.PontoPedido, pt));
                if (p.EstoqueMaximo > 0) campos.Add("máximo " + Fmt(p.EstoqueMaximo, pt));
                sb.Append("Cadastro do MV: ").Append(Juntar(campos)).Append(".\n");
            }
            int sug = AnaliseEstoque.Sugestao(p, r.DiasPeriodo);
            if (sug > 0)
            {
                sb.Append("Sugestão de compra: ").Append(sug.ToString("N0", pt));
                if (p.VlUnitario > 0)
                    sb.Append(" (≈ R$ ").Append((sug * p.VlUnitario).ToString("N2", pt)).Append(")");
                sb.Append(".\n");
            }
            if (p.Fornecedor.Length > 0)
            {
                // "Última entrada: FORNECEDOR em 12/03" lia-se como se o fornecedor fosse a
                // coisa que entrou; a data vem primeiro e o fornecedor fecha a frase.
                if (p.UltimaEntrada.HasValue)
                    sb.Append("Última entrada em ").Append(p.UltimaEntrada.Value.ToString("dd/MM/yyyy"))
                      .Append(", de ").Append(p.Fornecedor).Append('.');
                else
                    sb.Append("Fornecedor da última entrada: ").Append(p.Fornecedor).Append('.');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // "O saldo cobre cerca de N dias", em frase separada ("" quando não há consumo
        // nenhum para dividir). Singular e plural escritos por extenso: "dia(s)" é economia
        // de código que o leitor paga. Saldo zerado é dito com todas as letras — é o caso
        // mais grave da tela e "não cobre um dia inteiro" o fazia parecer pouca coisa.
        private static string TextoCobertura(ProdutoEstoque p, int diasPeriodo, CultureInfo pt)
        {
            // "O saldo" é o sujeito de todas as frases deste bloco. "A CAF está zerada",
            // logo depois de "Saiu da CAF 3", lia-se como se o almoxarifado inteiro
            // estivesse vazio.
            if (p.SaldoCaf < 0) return " O saldo na CAF está negativo.";
            if (p.SaldoCaf == 0) return " O saldo na CAF está zerado.";
            double cob = AnaliseEstoque.CoberturaEfetiva(p, diasPeriodo);
            if (double.IsInfinity(cob)) return "";
            if (cob >= 999) return " O saldo cobre mais de 999 dias.";
            long dias = (long)Math.Round(cob);
            if (dias <= 0) return " O saldo não cobre um dia inteiro.";
            if (dias == 1) return " O saldo cobre cerca de 1 dia.";
            return " O saldo cobre cerca de " + dias.ToString("N0", pt) + " dias.";
        }

        // "a", "a e b", "a, b e c". A seção enumera com vírgula e "e", nunca com um
        // símbolo separador no meio da frase.
        private static string Juntar(List<string> itens)
        {
            if (itens.Count == 0) return "";
            if (itens.Count == 1) return itens[0];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < itens.Count - 1; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(itens[i]);
            }
            return sb.Append(" e ").Append(itens[itens.Count - 1]).ToString();
        }

        // Fecha com ponto o texto que vem de fora (mensagem de exceção), senão a frase
        // seguinte do banner gruda nele.
        private static string Frase(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "";
            char c = s[s.Length - 1];
            return (c == '.' || c == '!' || c == '?' || c == ':') ? s : s + ".";
        }

        // Base do tooltip de uma coluna de setor; o RenderGrid completa com o motivo
        // quando a coluna sai sem dado.
        private static string DicaSetor(SetorComparacao s)
        {
            return "Saldo atual no estoque " + s.Codigo + " (" + s.Nome + ").";
        }

        // ---- Comandos globais (F5 / Ctrl+F) ----
        public void ComandoAtualizar()
        {
            if (!_busy) OnGerar(null, EventArgs.Empty);
        }

        public void ComandoFocarBusca()
        {
            if (txtBusca != null && txtBusca.Enabled) { txtBusca.Focus(); txtBusca.SelectAll(); }
        }
    }
}

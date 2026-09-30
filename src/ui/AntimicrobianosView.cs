using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Antimicrobianos": quem está em uso, em que setor, com qual antibiótico e em
    // que posologia — e quanto disso ainda falta dispensar.
    //
    // A lista sai da prescrição médica (PEP), não da justificativa de uso: a justificativa é
    // um documento em Flash e o que interessa (produto, dose, frequência, "D5/7") está na
    // própria prescrição. READ-ONLY do começo ao fim.
    //
    // A varredura entra pelo ATENDIMENTO, que a aba Solicitações já traz do hospital inteiro.
    // Isso não é detalhe: entrar pelo feed global do PEP conta o mesmo tratamento duas vezes
    // (a prescrição de ontem e a de hoje) — medido, a ceftriaxona saltava de 38 para 160.
    public class AntimicrobianosView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private PrimaryButton btnAtualizar;
        private TextBox txtBusca;
        private PrimaryButton btnSemJust;
        private bool _soPendentes;
        private StatusBanner banner;
        private GridPro grid, gridEstoque;
        private PrimaryButton btnAbaPacientes, btnAbaEstoque;
        private Label lblVazio;
        // Atualização do monitor que chegou com o mouse sobre a grade: fica pendente e
        // desenha ao sair (as linhas deslizariam sob o ponteiro de quem está lendo).
        private bool _renderPendente;
        private DateTime _adiadoDesde = DateTime.MinValue;

        // Qual das duas visões está aberta. As perguntas são diferentes e não cabem na
        // mesma grade: "o que este paciente está tomando" é linha por prescrição, e
        // "tem ceftriaxona suficiente" é linha por PRODUTO, somando o hospital inteiro.
        // Estático como o resto do estado: o shell destrói a view a cada troca de módulo
        // e voltar sempre na aba de pacientes seria perder o lugar.
        private static bool _abaEstoque;

        private const int EstoqueFarmaciaCentral = 5;
        // Filtro da Posição de Estoque Simplificado: espécie 1 = DROGAS E MEDICAMENTOS,
        // classe 4 = ANTI-INFECCIOSOS GERAIS PARA USO SISTEMICO. É a mesma consulta que a
        // farmácia faz na mão, e o saldo vem de TODOS os estoques do hospital, não de um só.
        private const int EspecieMedicamentos = 1;
        private const int ClasseAntiInfecciosos = 4;

        // ESTADO COMPARTILHADO: sobrevive à troca de aba, porque o shell destrói a view a cada
        // visita e o monitor precisa continuar rodando sem ela. Limpo no logout (tem PHI).
        private static readonly List<ItemAntimicrobiano> _itens = new List<ItemAntimicrobiano>();
        // O catálogo INTEIRO da classe (não só o que está prescrito): a aba Estoque lista
        // todos os antimicrobianos da casa, para dar ciência do conjunto — inclusive dos
        // que ninguém está tomando hoje. Vem do mesmo relatório que já era lido para
        // preencher o saldo, então não custa nenhuma consulta a mais.
        private static readonly List<SaldoProduto> _catalogo = new List<SaldoProduto>();
        private static bool _busy;
        private static DateTime _atualizadoEm;
        private static int _semPrescricao, _semJustificativa, _consultados;
        // _semCalculo: prescrições fora da soma por frequência/quantidade desconhecida.
        // _semConversao: fora da soma porque a dose veio em medida (mg/UI) e não deu para
        // converter em embalagens. _soHoje: entraram, mas só com o consumo do dia.
        private static int _semCalculo, _semConversao, _soHoje;
        // true = a última varredura NÃO conseguiu consultar o estoque (portão ocupado ou
        // erro): os saldos na tela são os da varredura anterior e o banner precisa dizer.
        private static bool _estoqueFalhou;
        // Nomes já anunciados no log como "sem ligação" — sem isso o monitor repetiria as
        // mesmas duas linhas a cada 3 minutos, o dia inteiro.
        private static readonly HashSet<string> _semLigacaoLogados =
            new HashSet<string>(StringComparer.Ordinal);
        private static string _erro, _avisoVarredura;
        private static bool _incompleta;
        // Qual faixa da zebra cada linha usa. A alternância é por PACIENTE (e não por linha),
        // para o bloco de quem toma três antibióticos se ler como um bloco só.
        private readonly Dictionary<int, bool> _faixaDaLinha = new Dictionary<int, bool>();

        public AntimicrobianosView(MainWindow host)
        {
            _host = host;
            this.Dock = DockStyle.Fill;
            this.BackColor = Theme.ContentBg;
            // Sem Padding próprio: o shell já dá a moldura da área de conteúdo, e a
            // moldura dobrada roubava largura justo da grade (a coluna Justif. sumia).

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            // Coluna única Percent: sem isso a coluna cresce até o preferido dos cartões
            // e o conteúdo pode estourar o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 3;
            // AutoSize, não altura fixa: com 100px fixos o conteúdo cabia no meu DPI e era
            // cortado embaixo num DPI maior, porque a fonte cresce mais que a linha.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // barra de ações
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // grade

            root.Controls.Add(BuildBarra(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultado(), 0, 2);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
        }

        private Control BuildBarra()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 5;
            tl.RowCount = 2;
            // Filtros à esquerda (com micro-rótulo, como nas outras abas), ação à direita.
            //
            // Quem ESTICA é a coluna da BUSCA, não uma coluna vazia. Com a sobra no meio, ao
            // apertar a janela ela ia a zero e o TableLayoutPanel passava a cortar a última
            // coluna Absolute — o botão. Agora, quando falta espaço, quem cede é a busca
            // (que ainda é usável menor) e os botões ficam sempre inteiros.
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // busca (elástica)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(16)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(236))); // filtro
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(16)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(164))); // Atualizar
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tl.Controls.Add(Micro("BUSCAR"), 0, 0);
            tl.Controls.Add(Micro("FILTRO"), 2, 0);

            tl.Controls.Add(BuildBusca(), 0, 1);

            btnAtualizar = new PrimaryButton();
            btnAtualizar.Text = "Atualizar";
            btnAtualizar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnAtualizar.Height = Theme.S(40);
            btnAtualizar.Margin = new Padding(0, 0, Theme.S(4), 0);   // nao encostar na borda da celula
            btnAtualizar.Click += new EventHandler(delegate { Consultar(); });
            tl.Controls.Add(btnAtualizar, 4, 1);

            // Atalho da CCIH: a lista inteira serve à farmácia, mas quem cobra o médico quer
            // abrir direto na pendência. É um BOTÃO que liga/desliga, não um checkbox nativo:
            // a caixinha do Windows destoava do resto da tela e o estado ligado passava
            // despercebido. Como botão, ligado fica azul cheio e não tem como não ver.
            btnSemJust = new PrimaryButton();
            btnSemJust.Ghost = true;
            btnSemJust.Text = TextoDoFiltro();
            btnSemJust.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnSemJust.Height = Theme.S(40);
            btnSemJust.Margin = new Padding(0);
            btnSemJust.Click += new EventHandler(delegate
            {
                _soPendentes = !_soPendentes;
                AplicarEstadoDoFiltro();
                RenderGrid();
            });
            tl.Controls.Add(btnSemJust, 2, 1);

            var dica = new ToolTip();
            dica.SetToolTip(btnAtualizar, "Relê as prescrições no MV Soul");
            dica.SetToolTip(btnSemJust, "Filtra a lista para as prescrições que exigem justificativa e ainda estão sem ela."
                + "\nClique de novo para voltar a ver todos.");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });

            card.Controls.Add(tl);
            return card;
        }

        private Label Micro(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true; l.Margin = new Padding(2, 0, 0, Theme.S(5));
            return l;
        }

        // Mesmo desenho da busca da aba Solicitações: campo dentro de um FieldHost (cantos
        // arredondados, borda sutil) com a lupa à esquerda e o texto sem borda própria.
        // Filtra ENQUANTO se digita, sobre o que já está em memória — é instantâneo.
        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            // Elástico com teto: encolhe se a janela apertar (protegendo os botões de serem
            // cortados) e para de crescer em 420, para não virar um campo de 1200px.
            hostBusca.MaximumSize = new Size(Theme.S(420), 0);
            hostBusca.Height = Theme.S(34);
            hostBusca.Margin = new Padding(0, 3, 0, 3);   // centra contra o botão de 40px
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
            // Debounce curto: filtra "enquanto digita" sem redesenhar a grade a cada tecla.
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
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, "Buscar paciente, setor ou antimicrobiano");
            });

            hostBusca.Controls.Add(txtBusca);
            hostBusca.Controls.Add(lupa);
            return hostBusca;
        }

        // O rótulo carrega a contagem: com o filtro desligado, "(3)" já avisa que há três
        // pendências sem obrigar a ler o banner nem a varrer a coluna do ✘.
        private string TextoDoFiltro()
        {
            return "Sem justificativas" + (_semJustificativa > 0 ? "  (" + _semJustificativa + ")" : "");
        }

        private void AplicarEstadoDoFiltro()
        {
            if (btnSemJust == null) return;
            btnSemJust.Ghost = !_soPendentes;    // ligado = azul cheio
            btnSemJust.Text = TextoDoFiltro();
            btnSemJust.Invalidate();
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            return banner;
        }

        private Control BuildResultado()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1; tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // As duas visões, no lugar onde antes havia só o título do cartão. São botões
            // no mesmo desenho do resto da tela (cheio = aberta, contorno = fechada) em vez
            // de um TabControl nativo, que traria as abas cinza do Windows para dentro de
            // um cartão branco e destoaria de tudo.
            var abas = new FlowLayoutPanel();
            abas.AutoSize = true; abas.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            abas.BackColor = Theme.CardBg; abas.WrapContents = false;
            abas.FlowDirection = FlowDirection.LeftToRight;
            abas.Margin = new Padding(0, 2, 0, Theme.S(10)); abas.Padding = new Padding(0);
            abas.Anchor = AnchorStyles.Left;

            btnAbaPacientes = Aba("Pacientes em uso", 176, false);
            btnAbaEstoque = Aba("Estoque", 130, true);
            abas.Controls.Add(btnAbaPacientes);
            abas.Controls.Add(btnAbaEstoque);
            tl.Controls.Add(abas, 0, 0);

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;
            gridEstoque = NovaGridEstoque(); gridEstoque.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Carregando as prescrições...";

            host.Controls.Add(grid);
            host.Controls.Add(gridEstoque);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 1);

            card.Controls.Add(tl);
            return card;
        }

        private PrimaryButton Aba(string texto, int largura, bool estoque)
        {
            var b = new PrimaryButton();
            b.Text = texto; b.Width = Theme.S(largura); b.Height = Theme.S(34);
            b.Margin = new Padding(0, 0, Theme.S(8), 0);
            b.Click += new EventHandler(delegate
            {
                if (_abaEstoque == estoque) return;
                _abaEstoque = estoque;
                AplicarEstadoDasAbas();
                RenderGrid();
            });
            return b;
        }

        private void AplicarEstadoDasAbas()
        {
            if (btnAbaPacientes == null) return;
            btnAbaPacientes.Ghost = _abaEstoque;    // aberta = azul cheio
            btnAbaEstoque.Ghost = !_abaEstoque;
            btnAbaPacientes.Invalidate(); btnAbaEstoque.Invalidate();
            // A pendência de justificativa é por prescrição, não por produto: no Estoque
            // esse filtro não teria o que fazer. Desligado em vez de escondido, para a
            // barra não mudar de forma ao trocar de aba.
            if (btnSemJust != null) btnSemJust.Enabled = !_abaEstoque && !_busy;
            if (txtBusca != null && txtBusca.IsHandleCreated)
                SendMessage(txtBusca.Handle, 0x1501, (IntPtr)1, _abaEstoque
                    ? "Buscar antimicrobiano"
                    : "Buscar paciente, setor ou antimicrobiano");
        }

        // O desenho da grade, sem coluna nenhuma: as duas abas usam o mesmo, e duplicar
        // vinte linhas de estilo é a maneira mais certa de elas divergirem com o tempo.
        // O grosso (cabeçalho claro, seleção, hover, double buffer) já vem do GridPro;
        // aqui fica só o que é escolha desta tela.
        private GridPro NovaGridBase()
        {
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            // As colunas de texto são Fill e a grade se ajusta à largura disponível; a
            // rolagem lateral fica de REDE DE SEGURANÇA — só aparece se, mesmo assim, o
            // conjunto não couber (janela mínima, DPI alto), em vez de amputar a última
            // coluna, que é justamente o sinal da justificativa.
            g.ScrollBars = ScrollBars.Both;
            // Saiu de cima da grade: aplica a atualização do monitor que ficou pendente
            // (ver RenderGridAutomatico).
            g.MouseLeave += new EventHandler(delegate
            {
                if (!_renderPendente) return;
                _renderPendente = false; _adiadoDesde = DateTime.MinValue;
                RenderGrid();
            });
            return g;
        }

        private GridPro NovaGrid()
        {
            var g = NovaGridBase();

            // Paciente e antimicrobiano são os dois textos longos e dividem toda a sobra
            // (Fill, peso 2 para o nome do remédio, que é o mais comprido). O resto tem
            // largura fixa e ENXUTA: cada pixel gasto num campo curto sai desses dois.
            // O orçamento inteiro (fixas + mínimos dos Fill) fecha em ~765px na base de
            // 96 DPI, para a coluna Justif. caber já na janela padrão.
            //
            // Consumo e Estoque saíram daqui: a pergunta que eles respondem ("dá para o
            // hospital?") é do produto inteiro, não de uma pessoa, e agora está na aba
            // Estoque. Aqui ficou o que se lê olhando UM paciente.
            var pac = Col("paciente", "Paciente", 240, true, DataGridViewContentAlignment.MiddleLeft);
            pac.MinimumWidth = Theme.S(165); pac.FillWeight = 100; g.Columns.Add(pac);
            // 76, não 64: em 96 DPI (farmácia) a fonte rende mais larga e 7 dígitos
            // batiam na borda — mesma correção das abas Solicitações/Justificativas.
            g.Columns.Add(Col("atend", "Atend.", 76, false, DataGridViewContentAlignment.MiddleCenter));
            var prod = Col("produto", "Antimicrobiano", 260, true, DataGridViewContentAlignment.MiddleLeft);
            prod.MinimumWidth = Theme.S(180); prod.FillWeight = 115; g.Columns.Add(prod);
            // Dose em embalagens ("2 FRASCOS"), sem via nem concentração — a prescrição
            // original do MV (com a via) vive no tooltip da célula.
            g.Columns.Add(Col("dose", "Dose", 112, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("freq", "Frequência", 104, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("dias", "Dias", 60, false, DataGridViewContentAlignment.MiddleCenter));
            // Só o símbolo: a coluna fica estreita e a leitura é de relance, sem pintar a
            // linha inteira — a linha já usa cor para outra coisa (frequência não reconhecida).
            var jus = Col("justif", "Justif.", 68, false, DataGridViewContentAlignment.MiddleCenter);
            jus.DefaultCellStyle.Font = Theme.Icon;
            g.Columns.Add(jus);

            g.Columns["dias"].ToolTipText = "Dia atual do tratamento / total previsto pelo médico";
            // A legenda descreve as MESMAS formas que as células desenham (certo fino,
            // xis fino, traço) — prometer ✔/✘ cheios enquanto a célula desenha outra
            // coisa só cria dúvida.
            g.Columns["justif"].ToolTipText = "Justificativa de uso de antimicrobiano emitida pelo médico"
                + "\n✓ emitida    ✕ não emitida    – dispensada pelo protocolo";

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellPainting += new DataGridViewCellPaintingEventHandler(Grid_PintarPaciente);
            return g;
        }

        // A grade do estoque: uma linha por PRODUTO, não por prescrição. É a visão da
        // farmácia — "a ceftriaxona vai consumir 20 até o fim dos tratamentos e há 765
        // em casa" — e por isso mistura o que veio do PEP (consumo) com o que veio do
        // Morphis (saldo), somando todos os pacientes de uma vez.
        private GridPro NovaGridEstoque()
        {
            var g = NovaGridBase();

            var prod = Col("produto", "Antimicrobiano", 320, true, DataGridViewContentAlignment.MiddleLeft);
            prod.MinimumWidth = Theme.S(240); g.Columns.Add(prod);
            g.Columns.Add(Col("pacientes", "Pacientes", 90, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("consumo", "Consumo", 100, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("estoque", "Estoque", 96, false, DataGridViewContentAlignment.MiddleCenter));
            // O número da sobra, não uma bolinha: quanto vai restar depois de atender os
            // tratamentos em curso. A cor (verde/amarelo/vermelho) carrega a urgência e o
            // número diz o tamanho dela — era o que a bolinha não dizia.
            var st = Col("sobra", "Sobra", 84, false, DataGridViewContentAlignment.MiddleCenter);
            g.Columns.Add(st);

            g.Columns["pacientes"].ToolTipText = "Quantos pacientes estão em uso deste antimicrobiano agora";
            g.Columns["consumo"].ToolTipText =
                "Tudo o que ainda falta dispensar deste produto até o fim dos tratamentos em curso"
                + "\n(soma de todos os pacientes: dias que faltam, contando hoje, × dose por dia)";
            g.Columns["estoque"].ToolTipText = "Saldo do produto somando TODOS os estoques do hospital"
                + "\n(Posição de Estoque Simplificado: espécie 1, classe 4, saldo diferente de zero)";
            g.Columns["sobra"].ToolTipText = "Quanto vai SOBRAR no estoque depois de atender os tratamentos em curso"
                + "\n(estoque − consumo previsto; negativo = falta)"
                + "\nverde — sobra com folga (o saldo cobre duas vezes ou mais o consumo)"
                + "\namarelo — sobra, mas com menos de duas vezes de folga"
                + "\nvermelho — FALTA para fechar os tratamentos"
                + "\nvazio — ninguém em uso, ou não deu para projetar/comparar";

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(GridEstoque_Format);
            return g;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);   // valores BASE de 96 DPI
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            // Cabeçalho acompanha a célula: coluna centralizada com título à esquerda
            // continuava parecendo torta.
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        // Fundo da linha de ressalva quando SELECIONADA: um degrau mais escuro que o
        // WarningBg — com os dois iguais, clicar na linha âmbar não mudava um pixel e a
        // seleção "sumia" ao passar por ela com as setas.
        private static readonly Color RessalvaSelBg = Color.FromArgb(255, 245, 224, 178);

        // Realce: a linha que o programa não conseguiu projetar (frequência estranha, sem
        // previsão de término, dose avulsa) sai em âmbar — ela NÃO entra na soma, e isso
        // precisa saltar aos olhos em vez de virar um total silenciosamente menor.
        private void Grid_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
            var it = grid.Rows[e.RowIndex].Tag as ItemAntimicrobiano;
            if (it == null) return;

            bool faixa;
            if (_faixaDaLinha.TryGetValue(e.RowIndex, out faixa) && faixa)
            { e.CellStyle.BackColor = Theme.GridZebra; e.CellStyle.SelectionBackColor = Theme.SelBg; }

            // O nome do paciente é a âncora de leitura da linha: fica em negrito, e as
            // linhas de continuação do mesmo paciente ficam com o texto mais suave.
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "paciente") e.CellStyle.Font = Theme.BodyBold;
            else if (col == "atend") e.CellStyle.ForeColor = Theme.TextSoft;

            if (it.Ressalva.Length > 0)
            {
                e.CellStyle.BackColor = Theme.WarningBg;
                e.CellStyle.SelectionBackColor = RessalvaSelBg;
                e.CellStyle.ForeColor = Theme.Warning;
                e.CellStyle.SelectionForeColor = Theme.Warning;
            }
            // O ✓/✕ carrega a cor sozinho — a pedido, a linha inteira não é pintada.
            if (col == "justif")
            {
                e.CellStyle.ForeColor = !it.ExigeJustificativa ? Theme.TextSoft
                    : it.Justificado ? Theme.Success : Theme.Error;
                e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
            }
        }

        // A lista é ORDENADA por setor, e o setor não tinha onde aparecer (a coluna foi
        // retirada a pedido): entra como sufixo discreto depois do nome, na primeira
        // linha de cada paciente. Duas cores numa célula só o pintor consegue — o
        // formatador colore a célula inteira. Fundo e linha divisória são refeitos aqui
        // na mesma ordem de decisão do Grid_Format + hover do GridPro, para a célula não
        // destoar das vizinhas.
        private void Grid_PintarPaciente(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "paciente") return;
            var it = grid.Rows[e.RowIndex].Tag as ItemAntimicrobiano;
            if (it == null) return;
            string nome = e.FormattedValue as string;
            if (string.IsNullOrEmpty(nome)) return;               // linha de continuação: pintura normal
            string setor = NomeProprio(it.Setor);
            if (setor.Length == 0) return;                        // sem setor, nada a acrescentar

            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            bool faixa;
            _faixaDaLinha.TryGetValue(e.RowIndex, out faixa);
            Color fundo;
            if (sel) fundo = it.Ressalva.Length > 0 ? RessalvaSelBg : Theme.SelBg;
            else if (e.RowIndex == grid.LinhaHover) fundo = Theme.HoverBg;
            else if (it.Ressalva.Length > 0) fundo = Theme.WarningBg;
            else if (faixa) fundo = Theme.GridZebra;
            else fundo = Theme.CardBg;
            using (var b = new SolidBrush(fundo))
                e.Graphics.FillRectangle(b, e.CellBounds);
            using (var p = new Pen(Theme.GridLine))
                e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Bottom - 1,
                                       e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                      | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            var area = new Rectangle(e.CellBounds.X + Theme.S(6), e.CellBounds.Y,
                                     e.CellBounds.Width - Theme.S(12), e.CellBounds.Height - 1);
            Color corNome = it.Ressalva.Length > 0 ? Theme.Warning : Theme.TextDark;
            TextRenderer.DrawText(e.Graphics, nome, Theme.BodyBold, area, corNome, flags);

            // O setor só entra se sobrar espaço depois do nome — nome cortado com
            // reticências já é informação demais brigando por pixels.
            int usado = TextRenderer.MeasureText(e.Graphics, nome, Theme.BodyBold,
                new Size(int.MaxValue, area.Height), flags).Width;
            if (usado < area.Width - Theme.S(30))
            {
                var areaSetor = new Rectangle(area.X + usado + Theme.S(6), area.Y,
                                              area.Width - usado - Theme.S(6), area.Height);
                TextRenderer.DrawText(e.Graphics, "· " + setor, Theme.Small, areaSetor,
                    Theme.TextSoft, flags);
            }
            e.Handled = true;
        }

        // Zebra simples (por linha, já que aqui cada linha é um produto) e o realce do
        // saldo curto, que é a razão de existir desta aba.
        private void GridEstoque_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= gridEstoque.Rows.Count) return;
            var l = gridEstoque.Rows[e.RowIndex].Tag as LinhaEstoque;
            if (l == null) return;

            if (e.RowIndex % 2 == 1)
            { e.CellStyle.BackColor = Theme.GridZebra; e.CellStyle.SelectionBackColor = Theme.SelBg; }

            string col = gridEstoque.Columns[e.ColumnIndex].Name;
            int n = Nivel(l);
            // Quem não está em uso fica esmaecido: a lista existe para dar ciência de todos
            // os antimicrobianos, mas quem tem paciente hoje é que precisa ser lido.
            if (n >= 4 && col != "sobra") e.CellStyle.ForeColor = Theme.TextSoft;
            else if (col == "produto") e.CellStyle.Font = Theme.BodyBold;
            else if (col == "pacientes") e.CellStyle.ForeColor = Theme.TextSoft;

            if (col == "sobra")
            {
                // A cor é a leitura de relance; o número, o tamanho do aperto. Nos níveis
                // sem projeção (3 e 4) a célula está vazia e a cor não importa.
                e.CellStyle.ForeColor = n == 0 ? Theme.Error
                    : n == 1 ? Theme.Warning
                    : n == 2 ? Theme.Success : Theme.TextSoft;
                e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
                if (n <= 1) e.CellStyle.Font = Theme.BodyBold;   // falta/aperto salta aos olhos
                return;
            }
            // Fundo vermelho só no caso em que alguém precisa comprar hoje. Os outros
            // níveis a bolinha já resolve — pintar tudo tiraria o peso deste.
            if (col == "estoque" && n == 0)
            {
                e.CellStyle.BackColor = Theme.ErrorBg;
                e.CellStyle.SelectionBackColor = Theme.ErrorBg;
                e.CellStyle.ForeColor = Theme.Error;
                e.CellStyle.SelectionForeColor = Theme.Error;
                e.CellStyle.Font = Theme.BodyBold;
            }
        }

        private void OnLoad(object sender, EventArgs e)
        {
            _viva = this;
            this.Disposed += new EventHandler(delegate { if (_viva == this) _viva = null; });
            AplicarEstadoDasAbas();

            if (_host.Session == null)
            {
                // --shell-preview / --atb-preview: desenho da tela sem tocar no MV.
                // Com "--aba-estoque" junto, abre direto na sub-aba Estoque — é como o
                // laço de screenshot confere aquela grade sem clique sintético.
                if (_host.Usuario == "PREVIEW")
                {
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--aba-estoque") >= 0)
                    { _abaEstoque = true; AplicarEstadoDasAbas(); }
                    CarregarExemplo();
                    return;
                }
                banner.SetState(StatusKind.Warning, "Faça login para ver os antimicrobianos.");
                lblVazio.Text = "Faça login para ver os antimicrobianos.";
                return;
            }

            IniciarMonitor(_host);
            // Voltando à aba com a foto do monitor em mãos: desenha na hora, sem esperar os
            // ~5 s de uma varredura nova. Só busca se ainda não houver nada.
            if (_itens.Count > 0 && _atualizadoEm.Date == DateTime.Today)
            {
                AplicarEstadoDoFiltro();
                RenderGrid();
                MostrarBanner();
            }
            else if (!_busy) Consultar();
        }

        private void Consultar() { Consultar(false); }

        // A busca com feedback na tela. O trabalho pesado está em VarrerAsync, que é ESTÁTICO
        // e roda também sem esta tela existir — ver o monitor mais abaixo.
        private async void Consultar(bool automatico)
        {
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy)
            {
                // Já tem varredura em curso (em geral a automática): o clique não pode
                // morrer mudo, senão o botão parece quebrado.
                banner.SetState(StatusKind.Busy, "Atualização em andamento — a lista se renova em instantes.");
                return;
            }
            _busy = true;
            // Na atualização automática a tela NÃO se desmonta: apagar a grade e escrever
            // "Carregando..." por cima de quem está lendo seria pior que não atualizar.
            // Travar a busca também não: ela só filtra o que já está em memória.
            Travar(automatico ? "Atualizando..." : "Buscando...", automatico);
            if (!automatico)
            {
                banner.SetState(StatusKind.Busy, "Lendo as prescrições do hospital...");
                // As DUAS grades saem de cena: com a do estoque visível por cima, o
                // "Carregando..." (e a mensagem de erro) ficava atrás dela para quem
                // estava na aba Estoque.
                grid.Visible = false; gridEstoque.Visible = false; lblVazio.Visible = true;
                lblVazio.Text = "Carregando as prescrições...";
            }

            int sessao = _sessaoId;
            try
            {
                await VarrerAsync(_host.Session, sessao);
                if (IsDisposed || Disposing || sessao != _sessaoId) return;
                AplicarEstadoDoFiltro();
                RenderGrid();
                MostrarBanner();
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Erro ao ler as prescrições: " + ex.Message);
                lblVazio.Text = "Falha na busca. Tente “Atualizar”.";
            }
            finally
            {
                _busy = false;
                if (!IsDisposed && !Disposing) Destravar();
            }
        }

        // Uma passada completa: pacientes (ERP) -> prescrições (PEP) -> estoque -> justificativas.
        // É ESTÁTICA de propósito: precisa rodar com a aba fechada, quando não existe instância
        // desta view. O resultado fica nos campos estáticos e a tela (se houver) só desenha.
        private static async Task VarrerAsync(Session sessao_, int sessao)
        {
            // 1) Quem está internado: a foto que a aba Solicitações mantém do dia. Ela acumula
            //    todas as passadas, então não perde quem já foi atendido de manhã. Se ainda não
            //    houver foto (o módulo nunca foi aberto), busca aqui mesmo.
            bool incompleta = false;
            var solic = SolicitacoesView.FotoDeHoje(EstoqueFarmaciaCentral);
            if (solic == null)
            {
                bool tenho = await MvGate.EntrarAsync("antimicrobianos", 60000);
                if (!tenho) { _erro = "O servidor está respondendo outra consulta. Tente de novo em instantes."; return; }
                try
                {
                    var r = await MorphisClient.ListarDiaAsync(sessao_, EstoqueFarmaciaCentral, null, null, DateTime.Today);
                    solic = r.Itens;
                    // O servidor informa o total: se veio menos, a lista de pacientes está
                    // curta e a previsão sairia a menor com cara de completa.
                    incompleta = r.AtingiuTeto || (r.TotalServidor > 0 && r.Itens.Count < r.TotalServidor);
                }
                finally { MvGate.Sair(); }
            }
            if (sessao != _sessaoId) return;

            var alvos = new List<PacienteAlvo>();
            var vistos = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in solic)
            {
                if (string.IsNullOrEmpty(s.Atendimento) || string.IsNullOrEmpty(s.Paciente)) continue;
                if (!vistos.Add(s.Atendimento)) continue;
                alvos.Add(new PacienteAlvo { Atendimento = s.Atendimento, Nome = s.Paciente, Setor = s.Setor });
            }

            // 2) Prescrições no PEP (outra sessão, fora do portão do Morphis).
            var v = await PepClient.ListarPorPacienteAsync(sessao_, alvos);
            if (sessao != _sessaoId) return;
            v.ListaDePacientesIncompleta = incompleta;

            // 3) Estoque. A Posição de Estoque Simplificado resolve as duas coisas de uma vez:
            //    liga o nome da prescrição ao produto do catálogo e traz o saldo do HOSPITAL
            //    INTEIRO. Se falhar, a coluna fica "—" e o resto continua válido.
            int semLigacao = await LigarEstoqueAsync(sessao_, v.Itens);
            if (sessao != _sessaoId) return;

            // 4) Justificativas. A janela se ajusta ao tratamento mais antigo em curso: quem
            //    está em D13 começou há 12 dias e a justificativa dele é de lá.
            int semJust = 0;
            try
            {
                int janela = CruzamentoJustificativas.JanelaNecessaria(v.Itens, 7);
                var just = await PepClient.ListarJustificativasAsync(
                    sessao_, DateTime.Today.AddDays(-janela), DateTime.Today);
                if (sessao != _sessaoId) return;
                semJust = CruzamentoJustificativas.Aplicar(v.Itens, just.Itens);
            }
            catch (SessaoExpiradaException) { throw; }
            catch (Exception ex)
            {
                // Sem as justificativas a lista de antimicrobianos ainda vale.
                Logger.Log("Antimicrobianos: falha ao cruzar as justificativas: " + ex.Message);
            }

            // Última trava antes de publicar: se houve logout no meio (inclusive dentro do
            // catch acima, que engole a exceção e segue), nada pode voltar para a memória —
            // esta lista tem nome de paciente e o PararMonitor acabou de apagá-la.
            if (sessao != _sessaoId) return;

            _itens.Clear(); _itens.AddRange(v.Itens);
            _atualizadoEm = DateTime.Now;
            _erro = null;
            _estoqueFalhou = semLigacao < 0;
            // Itens sem ligação com o estoque NÃO viram mais aviso na tela (pedido do
            // farmacêutico, 29/08/2026): o saldo "—" na aba Estoque e o log do
            // LigarEstoqueAsync (um registro por nome) já contam a história para quem
            // administra; na tela o aviso só gerava ruído.
            _semJustificativa = semJust;
            _incompleta = v.ListaDePacientesIncompleta;
            _avisoVarredura = v.Aviso;
            _consultados = v.AtendimentosConsultados;
            // Os atendimentos que o PEP não reconhece (prescrição em papel, ambulatório,
            // pronto-socorro) são normais e alguns todo dia; só viram aviso quando são
            // muitos — aí sim a lista está capenga.
            _semPrescricao = v.AtendimentosSemPaciente;
            // O que o programa NÃO conseguiu calcular vira frase no banner, não símbolo na
            // coluna: um "+?" ao lado de um número não diz o que fazer, e quem lê a tela
            // não tem como adivinhar. Aqui a lista já está publicada, então basta contar.
            _semCalculo = 0; _semConversao = 0; _soHoje = 0;
            foreach (var it in _itens)
            {
                // A ressalva de conversão é a única em que dose e frequência são conhecidas;
                // as outras duas continuam no contador antigo (e na frase antiga do banner).
                if (it.Ressalva.Length > 0)
                {
                    if (it.AplicacoesPorDia > 0 && it.Quantidade > 0 && it.QuantidadePorAplicacao < 0)
                        _semConversao++;
                    else _semCalculo++;
                }
                else if (it.SoHoje.Length > 0) _soHoje++;
            }
            // O caminho do arquivo fica SÓ aqui: é quem administra o programa que lê o
            // log — na tela essa instrução soava como erro técnico para o farmacêutico.
            if (_semCalculo > 0)
                Logger.Log("Antimicrobianos: " + _semCalculo + " prescrição(ões) com frequência"
                    + " não reconhecida — cadastre em dados/frequencias.txt.");
            if (_semConversao > 0)
                Logger.Log("Antimicrobianos: " + _semConversao + " prescrição(ões) com dose em"
                    + " medida (mg/UI/mL) sem apresentação conhecida para converter — confira"
                    + " se o nome do produto declara o conteúdo (\"C/500MG\").");
            if (_semPrescricao > 0)
                Logger.Log("Antimicrobianos: " + _semPrescricao + " de " + v.AtendimentosConsultados
                    + " atendimentos sem prescrição eletrônica no PEP.");
        }

        // O banner ACUMULA os avisos em vez de escolher um. Era uma cadeia de else-if, e o
        // primeiro ramo apagava os outros: uma varredura incompleta escondia as pendências
        // de justificativa, que escondiam o horário. Agora todos aparecem e a severidade é
        // a do pior deles.
        private void MostrarBanner()
        {
            if (!string.IsNullOrEmpty(_erro)) { banner.SetState(StatusKind.Warning, _erro); return; }
            if (!string.IsNullOrEmpty(_avisoVarredura)) { banner.SetState(StatusKind.Error, _avisoVarredura); return; }
            if (_itens.Count == 0 && !_incompleta)
            { banner.SetState(StatusKind.Info, "Nenhum antimicrobiano prescrito agora."); return; }

            var avisos = new List<string>();
            var grau = StatusKind.Success;

            if (_incompleta)
            {
                avisos.Add("a varredura de solicitações veio incompleta e pode faltar paciente aqui "
                    + "(abra Solicitações, deixe atualizar e volte)");
                grau = StatusKind.Warning;
            }
            if (_consultados > 0 && _semPrescricao * 4 > _consultados)   // mais de 1/4 de fora
            {
                avisos.Add(_semPrescricao + " atendimentos não têm prescrição eletrônica no PEP e ficaram de fora");
                grau = StatusKind.Warning;
            }
            if (_semJustificativa > 0)
            {
                avisos.Add(_semJustificativa + (_semJustificativa == 1
                    ? " antimicrobiano sem justificativa emitida" : " antimicrobianos sem justificativa emitida"));
                grau = StatusKind.Warning;
            }
            if (_estoqueFalhou)
            {
                avisos.Add("não deu para consultar o estoque nesta varredura — os saldos"
                    + " podem estar desatualizados");
                grau = StatusKind.Warning;
            }
            // Estes dois são a explicação que antes ia como "+?" na coluna Consumo.
            if (_semCalculo > 0)
            {
                avisos.Add(_semCalculo + (_semCalculo == 1 ? " prescrição ficou" : " prescrições ficaram")
                    + " fora do consumo por frequência não reconhecida — avise quem administra o programa");
                grau = StatusKind.Warning;
            }
            if (_semConversao > 0)
            {
                avisos.Add(_semConversao + (_semConversao == 1 ? " prescrição ficou" : " prescrições ficaram")
                    + " fora do consumo: a dose veio em mg/UI e não deu para converter em"
                    + " frascos — avise quem administra o programa");
                grau = StatusKind.Warning;
            }
            if (_soHoje > 0)
            {
                avisos.Add("em " + _soHoje + (_soHoje == 1 ? " prescrição" : " prescrições")
                    + " o MV não diz até quando o tratamento vai, e nelas o consumo previsto"
                    + " é só o de hoje — o real é maior");
                grau = StatusKind.Warning;
            }

            // Só a hora, a pedido: as contagens da varredura saíram do banner. Os avisos
            // ficam — são o único lugar onde o problema aparece em palavras.
            string texto = "Atualizado às " + _atualizadoEm.ToString("HH:mm:ss") + " (hoje).";
            if (avisos.Count > 0) texto += " " + Maiuscula(string.Join("; ", avisos.ToArray())) + ".";
            banner.SetState(grau, texto);
        }

        private static string Maiuscula(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // ===== MONITOR EM SEGUNDO PLANO =====
        // Liga na primeira visita ao módulo e vive enquanto durar a SESSÃO, não enquanto a aba
        // está aberta: o shell destrói a view ao trocar de módulo, então um timer de instância
        // morreria junto. Com o timer estático, ao voltar para a aba a lista já está pronta.
        //
        // Custo por ciclo: ~5 s, dos quais só o relatório de estoque (0,6 s) passa pelo portão
        // do Morphis — o PEP é outra sessão. O tick desiste sem enfileirar se o portão estiver
        // ocupado, para nunca atrapalhar quem está produzindo kit ou enviando pedido.
        public const int IntervaloMonitorMs = 180000;   // 3 minutos

        private static System.Windows.Forms.Timer _monitor;
        private static MainWindow _monitorHost;
        private static int _sessaoId;
        private static AntimicrobianosView _viva;

        public static void IniciarMonitor(MainWindow host)
        {
            _monitorHost = host;
            // A lista de pacientes sai da foto que o módulo Solicitações mantém. Sem ele
            // ligado, cada ciclo faria a própria varredura — 7 a 9 s segurando o portão do
            // MV, de 3 em 3 minutos. Ligando os dois, sobra só o relatório de estoque (0,6 s).
            SolicitacoesView.IniciarMonitor(host);
            if (_monitor == null)
            {
                _monitor = new System.Windows.Forms.Timer();
                _monitor.Interval = IntervaloMonitorMs;
                _monitor.Tick += new EventHandler(MonitorTick);
            }
            if (_monitor.Enabled) return;
            _monitor.Start();
            Logger.Log("Antimicrobianos: monitor em segundo plano ligado (a cada "
                + (IntervaloMonitorMs / 1000) + "s).");
        }

        // Fim de sessão: invalida a varredura em voo e APAGA o que está em memória — a lista
        // tem nome de paciente e não pode reaparecer no login seguinte.
        public static void PararMonitor()
        {
            bool estava = _monitor != null && _monitor.Enabled;
            if (_monitor != null) _monitor.Stop();
            _monitorHost = null;
            _sessaoId++;
            _itens.Clear();
            _catalogo.Clear();
            _atualizadoEm = DateTime.MinValue;
            _semPrescricao = _semJustificativa = _consultados = 0;
            _semCalculo = _semConversao = _soHoje = 0;
            _estoqueFalhou = false;
            _semLigacaoLogados.Clear();
            _erro = null; _avisoVarredura = null; _incompleta = false;
            if (estava) Logger.Log("Antimicrobianos: monitor em segundo plano desligado.");
        }

        private static async void MonitorTick(object sender, EventArgs e)
        {
            if (_monitorHost == null || _monitorHost.Session == null) return;
            if (_busy) return;                                             // consulta em curso
            if (_monitorHost.WindowState == FormWindowState.Minimized) return;
            if (MvGate.Ocupado) return;                                    // o usuário está no MV
            if (ProduzirView.OcupadoNoMv) return;                          // preview de kit aberto

            // O intervalo passa a contar do FIM da varredura: sem isso o monitor ficaria boa
            // parte do tempo falando com o servidor.
            _monitor.Stop();
            int sessao = _sessaoId;
            _busy = true;
            // Sinal de vida na tela (se ela estiver aberta): o botão vira "Atualizando..."
            // durante a varredura de fundo — sem isso a grade se redesenhava "sozinha" e o
            // clique em Atualizar nesse intervalo parecia engolido.
            var vAntes = _viva;
            if (vAntes != null && !vAntes.IsDisposed && !vAntes.Disposing)
                vAntes.Travar("Atualizando...", true);
            try
            {
                await VarrerAsync(_monitorHost.Session, sessao);
                if (sessao != _sessaoId) return;
                var v = _viva;
                if (v != null && !v.IsDisposed && !v.Disposing)
                { v.AplicarEstadoDoFiltro(); v.RenderGridAutomatico(); v.MostrarBanner(); }
            }
            catch (SessaoExpiradaException)
            {
                // Em segundo plano não se arranca o usuário do que ele está fazendo: só para
                // o monitor. A próxima ação dele encontra a sessão morta e vai ao login.
                Logger.Log("Antimicrobianos: sessão expirada — monitor parado.");
                if (sessao == _sessaoId) PararMonitor();
                return;
            }
            catch (Exception ex) { Logger.Log("Antimicrobianos: falha no monitor: " + ex.Message); }
            finally
            {
                _busy = false;
                var vFim = _viva;
                if (vFim != null && !vFim.IsDisposed && !vFim.Disposing) vFim.Destravar();
                if (sessao == _sessaoId && _monitor != null && _monitorHost != null) _monitor.Start();
            }
        }

        // Só para conferir o desenho da tela (flag --atb-preview). Pacientes fictícios,
        // posologias reais do hospital — inclusive os casos chatos: dose de 2 unidades,
        // frequência que o programa não reconhece e dose avulsa.
        private void CarregarExemplo()
        {
            _itens.Clear();
            _itens.Add(Exemplo(1, "MARIA EXEMPLO DA SILVA", "UTI - LADO A", 530001,
                "MEROPENEM 1000MG FR. INJ", 1, "FR C/1000MG", "ENDOVENOSA", "08|08H - 08|08H", "D8/14", 649));
            _itens.Add(Exemplo(1, "MARIA EXEMPLO DA SILVA", "UTI - LADO A", 530001,
                "VANCOMICINA 500MG PO LIOF INJ FA C/500MG", 2, "FA C/500MG", "ENDOVENOSA", "12|12H - 12|12H", "D5/7", 290));
            // Dose em MEDIDA (o caso da conversão): 250 mg do frasco de 500 mg = 1 frasco
            // por horário. É o mesmo produto do exemplo acima, prescrito de outro jeito —
            // na aba Estoque os dois somam juntos, como os reais somam.
            _itens.Add(Exemplo(4, "CARLOS EXEMPLO LIMA", "7º ANDAR", 530004,
                "VANCOMICINA 500MG PO LIOF INJ FA C/500MG", 250, "MILIGRAMA", "ENDOVENOSA", "08|08H - 08|08H", "D2/7", 290));
            // O caso real de 29/08: vanco de 250mg POR SONDA — via não parenteral divide
            // o frasco (0,5 por horário), e a célula tem que mostrar "0,5 FRASCO".
            _itens.Add(Exemplo(1, "MARIA EXEMPLO DA SILVA", "UTI - LADO A", 530001,
                "VANCOMICINA 500MG PO LIOF INJ FA C/500MG", 250, "MILIGRAMA", "SONDA NASOENTERICA", "6HS/6HS", "D2/7", 290));
            _itens.Add(Exemplo(1, "MARIA EXEMPLO DA SILVA", "UTI - LADO A", 530001,
                "CEFTRIAXONA AMP FA C/1G EV", 2, "FA C/1G", "ENDOVENOSA", "AGORA", "D1/1", 243));
            _itens.Add(Exemplo(2, "JOAO EXEMPLO PEREIRA", "3º ANDAR", 530002,
                "MAXCEF (CEFEPIMA) 1G FA EV", 2, "FA C/1G", "ENDOVENOSA", "08|08H - 08|08H", "D5/7", 136));
            _itens.Add(Exemplo(3, "ANA EXEMPLO SOUZA", "5º ANDAR", 530003,
                "PIPERACILINA + TAZOBACTAM 4,5 FRASCO EV", 1, "FRASCO C/ 4,5G", "ENDOVENOSA", "6HS/6HS", "D3/7", 537));
            _itens.Add(Exemplo(3, "ANA EXEMPLO SOUZA", "5º ANDAR", 530003,
                "AZITROMICINA 500MG CP", 1, "COMP C/500MG", "ORAL", "- - 8HRS MANHA", "D2/5", 255));
            _itens.Add(Exemplo(2, "JOAO EXEMPLO PEREIRA", "3º ANDAR", 530002,
                "CEFTAZIDIMA 2000MG + AVIBACTAM 500MG (TORGENA)", 1, "FA", "ENDOVENOSA", "08|08H - 08|08H", "D2/7", 3));
            _itens.Add(Exemplo(4, "CARLOS EXEMPLO LIMA", "7º ANDAR", 530004,
                "AMICACINA 250MG/ML SOL INJ AMP C/ 2ML", 2, "AMP C/500MG", "ENDOVENOSA", "- - 16HRS TARDE", "D6/7", -1));
            // Bisnaga e comprimido avulso ("UNIDADE"): os dois formatos que o pedido de
            // padronização da dose (29/08/2026) citou — o laço de screenshot confere
            // "1 BG" e "1 CP" por aqui.
            _itens.Add(Exemplo(2, "JOAO EXEMPLO PEREIRA", "3º ANDAR", 530002,
                "NEOMICINA + BACITRACINA POMADA BG C/50G", 1, "BISNAGA C/50G", "TOPICA", "12|12H - 12|12H", "D3/10", 41));
            _itens.Add(Exemplo(4, "CARLOS EXEMPLO LIMA", "7º ANDAR", 530004,
                "METRONIDAZOL 400MG CP", 1, "UNIDADE", "ORAL", "08|08H - 08|08H", "D2/7", 320));
            // Sinal da justificativa: um justificado, um sem, um dispensado pelo protocolo.
            foreach (var it in _itens)
            {
                it.ExigeJustificativa = it.Produto.IndexOf("AZITROMICINA", StringComparison.Ordinal) < 0;
                bool pendente = it.Produto.IndexOf("CEFTAZIDIMA", StringComparison.Ordinal) >= 0
                             || it.Produto.IndexOf("AMICACINA", StringComparison.Ordinal) >= 0;
                it.JustificadoEm = pendente ? DateTime.MinValue : DateTime.Today.AddDays(-1).AddHours(14);
            }
            // Catálogo fictício: os prescritos acima mais alguns que ninguém está tomando,
            // que é o que a aba Estoque precisa mostrar (a lista é do hospital, não da
            // prescrição do dia).
            _catalogo.Clear();
            foreach (var it in _itens)
                if (it.EstoqueConhecido)
                    _catalogo.Add(new SaldoProduto { Codigo = it.CodigoProduto, Nome = it.Produto, Saldo = it.Estoque });
            _catalogo.Add(new SaldoProduto { Codigo = 9100001, Nome = "POLIMIXINA B 500.000 UI PO LIOF FA", Saldo = 469 });
            _catalogo.Add(new SaldoProduto { Codigo = 9100002, Nome = "OXACILINA SOL INJ IM/IV FA C/500MG", Saldo = 522 });
            _catalogo.Add(new SaldoProduto { Codigo = 9100003, Nome = "FLUCONAZOL 150MG CP", Saldo = 160 });
            _catalogo.Add(new SaldoProduto { Codigo = 9100004, Nome = "BENZILPENICILINA 1.200.000 UI", Saldo = 23 });

            _semJustificativa = 2;
            _atualizadoEm = DateTime.Now;
            AplicarEstadoDoFiltro();
            RenderGrid();
            banner.SetState(StatusKind.Info, "Pré-visualização: pacientes fictícios, sem consultar o MV.");
        }

        private static readonly Dictionary<string, int> _codigosExemplo =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static int CodigoExemplo(string produto)
        {
            int c;
            if (!_codigosExemplo.TryGetValue(produto, out c))
            { c = 9000001 + _codigosExemplo.Count; _codigosExemplo[produto] = c; }
            return c;
        }

        private static ItemAntimicrobiano Exemplo(int pid, string nome, string setor, int atend,
            string produto, decimal qt, string unidade, string via, string freq, string dias, decimal estoque)
        {
            var it = new ItemAntimicrobiano
            {
                PacienteId = pid, Paciente = nome, Setor = setor, Atendimento = atend,
                Produto = produto, Quantidade = qt, Unidade = unidade, Via = via,
                Frequencia = freq, Dias = dias,
                // Código FICTÍCIO, mas por PRODUTO — é ele que agrupa a aba Estoque. Saía do
                // id do paciente e a pré-visualização mostrava quatro linhas (uma por
                // pessoa) em vez de uma por antimicrobiano.
                CodigoProduto = CodigoExemplo(produto),
                ProdutoEstoque = produto, Estoque = estoque, EstoqueConhecido = estoque >= 0
            };
            int a, t;
            if (Posologia.LerDias(dias, out a, out t)) { it.DiaAtual = a; it.DiasPrevistos = t; }
            bool est;
            it.AplicacoesPorDia = Posologia.AplicacoesPorDia(freq, out est);
            it.FrequenciaEstimada = est;
            it.NaoRecorrente = Posologia.EhAvulsa(freq);
            return it;
        }

        // Preenche o saldo de cada linha. Devolve quantos itens ficaram sem ligação com o
        // estoque — a tela avisa, porque uma coluna "—" isolada passa despercebida.
        // -1 = a consulta INTEIRA falhou (portão ocupado ou erro): nada foi ligado e o
        // chamador precisa avisar que os saldos não são desta varredura.
        private static async Task<int> LigarEstoqueAsync(Session sessao_, List<ItemAntimicrobiano> itens)
        {
            if (itens.Count == 0) return 0;
            bool tenho = await MvGate.EntrarAsync("antimicrobianos-estoque", 60000);
            if (!tenho) { Logger.Log("Antimicrobianos: servidor ocupado, saldo não consultado."); return -1; }
            try
            {
                var pos = await MorphisClient.GerarPosicaoEstoqueAsync(
                    sessao_, EspecieMedicamentos, ClasseAntiInfecciosos);
                var cat = new PonteEstoque.Catalogo(pos.Produtos);
                _catalogo.Clear(); _catalogo.AddRange(pos.Produtos);

                // 1ª passada: catálogo da classe de anti-infecciosos (o mesmo que a aba
                // Estoque lista). Como ele é PARCIAL, ausência aqui não quer dizer saldo
                // zero — quem não ligar ganha uma 2ª chance abaixo.
                var pendentes = new List<ItemAntimicrobiano>();
                foreach (var it in itens)
                {
                    var p = PonteEstoque.Resolver(cat, it.Produto, it.Unidade, false);
                    if (p == null) { pendentes.Add(it); continue; }
                    it.CodigoProduto = p.Codigo;
                    it.ProdutoEstoque = p.Nome;
                    it.Estoque = p.Saldo;
                    it.EstoqueConhecido = true;
                }

                // 2ª passada: a espécie de medicamentos INTEIRA (~345 produtos, +1,3 s),
                // só quando sobrou pendência. Antimicrobiano fora da classe existe: o
                // creme vaginal de metronidazol é de outra classe no cadastro do MV e o
                // saldo dele só aparece aqui.
                int semLigacao = 0;
                if (pendentes.Count > 0)
                {
                    var largo = await MorphisClient.GerarPosicaoPorEstoqueAsync(
                        sessao_, 0, EspecieMedicamentos, 0);
                    var catLargo = new PonteEstoque.Catalogo(largo.Produtos);
                    bool novosNoLog = false;
                    foreach (var it in pendentes)
                    {
                        var p = PonteEstoque.Resolver(catLargo, it.Produto, it.Unidade, true);
                        if (p == null)
                        {
                            semLigacao++;
                            // Cada nome entra no log UMA vez por sessão — o monitor roda
                            // de 3 em 3 minutos e repetiria isto o plantão inteiro.
                            if (_semLigacaoLogados.Add(it.Produto))
                            {
                                novosNoLog = true;
                                Logger.Log("Antimicrobianos: sem ligação com o estoque: " + it.Produto);
                            }
                            continue;
                        }
                        it.CodigoProduto = p.Codigo;
                        it.ProdutoEstoque = p.Nome;
                        it.Estoque = p.Saldo;
                        it.EstoqueConhecido = true;
                    }
                    // O caminho do arquivo fica no log, para quem administra o programa —
                    // a tela fala com o farmacêutico, que não edita txt na pasta do sistema.
                    if (novosNoLog)
                        Logger.Log("Antimicrobianos: " + semLigacao + " item(ns) sem ligação com o"
                            + " estoque — cadastre o de-para em dados/antimicrobianos.txt.");
                }
                return semLigacao;
            }
            catch (SessaoExpiradaException) { throw; }
            catch (Exception ex)
            {
                // Saldo é complemento: sem ele a lista de pacientes ainda vale — mas o
                // -1 faz o banner avisar que os saldos não são desta varredura.
                Logger.Log("Antimicrobianos: falha ao ler o estoque: " + ex.Message);
                return -1;
            }
            finally { MvGate.Sair(); }
        }

        private void Travar(string texto, bool leve)
        {
            btnAtualizar.Enabled = false; btnAtualizar.Text = texto;
            if (leve) return;
            txtBusca.Enabled = false; btnSemJust.Enabled = false;
        }
        private void Destravar()
        {
            btnAtualizar.Enabled = true; btnAtualizar.Text = "Atualizar"; txtBusca.Enabled = true;
            btnSemJust.Enabled = !_abaEstoque;   // no Estoque ele continua desligado
        }

        // Ordena por setor > paciente > antimicrobiano e desenha, repetindo paciente/setor
        // só na primeira linha de cada pessoa — quem tem três antibióticos vira um bloco só.
        private void RenderGrid()
        {
            _renderPendente = false; _adiadoDesde = DateTime.MinValue;
            if (_abaEstoque) RenderEstoque(); else RenderPacientes();
        }

        // Redesenho vindo do MONITOR: com o mouse sobre a grade, adia e aplica quando o
        // ponteiro sair (MouseLeave em NovaGridBase) — as linhas deslizariam sob os olhos
        // de quem lê. Teto de 2 min para o mouse esquecido ali não congelar a tela.
        private void RenderGridAutomatico()
        {
            var g = _abaEstoque ? gridEstoque : grid;
            if (g != null && g.Visible && MouseSobreAGrade(g)
                && (!_renderPendente || (DateTime.Now - _adiadoDesde).TotalMinutes < 2))
            {
                if (!_renderPendente) { _renderPendente = true; _adiadoDesde = DateTime.Now; }
                return;
            }
            RenderGrid();
        }

        private static bool MouseSobreAGrade(Control g)
        {
            try { return g.ClientRectangle.Contains(g.PointToClient(Cursor.Position)); }
            catch { return false; }
        }

        // Atalhos globais (F5 / Ctrl+F) roteados pela MainWindow.
        public void ComandoAtualizar()
        {
            Consultar();   // mesmo caminho do botão; se já há varredura, ele mesmo avisa
        }

        public void ComandoFocarBusca()
        {
            if (txtBusca != null && txtBusca.CanFocus)
            { txtBusca.Focus(); txtBusca.SelectAll(); }
        }

        private void RenderPacientes()
        {
            gridEstoque.Visible = false;
            string busca = Normalizar(txtBusca != null ? txtBusca.Text : "");
            var mostrar = new List<ItemAntimicrobiano>();
            bool soPendentes = _soPendentes;
            foreach (var i in _itens)
            {
                if (soPendentes && (!i.ExigeJustificativa || i.Justificado)) continue;
                if (busca.Length == 0
                    || Normalizar(i.Paciente).Contains(busca)
                    || Normalizar(i.Setor).Contains(busca)
                    || Normalizar(i.Produto).Contains(busca)) mostrar.Add(i);
            }

            mostrar.Sort(delegate(ItemAntimicrobiano a, ItemAntimicrobiano b)
            {
                int c = string.Compare(a.Setor ?? "", b.Setor ?? "", StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = string.Compare(a.Paciente ?? "", b.Paciente ?? "", StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Produto ?? "", b.Produto ?? "", StringComparison.OrdinalIgnoreCase);
            });

            // A atualização automática redesenha a grade inteira; sem guardar o topo, a
            // lista saltaria para o começo de 3 em 3 minutos sob os olhos de quem lê.
            int topo = 0;
            try { if (grid.Rows.Count > 0 && grid.FirstDisplayedScrollingRowIndex >= 0) topo = grid.FirstDisplayedScrollingRowIndex; }
            catch { topo = 0; }
            // E sem guardar a SELEÇÃO (por chave de negócio, nunca por índice: linhas
            // novas deslocam tudo), quem marcou um paciente para acompanhar o perdia a
            // cada ciclo do monitor.
            string chaveSel = null;
            if (grid.SelectedRows.Count > 0)
            {
                var selAntes = grid.SelectedRows[0].Tag as ItemAntimicrobiano;
                if (selAntes != null) chaveSel = selAntes.Atendimento + "|" + selAntes.Produto;
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            _faixaDaLinha.Clear();
            bool faixa = false;
            int anterior = -1;
            for (int i = 0; i < mostrar.Count; i++)
            {
                var it = mostrar[i];
                bool primeiro = it.PacienteId != anterior;
                anterior = it.PacienteId;

                if (primeiro) faixa = !faixa;   // zebra por PACIENTE, não por linha

                int r = grid.Rows.Add(
                    primeiro ? NomeProprio(it.Paciente) : "",
                    primeiro ? (it.Atendimento > 0 ? it.Atendimento.ToString() : "") : "",
                    it.Produto,
                    Dose(it),
                    Frequencia(it),
                    string.IsNullOrEmpty(it.Dias) ? "—" : it.Dias,
                    SinalJustificativa(it));
                var linha = grid.Rows[r];
                linha.Tag = it;
                _faixaDaLinha[r] = faixa;
                // Nome de produto quase sempre passa da largura: o inteiro fica no tooltip.
                linha.Cells["produto"].ToolTipText = it.Produto;
                // A célula padronizada ("2 FRASCOS") não engole o dado do MV: a prescrição
                // original — quantidade, unidade e via — fica no tooltip da dose, com a
                // conversão explicada quando houve.
                linha.Cells["dose"].ToolTipText = "Prescrito: " + it.Quantidade.ToString("0.##")
                    + " " + (it.Unidade ?? "").Trim()
                    + (string.IsNullOrEmpty(it.Via) ? "" : " · " + it.Via.Trim())
                    + (it.DoseConvertida
                        ? "\nConvertido para " + it.QuantidadePorAplicacao.ToString("0.##")
                          + " embalagem(ns) por horário, na apresentação do estoque"
                        : "");
                // O setor deixou de ter coluna própria (a lista de colunas foi pedida sem
                // ele), mas continua sendo o que a CCIH usa para achar a pessoa: fica aqui,
                // junto do nome inteiro, e a busca por setor continua funcionando.
                linha.Cells["paciente"].ToolTipText = (it.Paciente ?? "")
                    + (string.IsNullOrEmpty(it.Setor) ? "" : "\n" + NomeProprio(it.Setor));
                linha.Cells["dias"].ToolTipText = it.DiasPrevistos <= 0
                    ? "O médico não informou por quantos dias"
                    : it.DiasRestantes == 0
                        ? "Já passou dos dias previstos pelo médico"
                        : it.DiasRestantes == 1
                            ? "Último dia — ainda recebe hoje"
                            : "Faltam " + it.DiasRestantes + " dias, contando hoje";
                // A conta que este paciente contribui para a aba Estoque continua aqui, no
                // tooltip da frequência: é onde ela nasce (dose × vezes ao dia × dias).
                linha.Cells["freq"].ToolTipText = it.Frequencia
                    + (it.AplicacoesPorDia > 0
                        ? "\n" + it.ConsumoPorDia.ToString("0.##") + " por dia"
                          + (it.APrever > 0
                              ? "  ·  " + it.APrever.ToString("0.##")
                                + (it.SoHoje.Length > 0 ? " hoje (" + it.SoHoje + ")" : " até o fim do tratamento")
                              : "")
                          // Sem esta linha, "250 × MILIGRAMA" na coluna da dose e "3 por
                          // dia" aqui pareceriam contradição: a conta é em EMBALAGENS.
                          + (it.DoseConvertida
                              ? "\nDose de " + it.Quantidade.ToString("0.##") + " " + it.Unidade
                                + " = " + it.QuantidadePorAplicacao.ToString("0.##")
                                + " embalagem(ns) por horário, na apresentação do estoque"
                              : "")
                          + (it.FrequenciaEstimada ? "\n* vezes ao dia estimadas pelo programa" : "")
                        : "\nFrequência não reconhecida — avise quem administra o programa para cadastrá-la.");
                linha.Cells["justif"].ToolTipText = !it.ExigeJustificativa
                    ? "Dispensada pelo protocolo do hospital — este antimicrobiano não exige justificativa"
                    : it.Justificado
                        ? "Justificativa emitida em " + it.JustificadoEm.ToString("dd/MM/yyyy HH:mm")
                        : "Sem justificativa emitida desde o início deste antimicrobiano"
                          + (it.InicioTratamento == DateTime.MinValue ? "" : " (" + it.InicioTratamento.ToString("dd/MM") + ")");
                // ACRESCENTA a ressalva, não substitui: sobrescrevendo, justo a linha que
                // exige investigação perdia o nome inteiro do produto, a conta do consumo e
                // a data da justificativa.
                if (it.Ressalva.Length > 0)
                    foreach (DataGridViewCell c in linha.Cells)
                        c.ToolTipText = "Fora do consumo: " + it.Ressalva
                            + (string.IsNullOrEmpty(c.ToolTipText) ? "" : "\n" + c.ToolTipText);
            }
            grid.ResumeLayout();
            try { if (topo > 0 && topo < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = topo; }
            catch { }

            // Nada de linha 0 "selecionada" sozinha ao popular; se o usuário tinha uma
            // linha marcada, ela volta pela chave (e some com honestidade se saiu da lista).
            grid.LimparSelecao();
            if (chaveSel != null)
                foreach (DataGridViewRow rw in grid.Rows)
                {
                    var itSel = rw.Tag as ItemAntimicrobiano;
                    if (itSel != null && (itSel.Atendimento + "|" + itSel.Produto) == chaveSel)
                    { rw.Selected = true; break; }
                }

            grid.Visible = mostrar.Count > 0;
            lblVazio.Visible = mostrar.Count == 0;
            if (mostrar.Count == 0)
                lblVazio.Text = _itens.Count > 0 ? "Nada encontrado para essa busca." : "Nenhum antimicrobiano prescrito agora.";
        }

        // Uma linha da aba Estoque: um produto, com tudo o que os pacientes em uso ainda
        // vão levar dele somado.
        private class LinhaEstoque
        {
            public string Nome;                 // nome do catálogo (ou o da prescrição, se não ligou)
            public int Codigo;                  // produto do estoque; 0 = não ligado
            public bool EstoqueConhecido;
            public decimal Estoque;
            public decimal Consumo;             // soma do que falta dispensar
            public int Itens;                   // prescrições que entraram
            public int Ressalvados;             // itens que NÃO entraram na soma (e por quê)
            public int Parciais;                // itens em que a soma cobre só o dia de hoje
            public readonly List<string> Motivos = new List<string>();
            public readonly List<string> Parcialidades = new List<string>();
            public readonly HashSet<int> Pacientes = new HashSet<int>();
            public readonly List<string> Nomes = new List<string>();   // grafias do MV agrupadas aqui
        }

        // Agrupa por PRODUTO DO ESTOQUE, não pelo texto da prescrição: "CEFTRIAXONA AMP FA
        // C/1G EV" e "CEFTRIAXONA 1G FR" são o mesmo item de catálogo e precisam somar
        // juntos, senão o consumo aparece repartido em duas linhas e nenhuma delas assusta.
        // O que a ponte de estoque não conseguiu ligar fica em linha própria, pelo nome
        // cru — sem saldo, mas com o consumo visível.
        private void RenderEstoque()
        {
            grid.Visible = false;
            string busca = Normalizar(txtBusca != null ? txtBusca.Text : "");

            var mapa = new Dictionary<string, LinhaEstoque>(StringComparer.Ordinal);
            var ordem = new List<LinhaEstoque>();
            // Primeiro o catálogo inteiro: assim o produto que ninguém está tomando hoje
            // aparece com saldo e zero pacientes, em vez de simplesmente não existir. As
            // prescrições depois só somam consumo em cima das linhas já criadas.
            foreach (var p in _catalogo)
            {
                if (p == null || string.IsNullOrEmpty(p.Nome)) continue;
                string k = "#" + p.Codigo;
                if (mapa.ContainsKey(k)) continue;
                var novo = new LinhaEstoque();
                novo.Nome = p.Nome; novo.Codigo = p.Codigo;
                novo.EstoqueConhecido = true; novo.Estoque = p.Saldo;
                mapa[k] = novo; ordem.Add(novo);
            }
            foreach (var it in _itens)
            {
                string chave = it.EstoqueConhecido && it.CodigoProduto > 0
                    ? "#" + it.CodigoProduto
                    : "?" + Normalizar(it.Produto);
                LinhaEstoque l;
                if (!mapa.TryGetValue(chave, out l))
                {
                    l = new LinhaEstoque();
                    l.Nome = it.EstoqueConhecido && !string.IsNullOrEmpty(it.ProdutoEstoque)
                        ? it.ProdutoEstoque : it.Produto;
                    l.Codigo = it.CodigoProduto;
                    l.EstoqueConhecido = it.EstoqueConhecido;
                    l.Estoque = it.Estoque;
                    mapa[chave] = l; ordem.Add(l);
                }
                l.Itens++;
                if (it.PacienteId > 0) l.Pacientes.Add(it.PacienteId);
                if (!l.Nomes.Contains(it.Produto)) l.Nomes.Add(it.Produto);
                // O que sobrou de incalculável (frequência não reconhecida, quantidade não
                // lida) fica fora da soma e é contado; quem entrou só com o consumo de hoje
                // é contado à parte. Os dois viram frase no banner e no tooltip — nunca um
                // símbolo no meio do número.
                if (it.Ressalva.Length > 0)
                {
                    l.Ressalvados++;
                    if (!l.Motivos.Contains(it.Ressalva)) l.Motivos.Add(it.Ressalva);
                }
                else
                {
                    l.Consumo += it.APrever;
                    if (it.SoHoje.Length > 0)
                    {
                        l.Parciais++;
                        if (!l.Parcialidades.Contains(it.SoHoje)) l.Parcialidades.Add(it.SoHoje);
                    }
                }
            }

            var mostrar = new List<LinhaEstoque>();
            foreach (var l in ordem)
                if (busca.Length == 0 || Normalizar(l.Nome).Contains(busca) || ContemAlgum(l.Nomes, busca))
                    mostrar.Add(l);

            // Ordem alfabética, sempre. Esta lista é o catálogo da casa e se usa procurando
            // um nome — com a ordem mudando conforme o aperto do dia, o produto trocava de
            // lugar a cada atualização e não dava para decorar onde ele fica. Quem precisa
            // de atenção continua saltando: é para isso que existe a bolinha.
            mostrar.Sort(delegate(LinhaEstoque a, LinhaEstoque b)
            {
                return string.Compare(Normalizar(a.Nome), Normalizar(b.Nome), StringComparison.Ordinal);
            });

            int topo = 0;
            try { if (gridEstoque.Rows.Count > 0 && gridEstoque.FirstDisplayedScrollingRowIndex >= 0) topo = gridEstoque.FirstDisplayedScrollingRowIndex; }
            catch { topo = 0; }
            // Seleção preservada por chave (código do produto; sem código, o nome), como
            // na aba de pacientes: o refresh do monitor não pode roubá-la para o topo.
            string chaveSel = null;
            if (gridEstoque.SelectedRows.Count > 0)
            {
                var selAntes = gridEstoque.SelectedRows[0].Tag as LinhaEstoque;
                if (selAntes != null)
                    chaveSel = selAntes.Codigo > 0 ? "#" + selAntes.Codigo : "?" + Normalizar(selAntes.Nome);
            }

            gridEstoque.SuspendLayout();
            gridEstoque.Rows.Clear();
            foreach (var l in mostrar)
            {
                // Quem não tem ninguém em uso fica com as colunas de gente VAZIAS: um "0"
                // (ou um traço) repetido trinta vezes é ruído — o vazio some sozinho.
                // A Sobra só existe quando dá para calcular (em uso + consumo projetado +
                // saldo conhecido); nos outros casos a célula fica vazia e o tooltip explica.
                int n = Nivel(l);
                int r = gridEstoque.Rows.Add(
                    l.Nome,
                    l.Itens == 0 ? "" : (l.Pacientes.Count > 0 ? l.Pacientes.Count.ToString() : l.Itens.ToString()),
                    l.Itens == 0 ? "" : l.Consumo.ToString("0.##"),
                    l.EstoqueConhecido ? l.Estoque.ToString("0.##") : "",
                    n <= 2 ? (l.Estoque - l.Consumo).ToString("0.##") : "");
                var linha = gridEstoque.Rows[r];
                linha.Tag = l;

                linha.Cells["produto"].ToolTipText = l.EstoqueConhecido
                    ? "Produto " + l.Codigo + " — " + l.Nome
                      + (l.Nomes.Count > 1 ? "\nNa prescrição aparece como:\n· " + string.Join("\n· ", l.Nomes.ToArray()) : "")
                    : "Este item não foi ligado a um produto do estoque, então o saldo não aparece."
                      + "\nAvise quem administra o programa para cadastrar a ligação.";
                linha.Cells["pacientes"].ToolTipText = l.Itens == 0
                    ? "Ninguém em uso deste antimicrobiano agora"
                    : l.Itens == l.Pacientes.Count
                        ? l.Itens + " prescrição(ões) em curso"
                        : l.Itens + " prescrições em " + l.Pacientes.Count + " pacientes";
                linha.Cells["consumo"].ToolTipText = TooltipConsumo(l);
                linha.Cells["estoque"].ToolTipText = l.EstoqueConhecido
                    ? "Saldo somando todos os estoques do hospital"
                    : "Sem saldo: este item não foi ligado ao catálogo";
                linha.Cells["sobra"].ToolTipText = DetalheSituacao(l);
            }
            gridEstoque.ResumeLayout();
            try { if (topo > 0 && topo < gridEstoque.Rows.Count) gridEstoque.FirstDisplayedScrollingRowIndex = topo; }
            catch { }

            gridEstoque.LimparSelecao();
            if (chaveSel != null)
                foreach (DataGridViewRow rw in gridEstoque.Rows)
                {
                    var lSel = rw.Tag as LinhaEstoque;
                    if (lSel != null && (lSel.Codigo > 0 ? "#" + lSel.Codigo : "?" + Normalizar(lSel.Nome)) == chaveSel)
                    { rw.Selected = true; break; }
                }

            gridEstoque.Visible = mostrar.Count > 0;
            lblVazio.Visible = mostrar.Count == 0;
            if (mostrar.Count == 0)
                lblVazio.Text = ordem.Count > 0 ? "Nada encontrado para essa busca." : "Estoque de antimicrobianos ainda não lido.";
        }

        private static string TooltipConsumo(LinhaEstoque l)
        {
            if (l.Itens == 0) return "Nada a dispensar: não há prescrição em curso.";
            var sb = new System.Text.StringBuilder();
            sb.Append("Soma de ").Append(l.Itens - l.Ressalvados)
              .Append(l.Itens - l.Ressalvados == 1 ? " prescrição" : " prescrições")
              .Append(" até o fim dos tratamentos em curso.");
            if (l.Parciais > 0)
                sb.Append("\nEm ").Append(l.Parciais).Append(" delas a conta cobre só hoje (")
                  .Append(string.Join("; ", l.Parcialidades.ToArray()))
                  .Append("), então o consumo real é maior.");
            if (l.Ressalvados > 0)
                sb.Append("\nFicaram de fora ").Append(l.Ressalvados)
                  .Append(l.Ressalvados == 1 ? " prescrição: " : " prescrições: ")
                  .Append(string.Join("; ", l.Motivos.ToArray()))
                  .Append(".\nO consumo real é maior que este número.");
            return sb.ToString();
        }

        private static bool ContemAlgum(List<string> nomes, string busca)
        {
            foreach (string n in nomes) if (Normalizar(n).Contains(busca)) return true;
            return false;
        }

        // Quantas vezes o saldo cobre o que falta dispensar. É a chave de ordenação dentro
        // de cada nível.
        private static decimal Folga(LinhaEstoque l)
        {
            if (!l.EstoqueConhecido) return 9999m;
            if (l.Consumo <= 0) return 9998m;
            return l.Estoque / l.Consumo;
        }

        // A cor da bolinha, e também a ordem da lista.
        //   0 vermelho  o saldo não cobre os tratamentos em curso
        //   1 amarelo   cobre, mas com menos de 2× de folga
        //   2 verde     cobre com folga
        //   3 vazio     está em uso, mas não dá para projetar (ou não tem saldo)
        //   4 vazio     ninguém em uso agora
        //
        // Não existe bolinha por "nível absoluto de estoque" porque o MV não nos dá ponto
        // de pedido nem estoque mínimo — a única referência honesta é o consumo previsto.
        private static int Nivel(LinhaEstoque l)
        {
            if (l.Itens == 0) return 4;
            if (!l.EstoqueConhecido || l.Consumo <= 0) return 3;
            decimal f = Folga(l);
            return f < 1 ? 0 : f < 2 ? 1 : 2;
        }

        private static string DetalheSituacao(LinhaEstoque l)
        {
            if (l.Itens == 0)
                return "Ninguém em uso agora — nada a comparar com o saldo.";
            if (!l.EstoqueConhecido)
                return "Sem saldo para comparar — o produto não foi ligado ao catálogo do estoque.";
            if (l.Consumo <= 0)
                return "Nada projetado a dispensar" + (l.Ressalvados > 0
                    ? ", mas " + l.Ressalvados + " prescrição(ões) não puderam ser projetadas."
                    : " (os tratamentos terminam hoje ou são dose avulsa).");
            decimal sobra = l.Estoque - l.Consumo;
            return sobra >= 0
                ? "Sobram " + sobra.ToString("0.##") + " depois de atender os tratamentos em curso"
                  + " (o saldo cobre " + Folga(l).ToString("0.#") + "× o consumo)."
                : "Faltam " + (-sobra).ToString("0.##") + " para fechar os tratamentos em curso.";
        }

        // "2 × FA C/1G · EV" — a quantidade é POR HORÁRIO, e é o fator que mais se erra:
        // 2 frascos de 12/12h são 4 por dia, não 2. Quando é 1 o "1 ×" some: ele aparecia em
        // quase toda linha, roubava espaço da unidade (que ficava cortada) e ainda fazia o
        // 2× se perder no meio de dezenas de 1×. Sem ele, quem tem dose dobrada salta à vista.
        // A célula mostra a dose em EMBALAGENS ("2 FRASCOS", "1 BG", "1 CP") — pedido do
        // farmacêutico (29/08/2026): é o número que ele dispensa. A via e a concentração
        // saíram da célula (a apresentação já está no nome do produto; a prescrição
        // original do MV, com via, fica no tooltip). Quando NÃO dá para nomear a
        // embalagem com segurança — dose em medida sem conversão, fração de embalagem,
        // apresentação desconhecida — a célula mantém o formato bruto: melhor o texto
        // cru visível do que um palpite bonito e errado.
        private static string Dose(ItemAntimicrobiano it)
        {
            decimal q = it.QuantidadePorAplicacao;
            if (q > 0)
            {
                decimal fator; char baseUn;
                bool medida = SoulmvKit.Core.ConversaoDose.MedidaPura(it.Unidade, out fator, out baseUn);
                string emb = null;
                if (!medida)
                    emb = NomeEmbalagem(PrimeiroToken(Unidade(it.Unidade)), q, it);
                else if (q * 4 == decimal.Truncate(q * 4))
                    // Dose prescrita em mg/UI/mL e convertida: a embalagem exibida é a
                    // MESMA que fundamentou a conversão (o token antes do "C/..." no nome
                    // do produto) — nunca um chute. Frações entram só quando são limpas
                    // (inteiros, meios e quartos): a vanco por sonda de 250mg é "0,5
                    // FRASCO" (caso real de 29/08); já 1/6 de um frasco de suspensão
                    // renderizaria "0,17", que arredonda — esse fica no formato bruto.
                    emb = NomeEmbalagem(
                        EmbalagemDaConversao(it.ProdutoEstoque) ?? EmbalagemDaConversao(it.Produto), q, it);
                if (emb != null) return q.ToString("0.##") + " " + emb;
            }
            // Formato bruto (sem a via): quantidade × unidade como o MV mandou.
            string s = Unidade(it.Unidade);
            if (it.Quantidade != 1) s = it.Quantidade.ToString("0.##") + " × " + s;
            return s;
        }

        private static string PrimeiroToken(string s)
        {
            int i = (s ?? "").IndexOf(' ');
            return i < 0 ? (s ?? "") : s.Substring(0, i);
        }

        // Nome curto da embalagem para a célula (plural quando a sigla flexiona bem).
        // null = embalagem que não sabemos nomear — a célula fica no formato bruto.
        // FA e FR viram "FRASCO(S)" e UN vira "CP" a pedido; UN só vira CP quando o
        // nome do produto diz que é comprimido, senão um óvulo avulso viraria "CP".
        private static string NomeEmbalagem(string token, decimal n, ItemAntimicrobiano it)
        {
            switch ((token ?? "").ToUpperInvariant())
            {
                // n ≤ 1 fica no singular: "0,5 FRASCO", "1 FRASCO", "2 FRASCOS".
                case "FA": case "FR": case "FRA": case "FRASCO":
                    return n <= 1 ? "FRASCO" : "FRASCOS";
                case "AMP": case "AMPOLA": return "AMP";
                case "BG": case "BISNAGA": return "BG";
                case "CP": case "COMP": case "CPR": case "COMPRIMIDO": return "CP";
                case "CAP": case "CAPS": case "CAPSULA": case "CÁPSULA": return "CAP";
                case "BOLS": case "BOLSA": return n <= 1 ? "BOLSA" : "BOLSAS";
                case "ENV": case "ENVELOPE": return "ENV";
                case "UN": case "UND": case "UNID": case "UNIDADE":
                    return EhComprimido(it) ? "CP" : "UN";
                default: return null;
            }
        }

        // "FA C/500MG" no nome do produto -> "FA". null quando o nome não declara a
        // embalagem com conteúdo — e aí não se converte a exibição.
        private static string EmbalagemDaConversao(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(nome.ToUpperInvariant(),
                @"\b([A-ZÇÁÂÃÉÊÍÓÔÕÚ]{2,12})\s+C/\s*\d");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool EhComprimido(ItemAntimicrobiano it)
        {
            string nomes = (it.Produto ?? "") + " " + (it.ProdutoEstoque ?? "");
            return System.Text.RegularExpressions.Regex.IsMatch(nomes.ToUpperInvariant(),
                @"\b(CP|COMP|CPR|COMPRIMIDOS?)\b");
        }

        // Glifos do Segoe MDL2 (a mesma fonte do ícone de impressora nas outras abas):
        // E73E = certo, E711 = errado, E738 = traço do "dispensada" — um "–" de TEXTO
        // não existe nessa fonte de símbolos e renderizava como quadrado vazio (.notdef).
        private const string GlifoOk = "";        // CheckMark
        private const string GlifoFalta = "";     // Cancel (xis)
        private const string GlifoDispensa = "";  // Remove (traço)

        private static string SinalJustificativa(ItemAntimicrobiano it)
        {
            if (!it.ExigeJustificativa) return GlifoDispensa;
            return it.Justificado ? GlifoOk : GlifoFalta;
        }

        // A unidade do MV vem cheia de enchimento: "FRASCO C/ 4,5G", "COMP C/500MG".
        // O "C/" não informa nada e era justamente o que empurrava a concentração — que é o
        // que a farmácia lê — para fora da coluna. Vira "FR 4,5G", "CP 500MG".
        private static string Unidade(string u)
        {
            string s = (u ?? "").Trim();
            if (s.Length == 0) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*C/\s*", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^FRASCO\b", "FR");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^COMPRIMIDO\b|^COMP\b", "CP");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^CAPSULA\b|^CÁPSULA\b", "CAP");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^AMPOLA\b", "AMP");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^UNIDADE\b", "UN");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }

        // O MV grava nome de paciente e setor em CAIXA ALTA. Uma coluna inteira em maiúsculas
        // é lenta de ler (perde-se o contorno das palavras), então mostramos capitalizado.
        // Siglas curtas — UTI, CTI, PS — continuam maiúsculas.
        private static string NomeProprio(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string[] miudas = { "de", "da", "do", "das", "dos", "e", "di", "du" };
            // Lista fechada de siglas. Tentei deduzir "é sigla se tem 3 letras maiúsculas" e
            // o resultado foi "ANA Exemplo" — nome de gente entra nessa regra.
            string[] siglas = { "UTI", "CTI", "PS", "UPA", "CC", "RN", "SUS", "AMB", "HD", "CME", "SR" };
            var sb = new System.Text.StringBuilder(s.Length);
            bool primeira = true;
            foreach (string bruto in s.Trim().Split(' '))
            {
                string p = bruto.Trim();
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                string cima = p.ToUpperInvariant();
                if (Array.IndexOf(siglas, cima) >= 0) { sb.Append(cima); primeira = false; continue; }
                // Sem letra (o "-" de "UTI - LADO A", números): passa direto.
                if (!System.Text.RegularExpressions.Regex.IsMatch(p, "[A-Za-zÀ-ú]")) { sb.Append(p); continue; }
                string b = p.ToLowerInvariant();
                if (!primeira && Array.IndexOf(miudas, b) >= 0) { sb.Append(b); continue; }
                sb.Append(char.ToUpperInvariant(b[0])).Append(b.Substring(1));
                primeira = false;
            }
            return sb.ToString();
        }

        // "ENDOVENOSA" ocuparia mais espaço que a dose inteira; a farmácia lê a sigla.
        // O que não estiver na lista aparece abreviado, nunca cortado no meio.
        // O MV escreve a mesma coisa de cinco jeitos: "08|08H - 08|08H", "6HS/6HS",
        // "24HS/24HS", "12/12 ENF 10-22H", "- - 8HRS MANHA". Na coluna isso virava um
        // paredão de texto que não cabia e cortava justo no "×/dia", que é o que importa.
        // Aqui vira sempre a mesma forma curta: "8/8h · 3×". O texto original fica no tooltip.
        // Uma forma só para todas: intervalo + vezes ao dia ("8/8h 3x").
        //
        // O texto que vem do MV varia demais para ir cru na tela — "- - 8HRS MANHA",
        // "08|08H - 08|08H", "12/12 ENF - 10-22H", "3 X AO DIA" são todos a mesma
        // informação escrita de jeitos diferentes. O turno (manhã/tarde/noite) não
        // acrescenta nada para quem está calculando consumo, e o original continua no
        // tooltip da célula para quem precisar conferir.
        //
        // O intervalo sai de quantas vezes ao dia, não do texto: os dois sempre bateram
        // nos casos reais (6HS/6HS = 4×, 08|08H = 3×, 12|12H = 2×) e assim "3 X AO DIA"
        // também ganha o intervalo, em vez de virar um formato à parte.
        private static string Frequencia(ItemAntimicrobiano it)
        {
            if (it.NaoRecorrente) return "dose única";

            int v = it.AplicacoesPorDia;
            if (v <= 0) return "?";

            string marca = it.FrequenciaEstimada ? "*" : "";   // contagem veio de palpite
            // Sem o "3x" ao lado do intervalo (pedido do farmacêutico, 29/08/2026): quem
            // lê "8/8h" já sabe que são 3 vezes ao dia. O "Nx/dia" continua para as
            // frequências sem intervalo redondo — ali ele É a informação, não redundância.
            return 24 % v == 0
                ? (24 / v) + "/" + (24 / v) + "h" + marca
                : v + "x/dia" + marca;
        }

        private static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(d.Length);
            foreach (char c in d)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().ToUpperInvariant().Trim();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}

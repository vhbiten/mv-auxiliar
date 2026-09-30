using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Uma linha do pré-pedido: o produto do relatório + a quantidade escolhida.
    internal class LinhaPedido
    {
        public MovProduto Prod;
        public int Pedido;         // 0 = fora do pedido
        public int Sugestao;       // valor inicial calculado (referência p/ marcar ajuste manual)
        public decimal EstoqueCaf; // saldo atual do produto na CAF (relatório do estoque 2)
        public bool TemCaf;        // false = produto sem registro no relatório da CAF ("—")
    }

    // Módulo "Pedido CAF": monta o PRÉ-PEDIDO de reposição da Farmácia Central junto à CAF.
    // 1) Gera o R_MOVI_ESTOQ (READ-ONLY) do período escolhido → consumo + estoque atual por
    //    produto; 2) sugere a quantidade (consumo arredondado p/ número de caixa fechada);
    // 3) o farmacêutico ajusta livremente na grade (vendo SAIU / ESTOQUE / SUGESTÃO lado a
    //    lado); 4) "Enviar pedido à CAF" GRAVA a solicitação no MV (pedido entre estoques,
    //    validado ao vivo no pedido nº 459588 em 17/08/2026) — pede confirmação, e depois de
    //    enviar não reabilita: reenviar criaria uma SEGUNDA solicitação.
    public class PedidoCafView : UserControl, IComandosDeTela
    {
        // Direção do pedido: a CAF ATENDE (fornece) e a Farmácia Central SOLICITA (recebe).
        // Trocar estes dois números grava o pedido ao contrário — e o MV não reclama.
        private const int EstoqueCaf = 2;
        private const int EstoqueFarmaciaCentral = 5;

        // Larguras BASE (96 DPI) das colunas dos botões — passam por Theme.S() no uso.
        // Orçamento da barra p/ janela mínima (base 96 DPI): padding do cartão 40 +
        // 156+112+144+100+152 = 664 fixos ≤ ~672 úteis — o "Enviar pedido à CAF" nunca é
        // cortado, "Medicamentos" cabe sem reticências e a moldura do cartão fecha inteira.
        // PERÍODO precisa de 112: em 96 DPI (monitor da farmácia) "3 dias" saía "3 di…"
        // com 96 — a diferença veio do tipo e do Abrir PDF, que tinham sobra.
        private const int LarguraGerar = 144;
        private const int LarguraEnviar = 152;

        // Tipos de pedido (valor guardado no combo e no snapshot da sessão).
        private const string TipoMedicamentos = "1";
        private const string TipoSoros = "S";
        private const string TipoMateriais = "2";
        private const string TipoHemodialise = "H";

        private readonly MainWindow _host;
        private StyledComboBox cmbEspecie;
        private StyledComboBox cmbPeriodo;
        private PrimaryButton btnGerar;
        private PrimaryButton btnPdf;
        private PrimaryButton btnEnviar;
        private PrimaryButton btnComprovante;
        private PrimaryButton btnNovo;
        private TableLayoutPanel _barraAcoes;
        private TextBox txtBusca;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;

        private bool _enviando;        // trava anti-duplo-clique do envio
        private bool _travado;         // já enviou (ou falhou de forma ambígua): não envia mais

        // Trava GLOBAL: o shell não deixa trocar de módulo/sair durante a gravação.
        private static bool _envioEmAndamento;
        public static bool EnvioEmAndamento { get { return _envioEmAndamento; } }

        // Conversa longa com o MV em curso (gerar pré-pedido = relatório + ~100 consultas de
        // saldo, ou o envio). O monitor de solicitações consulta isto para não disputar o
        // servidor de forms, que não lida bem com operações simultâneas na mesma sessão.
        private static bool _ocupadoNoMv;
        public static bool OcupadoNoMv { get { return _ocupadoNoMv || _envioEmAndamento; } }

        private readonly List<LinhaPedido> _linhas = new List<LinhaPedido>();
        private readonly List<LinhaPedido> _visiveis = new List<LinhaPedido>();   // após a busca (linha da grade -> modelo)
        private string _reportUrl;
        private string _reportUrl2;   // 2º relatório (hemodiálise usa as duas espécies)
        private DateTime _de, _ate;
        private int _dias = 3;      // período usado na geração (recalcular sugestão ao mudar a caixa)
        private bool _busy;
        private bool _renderizando;                    // suprime handlers da grade durante o rebuild
        private bool _movendoCelula;                   // evita reentrância ao desviar p/ a coluna "Pedido"
        private System.Windows.Forms.Timer _esperaBusca;   // debounce da busca (parado durante a geração)
        private int _linhaCorrente = -1;               // linha selecionada (repinta ao mudar)
        private int _codigoDuplicado;                  // produto que o usuário tentou incluir 2x
        private bool _incluindo;                       // inclusão manual em curso (consulta ao MV)

        // Escala de fundo da grade, do mais claro ao mais escuro — cada degrau tem que ser
        // visível ao lado do anterior. O erro antigo era a seleção (238,245,250) cair MAIS CLARA
        // que o fundo "no pedido" (Theme.InfoBg = 231,241,248): linha selecionada e linha com
        // item pedido ficavam iguais.
        //   linha branca .............. #FFFFFF
        //   linha branca + hover ...... HoverBranco
        //   linha "no pedido" ......... Theme.InfoBg
        //   linha "no pedido" + hover . HoverAzul
        //   linha SELECIONADA ......... SelecaoBg  (a mais escura, igual para os dois casos)
        private static readonly Color HoverBranco = Color.FromArgb(245, 249, 252);
        private static readonly Color HoverAzul = Color.FromArgb(211, 230, 244);
        private static readonly Color SelecaoBg = Color.FromArgb(183, 214, 237);
        // Realce de "esse produto já está no pedido" (tentativa de inclusão repetida).
        private static readonly Color DuplicadoBg = Color.FromArgb(250, 217, 213);
        private static readonly Color DuplicadoFg = Color.FromArgb(146, 43, 33);
        private const string AvisoDuplicado = "O item já está no pedido";

        // Snapshot em memória: o pré-pedido (com os ajustes) sobrevive à troca de módulo
        // na mesma sessão — gerar de novo custa ~10s e perderia os ajustes do farmacêutico.
        private static List<LinhaPedido> _memLinhas;
        private static string _memUrl;
        private static DateTime _memQuando, _memDe, _memAte;
        private static string _memTipo = TipoMedicamentos;
        private static int _memPeriodo = 3;

        // O pedido enviado também sobrevive à troca de módulo: sem isto, voltar à aba recriaria
        // a view com o mesmo pré-pedido e o botão liberado — e o MV aceitaria um pedido gêmeo.
        // _memBloqueado cobre o caso em que NÃO veio número (gravação incerta): aí _memPedido é
        // 0 e não daria para distinguir de "nunca enviou" — era por onde escapava o pedido duplo.
        private static long _memPedido;
        private static string _memPedidoUrl;
        private static bool _memBloqueado;
        private static DateTime _memPedidoData;   // dia do envio: a trava vale para HOJE

        // A trava existe para não duplicar o pedido do DIA. Sem a data, quem deixa o app aberto a
        // semana toda veria o aviso todo dia e aprenderia a clicar "Gerar outro" no automático —
        // justamente o reflexo que faria o pedido gêmeo no dia em que o aviso é verdadeiro.
        private static bool BloqueioAtivo
        {
            get { return (_memPedido != 0 || _memBloqueado) && _memPedidoData.Date == DateTime.Today; }
        }

        // Último pedido enviado no dia. Ao contrário dos de cima, NÃO é limpo ao gerar outro
        // pré-pedido — serve de lembrete no diálogo de confirmação ("já saiu o nº X às HH:mm").
        private static long _memUltimoNumero;
        private static DateTime _memUltimoQuando;

        // Chamado no logout: apaga o pré-pedido (não vaza entre usuários) mas MANTÉM a trava do
        // pedido enviado/incerto — sair e entrar não pode ser um jeito de liberar o reenvio.
        public static void LimparMemoria()
        {
            _memLinhas = null; _memUrl = null;
        }

        public PedidoCafView(MainWindow host)
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
            // cortado embaixo noutro, porque a fonte cresce mais que a linha.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cartão de opções (1 linha)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // grade (o principal)

            root.Controls.Add(BuildOpcoesCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildResultCard(), 0, 2);

            this.Controls.Add(root);
            this.Load += new EventHandler(OnLoad);
        }

        // ---- Cartão de opções (tipo, período e ações) ----
        private Control BuildOpcoesCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;                             // par da linha AutoSize do root
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(14), Theme.S(20), Theme.S(14));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 6;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(156))); // tipo de pedido
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112))); // período
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(LarguraGerar))); // Gerar pré-pedido
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(100))); // Abrir PDF
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(LarguraEnviar))); // Enviar à CAF
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // comprovante + folga
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tl.Controls.Add(MicroLabel("TIPO DE PEDIDO"), 0, 0);
            tl.Controls.Add(MicroLabel("PERÍODO"), 1, 0);

            // TIPO de pedido. No MV só existem duas espécies (1 = DROGAS E MEDICAMENTOS,
            // 2 = MATERIAIS); SOROS e HEMODIÁLISE são recortes feitos aqui, no cliente, e saem
            // dos outros tipos. Hemodiálise pega das DUAS espécies (soluções são espécie 1;
            // dialisador e linha de sangue são espécie 2).
            cmbEspecie = NovoCombo(new object[] {
                new OpcaoItem("Medicamentos", TipoMedicamentos),
                new OpcaoItem("Soros", TipoSoros),
                new OpcaoItem("Materiais", TipoMateriais),
                new OpcaoItem("Hemodiálise", TipoHemodialise) });
            Selecionar(cmbEspecie, TipoDoDia());
            tl.Controls.Add(cmbEspecie, 0, 1);

            cmbPeriodo = NovoCombo(new object[] {
                new OpcaoItem("2 dias", "2"), new OpcaoItem("3 dias", "3"),
                new OpcaoItem("4 dias", "4") });
            cmbPeriodo.SelectedIndex = 1;   // padrão: 3 dias
            tl.Controls.Add(cmbPeriodo, 1, 1);

            btnGerar = new PrimaryButton();
            btnGerar.Text = "Gerar pré-pedido";
            btnGerar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnGerar.Height = Theme.S(40);
            btnGerar.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnGerar.Click += new EventHandler(OnGerar);
            tl.Controls.Add(btnGerar, 2, 1);

            btnPdf = new PrimaryButton();
            btnPdf.Text = "Abrir PDF";   // o tooltip diz qual PDF (do relatório de consumo)
            btnPdf.Ghost = true;
            btnPdf.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnPdf.Height = Theme.S(40);
            btnPdf.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnPdf.Enabled = false;
            btnPdf.Click += new EventHandler(OnAbrirPdf);
            tl.Controls.Add(btnPdf, 3, 1);

            // ⚠️ ÚNICO botão deste módulo que GRAVA no MV (cria a solicitação).
            btnEnviar = new PrimaryButton();
            btnEnviar.Text = "Enviar pedido";
            btnEnviar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnEnviar.Height = Theme.S(40);
            btnEnviar.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnEnviar.Enabled = false;               // quem decide é AtualizarEnvio()
            btnEnviar.Click += new EventHandler(OnEnviar);
            tl.Controls.Add(btnEnviar, 4, 1);

            // Depois que o pedido sai, esta faixa é o que resta a fazer: ver o comprovante e
            // começar outro. Fica escondida até haver um pedido enviado.
            var posEnvio = new FlowLayoutPanel();
            posEnvio.Dock = DockStyle.Fill;
            posEnvio.FlowDirection = FlowDirection.LeftToRight;
            posEnvio.WrapContents = false;
            posEnvio.AutoSize = true;
            posEnvio.BackColor = Theme.CardBg;
            posEnvio.Margin = new Padding(0);
            posEnvio.Padding = new Padding(0);

            btnComprovante = new PrimaryButton();
            btnComprovante.Text = "Abrir comprovante";
            btnComprovante.Ghost = true;
            btnComprovante.Size = new Size(Theme.S(168), Theme.S(40));
            btnComprovante.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnComprovante.Visible = false;
            btnComprovante.Click += new EventHandler(OnAbrirComprovante);
            posEnvio.Controls.Add(btnComprovante);

            btnNovo = new PrimaryButton();
            btnNovo.Text = "Novo pedido";
            btnNovo.Size = new Size(Theme.S(150), Theme.S(40));
            btnNovo.Margin = new Padding(0);
            btnNovo.Visible = false;
            btnNovo.Click += new EventHandler(OnNovoPedido);
            posEnvio.Controls.Add(btnNovo);

            tl.Controls.Add(posEnvio, 5, 1);
            _barraAcoes = tl;   // as colunas de "Gerar"/"Enviar" encolhem no estado pós-envio

            var dica = new ToolTip();
            dica.SetToolTip(btnGerar, "Gera o relatório de movimentações no MV Soul e monta a sugestão de pedido (não grava nada)");
            dica.SetToolTip(btnPdf, "Abre o PDF do relatório de movimentações no navegador");
            dica.SetToolTip(btnEnviar, "Cria a solicitação no MV Soul: a Farmácia Central pede à CAF. Pede confirmação antes de gravar.");
            dica.SetToolTip(btnComprovante, "Reabre o PDF do comprovante do pedido enviado");
            dica.SetToolTip(btnNovo, "Limpa a tela para montar outro pedido (não mexe no pedido já enviado)");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });

            card.Controls.Add(tl);
            return card;
        }

        // seg/qua/sex = medicamentos; ter/qui = materiais; fim de semana = medicamentos.
        // Soros não entram no automático: é o farmacêutico que escolhe quando pedir.
        private static string TipoDoDia()
        {
            var d = DateTime.Today.DayOfWeek;
            return (d == DayOfWeek.Tuesday || d == DayOfWeek.Thursday) ? TipoMateriais : TipoMedicamentos;
        }

        // Espécie do MV que o relatório precisa para cada tipo (soro vive na dos medicamentos).
        // Hemodiálise é o caso especial: precisa das DUAS (ver GerarRelatoriosAsync).
        private static int EspecieDoTipo(string tipo)
        {
            return tipo == TipoMateriais ? 2 : 1;
        }

        private static string NomeDoTipo(string tipo)
        {
            if (tipo == TipoMateriais) return "materiais";
            if (tipo == TipoSoros) return "soros";
            if (tipo == TipoHemodialise) return "hemodiálise";
            return "medicamentos";
        }

        // Filtra o resultado do relatório conforme o tipo. Hemodiálise tem precedência: seus
        // produtos saem de TODOS os outros tipos, que é o motivo desse recorte existir.
        private static bool EntraNoTipo(MovProduto p, string tipo)
        {
            bool hemo = ProdutosHemodialise.EhHemodialise(p.Codigo);
            if (tipo == TipoHemodialise) return hemo;
            if (hemo) return false;
            if (tipo == TipoMateriais) return true;              // espécie 2 não tem soro
            bool soro = ProdutosSoro.EhSoro(p.Codigo, p.Nome);
            return tipo == TipoSoros ? soro : !soro;
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

        // ---- Cartão do pré-pedido (grade editável) ----
        private Control BuildResultCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            // Sem o título "Pré-pedido" (a pedido): sobraram a busca e o botão das caixas.
            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2; tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // busca
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // Caixas fechadas
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            tl.Controls.Add(BuildBusca(), 0, 0);

            var btnCaixas = new PrimaryButton();
            btnCaixas.Ghost = true;
            // "Editar" a pedido; o tooltip diz O QUE se edita (as caixas fechadas).
            btnCaixas.Text = "Editar";
            btnCaixas.Size = new Size(Theme.S(100), Theme.S(34));
            btnCaixas.Anchor = AnchorStyles.Right;
            btnCaixas.Margin = new Padding(0, 2, Theme.S(14), Theme.S(10));
            btnCaixas.Click += new EventHandler(OnCaixas);
            var dicaCaixas = new ToolTip();
            dicaCaixas.SetToolTip(btnCaixas, "Editar as quantidades de embalagem fechada (caixas) por produto");
            this.Disposed += new EventHandler(delegate { dicaCaixas.Dispose(); });
            tl.Controls.Add(btnCaixas, 1, 0);

            var host = new Panel();
            host.Dock = DockStyle.Fill; host.BackColor = Theme.CardBg;

            grid = NovaGrid(); grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill; lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body; lblVazio.ForeColor = Theme.TextSoft; lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Escolha o tipo e o período e clique em “Gerar pré-pedido”.";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);
            tl.Controls.Add(host, 0, 1); tl.SetColumnSpan(host, 2);

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBusca()
        {
            var hostBusca = new FieldHost();
            hostBusca.Anchor = AnchorStyles.Left;
            hostBusca.Width = Theme.S(340); hostBusca.Height = Theme.S(34);
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
            // _busy: um tick atrasado NÃO pode redesenhar a grade velha por cima do "Gerando..."
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
            // A escala de 5 fundos (acima) é pintura PRÓPRIA desta view: o GridPro entra pelo
            // resto (double buffer, cabeçalho claro, hover rastreado no MouseMove/scroll) e
            // só a pintura de hover dele é desligada — quem colore é o Grid_Format, lendo
            // g.LinhaHover.
            g.HoverPintado = false;
            // A ÚLTIMA linha fica vazia: é por ela que se acrescenta um produto que não veio no
            // relatório (quem não teve movimento no período não aparece).
            g.AllowUserToAddRows = true;
            g.AllowUserToResizeColumns = false;
            g.ReadOnly = false;   // só a coluna "Pedido" é editável (as demais são ReadOnly)
            // EditOnEnter: ao entrar na célula "Pedido" (clique ou seta) o cursor já pisca ali,
            // pronto para digitar — descendo a lista você percorre a coluna Pedido, não linhas mudas.
            g.EditMode = DataGridViewEditMode.EditOnEnter;
            g.AllowUserToOrderColumns = false;
            // sem zebra: linha branca é o estado "fora do pedido"; azul suave = "no pedido"

            // "codigo" não é ReadOnly por causa da linha de adicionar; CellBeginEdit impede
            // editar o código das linhas que já existem (lá ele é só leitura).
            // Larguras BASE (96 DPI) enxutas de propósito: fixas ~524px + produto (mín. 140) +
            // barra vertical cabem na janela mínima de 1000px — a coluna "Pedido", a única
            // editável, nunca fica atrás de scroll horizontal.
            g.Columns.Add(Col("codigo", "Código", 80, false, DataGridViewContentAlignment.MiddleCenter, false));
            var prod = Col("produto", "Produto", 200, true, DataGridViewContentAlignment.MiddleLeft, true);
            prod.MinimumWidth = Theme.S(140);
            g.Columns.Add(prod);
            g.Columns.Add(Col("unid", "Unidade", 80, false, DataGridViewContentAlignment.MiddleCenter, true));
            var ecaf = Col("estoquecaf", "Estoque CAF", 110, false, DataGridViewContentAlignment.MiddleCenter, true);
            ecaf.HeaderCell.ToolTipText = "Saldo atual do produto na CAF (quem vai atender o pedido)";
            g.Columns.Add(ecaf);
            g.Columns.Add(Col("saiu", "Saiu", 78, false, DataGridViewContentAlignment.MiddleCenter, true));
            var est = Col("estoque", "Estoque", 84, false, DataGridViewContentAlignment.MiddleCenter, true);
            est.HeaderCell.ToolTipText = "Saldo atual na Farmácia Central";
            g.Columns.Add(est);
            var ped = Col("pedido", "Pedido", 92, false, DataGridViewContentAlignment.MiddleCenter, false);
            ped.DefaultCellStyle.Font = Theme.BodyBold;
            // Padding ZERO: o campo de edição ocupa a célula inteira. Com a padding padrão
            // sobrava uma faixa que a grade pintava de branco por baixo do editor, rasgando
            // o realce da linha justamente nesta coluna — e, com o número centralizado,
            // qualquer padding assimétrica também entortaria o centro.
            ped.DefaultCellStyle.Padding = new Padding(0);
            g.Columns.Add(ped);

            g.CellFormatting += new DataGridViewCellFormattingEventHandler(Grid_Format);
            g.CellEndEdit += new DataGridViewCellEventHandler(Grid_EndEdit);
            g.EditingControlShowing += new DataGridViewEditingControlShowingEventHandler(Grid_Editing);
            g.KeyDown += new KeyEventHandler(Grid_KeyDown);
            g.CurrentCellChanged += new EventHandler(Grid_CelulaMudou);
            // Código só se digita na linha de adicionar; nas demais é dado do relatório.
            g.CellBeginEdit += new DataGridViewCellCancelEventHandler(delegate(object s, DataGridViewCellCancelEventArgs ce)
            {
                if (ce.RowIndex < 0 || ce.ColumnIndex < 0) return;
                if (grid.Columns[ce.ColumnIndex].Name != "codigo") return;
                if (!grid.Rows[ce.RowIndex].IsNewRow || _travado || _busy) ce.Cancel = true;
            });
            // O hover de linha inteira é do GridPro: ele acompanha o mouse (e o scroll),
            // solta ao sair da área das linhas e já repinta só as 2 linhas afetadas.
            return g;
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int w, bool fill,
            DataGridViewContentAlignment align, bool readOnly)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = Theme.S(w);
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            // Cabeçalho acompanha a célula: coluna centralizada com título à esquerda
            // continuava parecendo torta.
            if (align == DataGridViewContentAlignment.MiddleCenter)
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            c.ReadOnly = readOnly;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return c;
        }

        // Aceita só dígitos na edição da coluna "Pedido".
        private void Grid_Editing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var tb = e.Control as TextBox;
            if (tb == null) return;
            tb.KeyPress -= SoNumeros;   // não acumular handler a cada edição
            if (grid.CurrentCell != null)
            {
                string cn = grid.Columns[grid.CurrentCell.ColumnIndex].Name;
                if (cn == "pedido" || cn == "codigo") tb.KeyPress += SoNumeros;   // só dígitos
            }

            // A célula em edição é SEMPRE a da linha corrente, que está selecionada (FullRowSelect)
            // — então o fundo do campo é o da seleção. Sem isto o TextBox sai branco e abre um
            // buraco no realce da linha, bem na coluna "Pedido".
            tb.BackColor = SelecaoBg;
            tb.ForeColor = Theme.TextDark;
            tb.BorderStyle = BorderStyle.None;   // sem moldura própria: o campo some dentro da linha
            // O TextBox não ocupa a célula toda: o resto é o painel de edição do DataGridView,
            // que sai BRANCO e abre uma faixa clara à esquerda do número, cortando a linha.
            // O painel só existe depois deste evento — daí o BeginInvoke.
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (tb.IsDisposed || tb.Parent == null) return;
                tb.Parent.BackColor = SelecaoBg;
            });
        }

        private void SoNumeros(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        }

        // A única coluna editável é "Pedido": qualquer célula que vire a corrente (clique numa
        // outra coluna, Tab, ou a grade ganhando foco) desvia para o "Pedido" da mesma linha.
        // Com EditMode=EditOnEnter, isso já abre o cursor — descer com a seta mantém a digitação.
        private void Grid_CelulaMudou(object sender, EventArgs e)
        {
            if (_renderizando) return;
            var c = grid.CurrentCell;

            // A LINHA inteira muda de cor com a seleção: repinta a que saiu e a que entrou
            // (o DataGridView sozinho só invalida as células, não a linha).
            int atual = c == null ? -1 : c.RowIndex;
            if (atual != _linhaCorrente)
            {
                int antiga = _linhaCorrente;
                _linhaCorrente = atual;
                InvalidateLinha(antiga); InvalidateLinha(atual);
            }

            if (_movendoCelula || _travado || _busy) return;
            // Só desvia (e abre o cursor) se o foco está NA grade: senão, um rebuild disparado
            // pela busca roubaria o foco de quem está digitando no campo de pesquisa.
            if (!grid.ContainsFocus) return;
            if (c == null || c.ColumnIndex < 0) return;
            int r = c.RowIndex;
            if (r < 0 || r >= grid.Rows.Count) return;

            // Na linha de adicionar o campo útil é o CÓDIGO; nas demais, a quantidade.
            string alvo = grid.Rows[r].IsNewRow ? "codigo" : "pedido";
            if (grid.Columns[c.ColumnIndex].Name == alvo) return;
            if (!grid.Rows[r].IsNewRow && r >= _visiveis.Count) return;

            _movendoCelula = true;
            try { grid.CurrentCell = grid.Rows[r].Cells[alvo]; }
            catch { }
            finally { _movendoCelula = false; }
        }

        // Delete/Backspace numa célula "Pedido" zera a linha (tira do pedido) sem abrir a edição.
        private void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (_busy || _travado) return;   // travada durante a geração e depois de enviar
            if (e.KeyCode != Keys.Delete && e.KeyCode != Keys.Back) return;
            if (grid.CurrentCell == null || grid.IsCurrentCellInEditMode) return;
            if (grid.Columns[grid.CurrentCell.ColumnIndex].Name != "pedido") return;
            int r = grid.CurrentCell.RowIndex;
            if (r < 0 || r >= _visiveis.Count) return;
            _visiveis[r].Pedido = 0;
            grid.Rows[r].Cells["pedido"].Value = "0";
            grid.InvalidateRow(r);
            AtualizarResumo();
            e.Handled = true;
        }

        // Fim da edição da célula "Pedido": grava no modelo.
        //   vazio     -> 0 (apagar a célula é o gesto de tirar o produto do pedido)
        //   inválido  -> volta o valor anterior
        private void Grid_EndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_renderizando) return;   // disparo sintético durante o rebuild da grade
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;

            // Código digitado na linha vazia do fim = incluir produto no pedido.
            if (grid.Columns[e.ColumnIndex].Name == "codigo")
            { IncluirProduto(grid.Rows[e.RowIndex]); return; }

            if (e.RowIndex >= _visiveis.Count) return;
            if (grid.Columns[e.ColumnIndex].Name != "pedido") return;
            var linha = _visiveis[e.RowIndex];
            object v = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            string txt = v == null ? "" : v.ToString().Trim();
            int novo;
            if (txt.Length == 0) novo = 0;
            else if (!int.TryParse(txt, out novo) || novo < 0)
                novo = linha.Pedido;   // inválido: mantém o que estava
            linha.Pedido = novo;
            grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = novo.ToString();
            grid.InvalidateRow(e.RowIndex);
            AtualizarResumo();
        }

        // ---- Inclusão manual de produto (linha vazia do fim da grade) ----
        // O relatório de movimentação só traz quem teve giro no período; o que faltar entra por
        // aqui, com nome e saldos lidos ao vivo no MV (consumo 0 — não houve movimento).
        private async void IncluirProduto(DataGridViewRow row)
        {
            if (_incluindo || _busy || _travado) return;
            string texto = Convert.ToString(row.Cells["codigo"].Value);
            texto = texto == null ? "" : texto.Trim();
            int cod;
            if (!int.TryParse(texto, out cod) || cod <= 0)
            {
                LimparCelulaNova(row);
                if (_codigoDuplicado != 0) { _codigoDuplicado = 0; RenderGrid(); }
                return;
            }

            // Já está no pedido: não duplica. Marca a linha existente em vermelho (com tooltip),
            // leva o usuário até ela e devolve a linha de inclusão limpa.
            var existente = _linhas.Find(delegate(LinhaPedido x) { return x.Prod.Codigo == cod; });
            if (existente != null)
            {
                LimparCelulaNova(row);
                _codigoDuplicado = cod;
                RenderGrid();
                MostrarLinha(cod);
                banner.SetState(StatusKind.Warning, AvisoDuplicado + ": " + cod + " — " + existente.Prod.Nome +
                    ". Ajuste a quantidade na linha dele.");
                return;
            }

            if (_host.Session == null)
            { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); LimparCelulaNova(row); return; }

            _incluindo = true;
            _codigoDuplicado = 0;
            LimparCelulaNova(row);
            banner.SetState(StatusKind.Busy, "Consultando o produto " + cod + " no MV Soul...");
            bool tenho = false;
            try
            {
                tenho = await MvGate.EntrarAsync("pedido-caf-incluir", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                { banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente incluir de novo em instantes."); return; }

                var umItem = new List<int> { cod };
                var nomes = await NomesDeAsync(umItem);
                if (IsDisposed || Disposing) return;
                string nome;
                if (!nomes.TryGetValue(cod, out nome) || string.IsNullOrEmpty(nome))
                {
                    banner.SetState(StatusKind.Error, "O código " + cod + " não existe no estoque da Farmácia Central.");
                    return;
                }

                // saldos em tempo real: CAF (quem atende) e Farmácia Central (quem pede)
                decimal saldoCaf = 0; bool temCaf = false, temFc = false;
                decimal saldoFc = 0;
                try
                {
                    var c = await MorphisClient.ConsultarSaldosCafAsync(_host.Session, EstoqueCaf, EstoqueFarmaciaCentral, umItem);
                    temCaf = c.TryGetValue(cod, out saldoCaf);
                    var f = await MorphisClient.ConsultarSaldosCafAsync(_host.Session, EstoqueFarmaciaCentral, EstoqueCaf, umItem);
                    temFc = f.TryGetValue(cod, out saldoFc);
                }
                catch (SessaoExpiradaException) { throw; }
                catch (Exception ex) { Logger.Log("Pedido CAF: saldos do produto " + cod + " indisponíveis: " + ex.Message); }
                if (IsDisposed || Disposing) return;

                var l = new LinhaPedido();
                l.Prod = new MovProduto();
                l.Prod.Codigo = cod; l.Prod.Nome = nome; l.Prod.Unidade = "";
                l.Prod.Consumo = 0;                       // não teve movimento no período
                l.Prod.EstoqueAtual = temFc ? saldoFc : 0;
                l.EstoqueCaf = saldoCaf; l.TemCaf = temCaf;
                l.Sugestao = 0; l.Pedido = 0;             // quantidade é do usuário
                _linhas.Add(l);
                _linhas.Sort(delegate(LinhaPedido a, LinhaPedido b)
                { return string.Compare(a.Prod.Nome, b.Prod.Nome, StringComparison.OrdinalIgnoreCase); });

                // Se a busca esconderia o produto recém-incluído, limpa a busca. O RenderGrid vem
                // logo depois (e não pelo timer da busca): MostrarLinha precisa de _visiveis já
                // atualizado para achar a linha.
                if (txtBusca.Text.Trim().Length > 0 && !Corresponde(l, txtBusca.Text.Trim()))
                { _esperaBusca.Stop(); txtBusca.Text = ""; }
                RenderGrid();

                banner.SetState(StatusKind.Success, nome + " incluído. Digite a quantidade na coluna “Pedido”.");
                MostrarLinha(cod, true);
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error, "Não foi possível incluir o produto " + cod + ": " + ex.Message);
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _incluindo = false;
            }
        }

        // A linha de adicionar não pode ser removida (é a "linha-fantasma" do DataGridView):
        // só se limpa a célula.
        private void LimparCelulaNova(DataGridViewRow row)
        {
            try { if (row.Index >= 0) row.Cells["codigo"].Value = ""; }
            catch { }
        }

        // Rola até o produto e o seleciona (opcionalmente abrindo a edição da quantidade).
        private void MostrarLinha(int codigo, bool editarQuantidade = false)
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || Disposing) return;
                for (int i = 0; i < _visiveis.Count && i < grid.Rows.Count; i++)
                {
                    if (_visiveis[i].Prod.Codigo != codigo) continue;
                    try
                    {
                        grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
                        grid.CurrentCell = grid.Rows[i].Cells["pedido"];
                        if (editarQuantidade) grid.BeginEdit(true);
                    }
                    catch { }
                    return;
                }
            });
        }

        // Redesenha só uma linha (para o hover não repintar a grade inteira).
        private void InvalidateLinha(int r)
        {
            if (r >= 0 && r < grid.Rows.Count) grid.InvalidateRow(r);
        }

        // Estados visuais da linha:
        //   fora do pedido (Pedido=0): fundo BRANCO, texto CINZA (discreta)
        //   no pedido (Pedido>0):      fundo azul bem claro, texto normal
        //   hover: a LINHA inteira escurece um tom (suave), acompanhando o mouse
        //   seleção: azul nitidamente mais forte — tem que se distinguir de "no pedido"
        private void Grid_Format(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string coluna = grid.Columns[e.ColumnIndex].Name;

            // Linha de adicionar (a última, vazia): fica discreta e explica o que fazer.
            if (e.RowIndex >= _visiveis.Count)
            {
                e.CellStyle.BackColor = Color.White;
                e.CellStyle.SelectionBackColor = Color.White;
                e.CellStyle.ForeColor = Theme.TextSoft;
                e.CellStyle.SelectionForeColor = Theme.TextSoft;
                if (coluna == "produto" && (e.Value == null || Convert.ToString(e.Value).Length == 0))
                {
                    e.Value = "digite o código ao lado para incluir um produto";
                    e.FormattingApplied = true;
                }
                return;
            }

            var l = _visiveis[e.RowIndex];
            string col = coluna;
            bool noPedido = l.Pedido > 0;

            // Produto que o usuário tentou incluir e já estava no pedido: linha em vermelho
            // (a tooltip vai nas células, em RenderGrid) até ele digitar outro código.
            if (l.Prod.Codigo == _codigoDuplicado)
            {
                e.CellStyle.BackColor = DuplicadoBg;
                e.CellStyle.SelectionBackColor = DuplicadoBg;
                e.CellStyle.ForeColor = DuplicadoFg;
                e.CellStyle.SelectionForeColor = DuplicadoFg;
                return;
            }

            // A linha corrente vai com SelecaoBg TAMBÉM no BackColor (não só no SelectionBackColor):
            // o DataGridView monta o campo de edição a partir do BackColor da célula, então sem
            // isto o "Pedido" da linha selecionada aparece claro, furando o realce da linha.
            bool selecionada = grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex;
            e.CellStyle.BackColor = selecionada ? SelecaoBg : FundoLinha(e.RowIndex, noPedido);
            e.CellStyle.SelectionBackColor = SelecaoBg;   // igual nos dois casos: seleção é seleção

            bool cinza = false;
            if (!noPedido)
            {
                e.CellStyle.ForeColor = Theme.TextSoft;                  // linha inteira cinza
                cinza = true;
                if (col == "pedido") e.CellStyle.Font = Theme.Body;      // "0" sem negrito
            }
            else
            {
                if (col == "saiu" && l.Prod.Consumo <= 0) { e.CellStyle.ForeColor = Theme.TextSoft; cinza = true; }
                if (col == "pedido" && l.Pedido != l.Sugestao) e.CellStyle.ForeColor = Theme.Primary; // ajustado à mão
            }
            // Cinza claro sobre o azul forte da seleção fica ilegível: só ali o texto escurece.
            e.CellStyle.SelectionForeColor = cinza ? Theme.TextDark : e.CellStyle.ForeColor;
        }

        // Fundo da linha fora da seleção (a seleção é pintada pelo SelectionBackColor).
        // Fonte única de verdade: o campo de edição usa a MESMA conta, senão fica um retângulo
        // branco por cima da linha realçada, cortando o hover justamente na célula "Pedido".
        private Color FundoLinha(int rowIndex, bool noPedido)
        {
            // Quem sabe a linha sob o mouse é o GridPro (rastreada no MouseMove e no scroll).
            if (rowIndex == grid.LinhaHover) return noPedido ? HoverAzul : HoverBranco;
            return noPedido ? Theme.InfoBg : Color.White;
        }

        // ---- Fluxo ----
        private void OnLoad(object sender, EventArgs e)
        {
            if (_host.Session == null)
            { banner.SetState(StatusKind.Warning, "Faça login para montar o pré-pedido."); return; }

            // restaura o pré-pedido do dia (com os ajustes), se houver
            if (_memLinhas != null && _memLinhas.Count > 0 && _memQuando.Date == DateTime.Today)
            {
                _linhas.Clear(); _linhas.AddRange(_memLinhas);
                _reportUrl = _memUrl; _de = _memDe; _ate = _memAte; _dias = _memPeriodo;
                Selecionar(cmbEspecie, _memTipo);
                Selecionar(cmbPeriodo, _memPeriodo.ToString());
                btnPdf.Enabled = _reportUrl != null;

                // Este pré-pedido já foi enviado (ou ficou incerto)? Continua travado nesta view
                // nova — senão bastaria trocar de módulo e voltar para enviar de novo. O
                // comprovante aparece sempre que existir URL, mesmo sem número.
                if (BloqueioAtivo)
                {
                    _travado = true;
                    AplicarEstadoPosEnvio(true);
                }
                RenderGrid();
                if (BloqueioAtivo && _memPedido != 0)
                {
                    banner.SetState(StatusKind.Success, "Este pré-pedido já foi enviado (pedido nº " + _memPedido +
                        "). Para montar outro, clique em “Novo pedido”.");
                    return;
                }
                if (BloqueioAtivo)
                {
                    banner.SetState(StatusKind.Error, "A tentativa anterior de envio NÃO foi confirmada pelo MV — " +
                        "o pedido PODE ter sido criado. Confira em Solicitações (estoque CAF) ANTES de enviar outro.");
                    return;
                }
                banner.SetState(StatusKind.Info, "Pré-pedido de " + _memQuando.ToString("HH:mm") +
                    " restaurado (consumo de " + _de.ToString("dd/MM") + " a " + _ate.ToString("dd/MM") + "). Ajuste e confira.");
            }
        }

        private void Selecionar(StyledComboBox cmb, string valor)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                var oi = cmb.Items[i] as OpcaoItem;
                if (oi != null && oi.Valor == valor) { cmb.SelectedIndex = i; return; }
            }
        }

        private async void OnGerar(object sender, EventArgs e)
        {
            if (_host.Session == null)
            { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }
            if (_busy) return;

            // Gerar um pré-pedido novo LIBERA o envio outra vez. Se já saiu um pedido hoje (ou
            // ficou incerto), isso tem que ser uma decisão consciente: o consumo do MV não muda
            // enquanto a CAF não atende, então o novo pré-pedido sai praticamente idêntico.
            if (BloqueioAtivo)
            {
                string aviso = _memPedido > 0
                    ? "O pedido nº " + _memPedido + " já foi enviado à CAF."
                    : "A última tentativa de envio não foi confirmada pelo MV (o pedido pode existir).";
                if (!StyledDialog.Confirmar(this.FindForm(), "Gerar outro pré-pedido?",
                        aviso + "\n\nGerar um pré-pedido novo libera o botão de enviar — e um segundo " +
                        "envio cria OUTRA solicitação no MV. Confira em Solicitações (estoque CAF) antes.\n\n" +
                        "Gerar mesmo assim?", "Gerar outro", "Cancelar"))
                    return;
            }
            _busy = true; _ocupadoNoMv = true;   // conversa longa: o monitor de solicitações espera

            string tipo = (cmbEspecie.SelectedItem as OpcaoItem).Valor;
            int especie = EspecieDoTipo(tipo);
            int dias = int.Parse((cmbPeriodo.SelectedItem as OpcaoItem).Valor);
            DateTime de = DateTime.Today.AddDays(-dias);

            TravarControles("Gerando...");
            grid.Visible = false; lblVazio.Visible = true;
            lblVazio.Text = "Gerando o relatório de movimentações no MV Soul...";
            banner.SetState(StatusKind.Busy, "Gerando o relatório de movimentações (" +
                NomeDoTipo(tipo) + ", desde " + de.ToString("dd/MM") + ")...");

            bool tenho = false;
            try
            {
                // Portão único do MV (o monitor de solicitações varre em segundo plano).
                tenho = await MvGate.EntrarAsync("pedido-caf-gerar", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes.");
                    lblVazio.Text = "Servidor ocupado. Clique em “Gerar pré-pedido”.";
                    return;
                }

                // Hemodiálise vem das DUAS espécies (soluções são medicamento; dialisador e
                // linha de sangue são material); os outros tipos, de uma só.
                int[] especies = (tipo == TipoHemodialise) ? new[] { 1, 2 } : new[] { especie };
                var urls = new List<string>();
                var produtos = new List<MovProduto>();
                var vistos = new HashSet<int>();
                int totalRelatorio = 0;
                for (int i = 0; i < especies.Length; i++)
                {
                    if (especies.Length > 1)
                        banner.SetState(StatusKind.Busy, "Gerando o relatório de movimentações (" +
                            NomeDoTipo(tipo) + ", parte " + (i + 1) + " de " + especies.Length + ")...");
                    var r = await MorphisClient.GerarMovimentacaoAsync(_host.Session, 5, especies[i], de);
                    if (IsDisposed || Disposing) return;
                    if (!string.IsNullOrEmpty(r.ReportUrl)) urls.Add(r.ReportUrl);
                    totalRelatorio += r.Produtos.Count;
                    // O filtro vem ANTES da consulta de saldo — no pedido de soros/hemodiálise
                    // são poucos produtos em vez de 200, e o saldo é uma ida ao servidor por item.
                    foreach (var p in r.Produtos)
                        if (EntraNoTipo(p, tipo) && vistos.Add(p.Codigo)) produtos.Add(p);
                }

                // Hemodiálise é lista fechada: item sem movimento no período NÃO pode sumir da
                // tela (o relatório só traz quem movimentou). Entra com consumo 0 para o
                // farmacêutico decidir na mão — some da lista seria pedir de menos.
                if (tipo == TipoHemodialise)
                {
                    var faltantes = new List<int>();
                    foreach (int cod in ProdutosHemodialise.Codigos()) if (!vistos.Contains(cod)) faltantes.Add(cod);
                    if (faltantes.Count > 0)
                    {
                        var nomes = await NomesDeAsync(faltantes);
                        if (IsDisposed || Disposing) return;
                        // O saldo da Farmácia Central destes itens NÃO pode ser assumido zero:
                        // o relatório de movimentações só fala de quem MOVIMENTOU, e sem giro no
                        // período a coluna "Estoque" mostrava 0 com o produto na prateleira —
                        // medido em 29/08/2026 contra a posição real do estoque 5: os cinco itens
                        // de hemodiálise tinham 9, 10, 11, 11 e 23. Vem do mesmo caminho em tempo
                        // real da inclusão manual, com os papéis invertidos (atendente = Central);
                        // conferido item a item com o R_POS_EST_S: 5 de 5.
                        Dictionary<int, decimal> saldoFc = null;
                        banner.SetState(StatusKind.Busy, "Consultando o saldo da Farmácia Central ("
                            + faltantes.Count + " itens de hemodiálise sem movimento no período)...");
                        try
                        {
                            saldoFc = await MorphisClient.ConsultarSaldosCafAsync(
                                _host.Session, EstoqueFarmaciaCentral, EstoqueCaf, faltantes);
                        }
                        catch (SessaoExpiradaException) { throw; }
                        catch (Exception ex)
                        { Logger.Log("Pedido CAF: saldo da Farmácia Central indisponível p/ hemodiálise: " + ex.Message); }
                        if (IsDisposed || Disposing) return;
                        foreach (int cod in faltantes)
                        {
                            var p = new MovProduto();
                            p.Codigo = cod;
                            string nm; p.Nome = nomes.TryGetValue(cod, out nm) && !string.IsNullOrEmpty(nm) ? nm : "(sem movimento no período)";
                            p.Unidade = ""; p.Consumo = 0;
                            decimal sf;
                            p.EstoqueAtual = (saldoFc != null && saldoFc.TryGetValue(cod, out sf)) ? sf : 0;
                            produtos.Add(p); vistos.Add(cod);
                        }
                        Logger.Log("Pedido CAF: hemodiálise — " + faltantes.Count + " item(ns) sem movimento incluído(s)"
                            + (saldoFc != null ? " com o saldo real da Farmácia Central." : " (saldo da Central indisponível)."));
                    }
                }

                Logger.Log("Pedido CAF: tipo " + NomeDoTipo(tipo) + " — " + produtos.Count +
                    " de " + totalRelatorio + " produtos do(s) relatório(s).");

                // Saldo atual na CAF: consultado EM TEMPO REAL pelo mesmo caminho do MV —
                // entrar o código no pedido entre estoques (M_SOLSAIPRO) e ler QT_ESTOQUE_ATUAL.
                // READ-ONLY: nada é gravado (nunca finaliza a solicitação). Ao contrário do
                // relatório de movimentações, traz o saldo de QUALQUER produto, inclusive os
                // SEM giro no período — que o relatório omitia, zerando a coluna indevidamente.
                // Falha aqui não derruba o pré-pedido (a coluna fica sem valor).
                Dictionary<int, decimal> caf = null;
                try
                {
                    var codigos = new List<int>();
                    foreach (var p in produtos) codigos.Add(p.Codigo);
                    banner.SetState(StatusKind.Busy, "Consultando o saldo atual da CAF (" +
                        codigos.Count + " produtos, em tempo real)...");
                    caf = await MorphisClient.ConsultarSaldosCafAsync(_host.Session, 2, 5, codigos);
                    if (IsDisposed || Disposing) return;
                }
                catch (SessaoExpiradaException) { throw; }
                catch (Exception ex) { Logger.Log("Pedido CAF: saldo da CAF indisponível: " + ex.Message); }

                _dias = dias;

                // Pré-pedido NOVO (dados novos no lugar dos antigos): libera o envio outra vez.
                // Só aqui — se a geração tivesse falhado, a trava do pedido anterior continuaria.
                // O usuário já confirmou isso no diálogo do começo do método.
                _travado = false; _memPedido = 0; _memPedidoUrl = null; _memBloqueado = false;
                btnComprovante.Visible = false;

                _linhas.Clear();
                foreach (var p in produtos)
                {
                    var l = new LinhaPedido();
                    l.Prod = p;
                    l.Sugestao = PedidoSugestao.Sugerir(p.Consumo, p.EstoqueAtual, dias, PedidoEmbalagens.Obter(p.Codigo));
                    l.Pedido = l.Sugestao;
                    decimal ec;
                    if (caf != null && caf.TryGetValue(p.Codigo, out ec)) { l.EstoqueCaf = ec; l.TemCaf = true; }
                    _linhas.Add(l);
                }
                _linhas.Sort(delegate(LinhaPedido a, LinhaPedido b)
                { return string.Compare(a.Prod.Nome, b.Prod.Nome, StringComparison.OrdinalIgnoreCase); });

                _reportUrl = urls.Count > 0 ? urls[0] : null;
                _reportUrl2 = urls.Count > 1 ? urls[1] : null;   // hemodiálise: 2 relatórios
                _de = de; _ate = DateTime.Today;
                btnPdf.Enabled = true;
                SalvarMemoria(tipo, dias);
                RenderGrid();

                if (_linhas.Count == 0)
                    banner.SetState(StatusKind.Warning, "Nenhum produto de " + NomeDoTipo(tipo) +
                        " com movimento no período. Confira o tipo e o período.");
                else
                    banner.SetState(StatusKind.Success, "Atualizado às " + DateTime.Now.ToString("HH:mm:ss")
                        + " (consumo de " + de.ToString("dd/MM") + " a " + DateTime.Today.ToString("dd/MM") + ").");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Erro ao gerar o pré-pedido: " + ex.Message);
                lblVazio.Text = "Falha ao gerar. Tente novamente.";
            }
            finally
            {
                if (tenho) MvGate.Sair();
                // _busy PRIMEIRO: DestravarControles reavalia o botão de enviar com base nele.
                // Na ordem inversa o envio ficava desabilitado para sempre depois de gerar.
                _busy = false; _ocupadoNoMv = false;
                if (!IsDisposed && !Disposing) DestravarControles();
            }
        }

        private void OnAbrirPdf(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_reportUrl)) return;
            try
            {
                Navegador.Abrir(_reportUrl);
                // Hemodiálise gera dois relatórios (uma espécie em cada): abre as duas abas.
                if (!string.IsNullOrEmpty(_reportUrl2)) Navegador.Abrir(_reportUrl2);
            }
            catch (Exception ex) { banner.SetState(StatusKind.Error, "Não foi possível abrir o PDF: " + ex.Message); }
        }

        // Nome dos produtos que não vieram no relatório: cache local primeiro, servidor só para
        // o que faltar (mesmo caminho que o cadastro de embalagens usa).
        private async System.Threading.Tasks.Task<Dictionary<int, string>> NomesDeAsync(List<int> codigos)
        {
            var nomes = new Dictionary<int, string>();
            var faltam = new List<int>();
            foreach (int c in codigos)
            {
                string n = ProdutoNomes.Get(c);
                if (!string.IsNullOrEmpty(n)) nomes[c] = n; else faltam.Add(c);
            }
            if (faltam.Count == 0) return nomes;
            try
            {
                var achados = await MorphisClient.ResolverNomesAsync(_host.Session, EstoqueFarmaciaCentral, faltam, null);
                foreach (var kv in achados) nomes[kv.Key] = kv.Value;
            }
            catch (SessaoExpiradaException) { throw; }
            catch (Exception ex) { Logger.Log("Pedido CAF: nomes indisponíveis: " + ex.Message); }
            return nomes;
        }

        private void OnAbrirComprovante(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_memPedidoUrl)) return;
            try { Navegador.Abrir(_memPedidoUrl); }
            catch (Exception ex)
            { banner.SetState(StatusKind.Warning, "Não foi possível abrir o comprovante. URL: " + _memPedidoUrl + " (" + ex.Message + ")"); }
        }

        // Alterna a barra entre "montando o pedido" e "pedido enviado". No estado pós-envio,
        // "Gerar pré-pedido" e "Enviar" saem de cena (o caminho é o "Novo pedido") e as colunas
        // deles encolhem: são 7 botões para uma faixa que, na janela padrão de 1180px, não
        // comporta todos — sem isso o "Novo pedido" ficava cortado fora da tela maximizada.
        private void AplicarEstadoPosEnvio(bool posEnvio)
        {
            if (_barraAcoes == null) return;
            btnGerar.Visible = !posEnvio;
            btnEnviar.Visible = !posEnvio;
            _barraAcoes.ColumnStyles[2].Width = posEnvio ? 0 : Theme.S(LarguraGerar);
            _barraAcoes.ColumnStyles[4].Width = posEnvio ? 0 : Theme.S(LarguraEnviar);
            btnNovo.Visible = posEnvio;
            btnComprovante.Visible = posEnvio && !string.IsNullOrEmpty(_memPedidoUrl);
        }

        // "Novo pedido": zera a tela para montar outro. É a declaração explícita de "terminei
        // com aquele" — por isso é aqui (e só aqui) que a trava anti-duplicidade cai.
        private void OnNovoPedido(object sender, EventArgs e)
        {
            if (_busy || _enviando) return;

            // Envio que não voltou confirmado: o pedido PODE existir. Antes de liberar a tela,
            // o farmacêutico tem que ter conferido — senão o "novo" vira o gêmeo do anterior.
            if (_memPedido == 0 && _memBloqueado)
            {
                if (!StyledDialog.Confirmar(this.FindForm(), "Começar outro pedido?",
                        "O envio anterior NÃO foi confirmado pelo MV — o pedido pode ter sido criado.\n\n" +
                        "Confira em Solicitações (estoque CAF) antes de montar outro, senão a CAF pode " +
                        "receber o mesmo pedido duas vezes.\n\nJá conferi, começar outro pedido?",
                        "Começar outro", "Cancelar"))
                    return;
            }

            LimparParaNovoPedido();
        }

        private void LimparParaNovoPedido()
        {
            // Solta a trava e apaga o pré-pedido enviado (inclusive o que sobrevive à troca de
            // aba). _memUltimoNumero FICA: o diálogo de confirmação do próximo envio ainda
            // lembra que já saiu um pedido hoje.
            _travado = false;
            _memPedido = 0; _memPedidoUrl = null; _memBloqueado = false;
            _memLinhas = null; _memUrl = null;

            _linhas.Clear(); _visiveis.Clear();
            _reportUrl = null; _reportUrl2 = null;
            btnPdf.Enabled = false;
            AplicarEstadoPosEnvio(false);   // volta a barra normal (Gerar / Enviar)
            if (txtBusca != null) txtBusca.Text = "";
            TravarColunaPedido(false);
            RenderGrid();   // grade vazia + estado inicial ("Escolha o tipo e o período...")
            banner.SetState(StatusKind.Info,
                "Tela limpa. Escolha o tipo e o período e clique em “Gerar pré-pedido”.");
        }

        // ---- Envio à CAF: AQUI o app GRAVA no MV (cria a solicitação) ----
        // Confirmação explícita antes; trava anti-duplo-clique; e o botão NÃO volta depois —
        // nem no sucesso, nem em falha ambígua (a gravação pode ter passado e um segundo clique
        // criaria um pedido gêmeo; o form do MV não tem chave de idempotência).
        private async void OnEnviar(object sender, EventArgs e)
        {
            if (_busy || _enviando || _travado) return;
            if (_host.Session == null)
            { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            if (grid.IsCurrentCellInEditMode) grid.EndEdit();   // o número em digitação entra no modelo
            btnEnviar.Enabled = false;                          // depois do EndEdit (que reavalia o botão)

            // _linhas (TODAS), nunca _visiveis: a busca esconde linhas que continuam no pedido.
            var itens = new List<PedidoItem>();
            long unidades = 0;
            int semSaldo = 0;
            foreach (var l in _linhas)
            {
                if (l.Pedido <= 0) continue;
                var pi = new PedidoItem();
                pi.Codigo = l.Prod.Codigo; pi.Quantidade = l.Pedido;
                itens.Add(pi); unidades += l.Pedido;
                if (l.TemCaf && l.EstoqueCaf < l.Pedido) semSaldo++;
            }
            if (itens.Count == 0)
            { banner.SetState(StatusKind.Warning, "Nenhum produto com quantidade na coluna “Pedido”."); AtualizarResumo(); return; }

            string aviso = semSaldo > 0
                ? "\n\nAtenção: " + semSaldo + (semSaldo == 1 ? " produto pede" : " produtos pedem") +
                  " mais do que a CAF tem em estoque."
                : "";
            // Criar a solicitação não mexe no estoque da Farmácia Central, então um pré-pedido
            // gerado de novo sai quase idêntico: lembrar o que já saiu HOJE evita o pedido gêmeo.
            if (_memUltimoNumero > 0 && _memUltimoQuando.Date == DateTime.Today)
                aviso += "\n\nJá foi enviado HOJE o pedido nº " + _memUltimoNumero + " às " +
                    _memUltimoQuando.ToString("HH:mm") + ". Confirme que este é um pedido DIFERENTE.";

            // Modal do próprio app (StyledDialog é Form.ShowDialog: bloqueia a janela inteira,
            // então a sidebar não pode descartar esta view no meio da confirmação).
            if (!StyledDialog.Confirmar(this.FindForm(), "Enviar pedido para a CAF?",
                    itens.Count + (itens.Count == 1 ? " produto" : " produtos") + " · " +
                    unidades.ToString("#,0") + " unidades." + aviso,
                    "Confirmar", "Cancelar"))
            { AtualizarResumo(); return; }

            // Tudo dentro do try: se algo aqui falhasse, _envioEmAndamento ficaria preso em true
            // e o app inteiro travaria (não deixa trocar de módulo, sair, nem fechar a janela).
            // _busy também bloqueia "Gerar pré-pedido" e o editor de caixas fechadas.
            bool tenhoGate = false;
            try
            {
                _enviando = true; _busy = true; _envioEmAndamento = true;
                TravarControles("Gerar pré-pedido");
                btnEnviar.Text = "Enviando...";
                banner.SetState(StatusKind.Busy, "Enviando o pedido à CAF (" + itens.Count + " produtos, " +
                    unidades.ToString("#,0") + " unidades)... aguarde, não feche o programa.");
                Logger.Log("Pedido CAF: ENVIANDO " + itens.Count + " itens / " + unidades + " unidades (atendente " +
                    EstoqueCaf + " = CAF, solicitante " + EstoqueFarmaciaCentral + " = Farmácia Central).");

                // Exclusividade no MV antes de gravar: nenhuma outra conversa pode estar aberta
                // na mesma sessão enquanto a solicitação é criada.
                tenhoGate = await MvGate.EntrarAsync("pedido-caf-enviar", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenhoGate)
                {
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta — o pedido NÃO foi enviado. Tente de novo em instantes.");
                    _travado = false;   // nada foi gravado: pode tentar de novo
                    return;
                }

                var res = await MorphisClient.EnviarPedidoCafAsync(_host.Session, itens,
                    EstoqueCaf, EstoqueFarmaciaCentral);

                // Estado estático ANTES do teste de descarte: se a view morresse aqui, perder o
                // número/trava deixaria o próximo acesso liberado para gravar de novo.
                _memPedido = res.Numero; _memPedidoUrl = res.ReportUrl;
                _memBloqueado = res.Numero > 0 || res.Ambiguo;
                _memPedidoData = DateTime.Today;
                if (res.Numero > 0) { _memUltimoNumero = res.Numero; _memUltimoQuando = DateTime.Now; }
                if (IsDisposed || Disposing) return;

                _travado = true;                       // qualquer desfecho: não reenviar desta tela
                // Barra vira "pedido enviado": comprovante (a prova mais forte de que gravou,
                // por isso aparece mesmo sem número) + o caminho para montar o próximo.
                AplicarEstadoPosEnvio(true);

                if (res.Numero > 0)
                {
                    Logger.Log("Pedido CAF: nº " + res.Numero + " -> " + res.ReportUrl);
                    if (!string.IsNullOrEmpty(res.ReportUrl))
                    {
                        try { Navegador.Abrir(res.ReportUrl); }
                        catch (Exception ex) { Logger.Log("Pedido CAF: navegador falhou: " + ex.Message); }
                    }
                    string rej = res.Rejeitados.Count > 0
                        ? " " + res.Rejeitados.Count + " produto(s) recusado(s) pelo MV não entraram." : "";
                    banner.SetState(StatusKind.Success, "Pedido nº " + res.Numero + " criado na CAF com " +
                        (itens.Count - res.Rejeitados.Count) + " produtos." + rej +
                        (string.IsNullOrEmpty(res.ReportUrl) ? "" : " Comprovante aberto no navegador.") +
                        " Para montar outro, clique em “Novo pedido”.");
                    if (res.Rejeitados.Count > 0)
                    {
                        var lst = new System.Text.StringBuilder();
                        foreach (var r in res.Rejeitados)
                        {
                            Logger.Log("Pedido CAF: recusado " + r.Codigo + ": " + r.Motivo);
                            if (lst.Length < 700) lst.Append("\n• ").Append(r.Codigo).Append(" — ").Append(r.Motivo);
                        }
                        StyledDialog.Aviso(this.FindForm(), "Produtos recusados pelo MV",
                            "O pedido nº " + res.Numero + " foi criado, mas estes produtos ficaram fora:" + lst,
                            "Entendi");
                    }
                }
                else
                {
                    Logger.Log("Pedido CAF: sem número (ambíguo=" + res.Ambiguo + ") — " + res.Erro);
                    // Só afirma "não foi criado" nos abortos ANTES do commit. Se o comando de
                    // gravar já saiu (Ambiguo), o pedido PODE existir — a mensagem não pode mentir.
                    banner.SetState(StatusKind.Error, res.Ambiguo
                        ? res.Erro
                        : "O pedido NÃO foi criado (nada foi gravado). " +
                          (res.Erro != null ? res.Erro : "O MV não confirmou a gravação.") +
                          " Corrija e gere um novo pré-pedido para enviar.");
                }
            }
            catch (SessaoExpiradaException)
            {
                // Pelo mesmo invariante do catch abaixo, uma expiração que CHEGA aqui é
                // pré-gravação (se a sessão caísse no commit, viria PedidoResult com Ambiguo).
                Logger.Log("Pedido CAF: sessão expirou ANTES da gravação — nada foi criado.");
                if (!IsDisposed && !Disposing)
                {
                    StyledDialog.Aviso(this.FindForm(), "Sessão expirada",
                        "A sessão caiu antes da gravação — nada foi criado no MV. Entre de novo e reenvie o pedido.",
                        "Ir para o login");
                    _envioEmAndamento = false;
                    _host.SessaoExpirou();
                }
                return;
            }
            catch (Exception ex)
            {
                // INVARIANTE (não quebrar): EnviarPedidoCafAsync NÃO lança depois do COMMIT — a
                // mensagem de gravação está dentro de um try/catch que devolve PedidoResult com
                // Ambiguo=true em vez de propagar, e não há await nenhum depois dela. Portanto uma
                // exceção AQUI é sempre pré-gravação: nada foi criado e reenviar é seguro.
                // Se algum dia entrar um await após o commit, esta tela precisa voltar a travar.
                Logger.Log("Pedido CAF: FALHA antes da gravação: " + ex);
                if (IsDisposed || Disposing) return;
                banner.SetState(StatusKind.Error, "Falha ao enviar: " + ex.Message +
                    ". Nada foi gravado no MV (a falha foi antes da gravação) — pode tentar de novo.");
            }
            finally
            {
                if (tenhoGate) MvGate.Sair();
                _enviando = false; _busy = false; _envioEmAndamento = false;
                if (!IsDisposed && !Disposing) DestravarControles();
            }
        }

        // Editor de caixas fechadas (modal). Ao fechar, recalcula a quantidade inicial das
        // linhas que o usuário ainda NÃO ajustou à mão (Pedido == valor calculado).
        private void OnCaixas(object sender, EventArgs e)
        {
            // Depois de enviar, recalcular as sugestões mudaria a grade sem mudar o que foi
            // pedido à CAF — a tela passaria a mentir na conferência contra o comprovante.
            if (_busy || _travado) return;
            using (var d = new CaixasDialog(_host.Session))
                d.ShowDialog(this.FindForm());

            if (_linhas.Count == 0) return;
            bool mudou = false;
            foreach (var l in _linhas)
            {
                int nova = PedidoSugestao.Sugerir(l.Prod.Consumo, l.Prod.EstoqueAtual, _dias,
                    PedidoEmbalagens.Obter(l.Prod.Codigo));
                if (nova == l.Sugestao) continue;
                if (l.Pedido == l.Sugestao) l.Pedido = nova;   // acompanha só quem não foi mexido
                l.Sugestao = nova;
                mudou = true;
            }
            if (mudou) RenderGrid();
        }

        private void TravarControles(string texto)
        {
            btnGerar.Enabled = false; btnGerar.Text = texto;
            btnPdf.Enabled = false;
            btnEnviar.Enabled = false;
            cmbEspecie.Enabled = false; cmbPeriodo.Enabled = false;
            txtBusca.Enabled = false;
            _esperaBusca.Stop();                                  // tick atrasado da busca não dispara no meio
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();  // edição aberta não sobrevive à troca de dados
            // Trava a ÚNICA coluna editável. Não usar grid.ReadOnly: o setter do DataGridView
            // limpa o ReadOnly de todas as colunas ao voltar para false, e as outras 6 colunas
            // (código/produto/saldos) ficariam editáveis para sempre.
            TravarColunaPedido(true);
        }

        private void TravarColunaPedido(bool travar)
        {
            var c = grid.Columns["pedido"];
            if (c != null) c.ReadOnly = travar;
            // Sem a linha de adicionar quando o pedido está travado (enviado) ou em geração.
            grid.AllowUserToAddRows = !travar;
        }

        private void DestravarControles()
        {
            btnGerar.Enabled = true; btnGerar.Text = "Gerar pré-pedido";
            btnPdf.Enabled = _reportUrl != null;
            cmbEspecie.Enabled = true; cmbPeriodo.Enabled = true;
            txtBusca.Enabled = true;
            // Pedido já enviado = quantidade CONGELADA: mudar depois faria a tela mostrar um
            // número que nunca foi para a CAF (e _memLinhas compartilha os mesmos objetos).
            TravarColunaPedido(_travado);
            AtualizarResumo();       // quem decide o estado do envio é AtualizarEnvio()
        }

        private void SalvarMemoria(string tipo, int dias)
        {
            _memLinhas = new List<LinhaPedido>(_linhas);
            _memUrl = _reportUrl;
            _memQuando = DateTime.Now;
            _memDe = _de; _memAte = _ate;
            _memTipo = tipo; _memPeriodo = dias;
        }

        // ---- Grade ----
        private void RenderGrid()
        {
            // Edição ativa não pode vazar para os dados novos: cancela ANTES de mexer em
            // _visiveis (o Rows.Clear dispara CellEndEdit com o índice antigo).
            _renderizando = true;
            try
            {
                if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
                grid.CurrentCell = null;

                string busca = txtBusca != null ? txtBusca.Text.Trim() : "";
                _visiveis.Clear();
                foreach (var l in _linhas)
                    if (busca.Length == 0 || Corresponde(l, busca)) _visiveis.Add(l);

                grid.SuspendLayout();
                grid.Rows.Clear();
                foreach (var l in _visiveis)
                {
                    int r = grid.Rows.Add(
                        l.Prod.Codigo.ToString(),
                        l.Prod.Nome,
                        l.Prod.Unidade,
                        Qtd(l.TemCaf ? l.EstoqueCaf : 0),   // sem registro na CAF = 0
                        Qtd(l.Prod.Consumo),
                        Qtd(l.Prod.EstoqueAtual),
                        l.Pedido.ToString());
                    grid.Rows[r].Tag = l;
                    // Tooltip do realce vermelho: o DataGridView não aceita tooltip por estilo.
                    if (l.Prod.Codigo == _codigoDuplicado)
                        foreach (DataGridViewCell c in grid.Rows[r].Cells) c.ToolTipText = AvisoDuplicado;
                }
                grid.ResumeLayout();

                bool tem = _visiveis.Count > 0;
                grid.Visible = tem;
                lblVazio.Visible = !tem;
                // Ao ficar visível com CurrentCell nulo, o WinForms torna a 1ª célula corrente
                // e (FullRowSelect) a linha 0 nasceria com a cor de seleção sem clique nenhum.
                // O BeginInvoke desfaz isso DEPOIS da exibição — e antes de qualquer
                // MostrarLinha enfileirado na sequência (a fila preserva a ordem).
                if (tem)
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!IsDisposed && !Disposing) grid.LimparSelecao();
                    });
                if (!tem)
                    lblVazio.Text = _linhas.Count > 0
                        ? "Nada encontrado para essa busca."
                        : "Escolha o tipo e o período e clique em “Gerar pré-pedido”.";
                AtualizarResumo();
            }
            finally { _renderizando = false; }
        }

        private static string Qtd(decimal v)
        {
            return v == decimal.Truncate(v) ? ((long)v).ToString() : v.ToString("0.##");
        }


        private static bool Corresponde(LinhaPedido l, string busca)
        {
            return BuscaSolicitacao.CorrespondeTexto(l.Prod.Codigo + " " + l.Prod.Nome, busca);
        }

        // O resumo do canto superior direito foi retirado a pedido; do que ele fazia,
        // sobrou o essencial — decidir se o botão de envio habilita.
        private void AtualizarResumo()
        {
            if (_linhas.Count == 0) { AtualizarEnvio(0); return; }
            int no = 0;
            foreach (var l in _linhas) if (l.Pedido > 0) no++;
            AtualizarEnvio(no);
        }

        // Habilita o envio só quando há o que enviar — e NUNCA depois de um envio (ou de uma
        // falha ambígua): um segundo clique criaria uma segunda solicitação no MV.
        private void AtualizarEnvio(int itens)
        {
            if (btnEnviar == null) return;
            if (_travado)
            {
                btnEnviar.Enabled = false;
                btnEnviar.Text = _memPedido > 0 ? "Pedido " + _memPedido + " enviado" : "Envio bloqueado";
                return;
            }
            btnEnviar.Enabled = !_busy && !_enviando && itens > 0 && _host.Session != null;
            // Texto fixo, a pedido: a contagem sai do botão (a confirmação de envio
            // continua dizendo quantos itens e quantas unidades vão).
            btnEnviar.Text = "Enviar pedido";
        }

        // ---- Comandos globais de teclado (F5 / Ctrl+F, roteados pela MainWindow) ----

        // F5 segue exatamente o caminho do botão "Gerar pré-pedido" — mesmas travas e
        // confirmações (inclusive o aviso quando já saiu um pedido hoje). No estado
        // pós-envio o botão sai de cena, e o F5 sai junto.
        public void ComandoAtualizar()
        {
            if (_busy || _enviando) return;
            if (btnGerar == null || !btnGerar.Visible || !btnGerar.Enabled) return;
            OnGerar(btnGerar, EventArgs.Empty);
        }

        public void ComandoFocarBusca()
        {
            if (txtBusca == null || !txtBusca.Enabled) return;
            txtBusca.Focus();
            txtBusca.SelectAll();
        }
    }
}

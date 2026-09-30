using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Módulo "Produção de Kits": Etapa 1 (escolher kit) -> pré-visualização ->
    // confirmar produção (IRREVERSÍVEL) -> comprovante + etiqueta.
    // A criação do kit é controlada: o código foi endurecido contra duplo-clique,
    // re-entrância e troca de tela durante a gravação.
    public class ProduzirView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbKit;
        private NumericUpDown numQtd;
        private FieldHost _numHost;
        private PrimaryButton btnPreview;
        private PrimaryButton btnConfirmar;
        private PrimaryButton btnCancelar;
        private PrimaryButton btnEtiqueta;
        private PrimaryButton btnTestarLas;
        private Label lblProduzido;
        private StatusBanner banner;
        private GridPro grid;
        private Label lblVazio;

        private const int Estoque = 5;       // sempre Farmácia Central
        private const int MaxKits = 20;      // teto de segurança (era 999)

        private MorphisClient _client;       // sessão do form mantida entre preview e finalização
        private System.Collections.Generic.List<KitProduzido> _kitsProduzidos; // p/ "Imprimir etiquetas" (vários kits)
        private bool _finalizado;            // trava: nunca finalizar duas vezes (por preview)

        // Trava global: produção sendo gravada no servidor. A shell consulta isto
        // para bloquear troca de módulo / logout / fechar a janela nesse intervalo.
        private static bool _producaoEmAndamento;
        public static bool ProducaoEmAndamento { get { return _producaoEmAndamento; } }

        // Há um form de produção ABERTO no servidor (da pré-visualização até confirmar/cancelar):
        // enquanto isso, o monitor de solicitações não abre outro form na mesma sessão — o
        // "Confirmar produção" depende desse task continuar válido.
        private static bool _previewAberto;
        public static bool OcupadoNoMv { get { return _producaoEmAndamento || _previewAberto; } }

        // A lista de kits vem do MV (LOV_FORMULA), mas só UMA vez por sessão: entrar no módulo
        // não pode custar uma conversa com o servidor toda vez. A lista de SampleData é o
        // ponto de partida (a tela já abre utilizável) e o plano B — kit novo cadastrado no MV
        // aparece sozinho, e servidor fora do ar não deixa o farmacêutico sem opções.
        // A sessão guardada junto é o que faz um novo login reler a lista.
        private static System.Collections.Generic.List<KitOption> _kitsMv;
        private static SoulmvKit.Core.Session _kitsSessao;

        // valores TRAVADOS no momento da pré-visualização (o diálogo de confirmação
        // mostra exatamente o que foi preparado no servidor, nunca os controles ao vivo).
        private string _previewKitNome;
        private int _previewKitCd;
        private int _previewQtd;

        public ProduzirView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;   // herda o DPI da janela
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            // Coluna única Percent: sem isso a coluna cresce até o preferido dos cartões
            // e o conteúdo pode estourar o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 4;
            // AutoSize, não altura fixa: com pixels fixos o cartão cabia num DPI e era
            // cortado embaixo em outro, porque a fonte cresce mais que a linha.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // cartão de entrada
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // cartão de pré-visualização
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // banner de status
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // ações

            root.Controls.Add(BuildInputCard(), 0, 0);
            root.Controls.Add(BuildPreviewCard(), 0, 1);
            root.Controls.Add(BuildBanner(), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);

            this.Controls.Add(root);

            banner.SetState(StatusKind.Info,
                "A pré-visualização mostra o que será produzido, nada é gravado até você confirmar a produção.");

            // Depois de a tela existir: alinha a lista de kits com o MV, em segundo plano.
            this.Load += new EventHandler(delegate { AtualizarListaDeKits(); });
        }

        // ---------- atalhos globais (F5 / Ctrl+F) roteados pela MainWindow ----------
        public void ComandoAtualizar()
        {
            // F5 refaz a pré-visualização pelo mesmo caminho do botão; as travas de
            // gravação/preview em andamento já desabilitam o botão nesses momentos.
            if (btnPreview.Enabled) OnPreview(btnPreview, EventArgs.Empty);
        }

        public void ComandoFocarBusca()
        {
            // Esta tela não tem campo de busca.
        }

        // ---------- Etapa 1: cartão de entrada ----------
        private Control BuildInputCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(16));
            card.Padding = new Padding(Theme.S(20), Theme.S(16), Theme.S(20), Theme.S(18));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3;
            tl.RowCount = 3;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(116)));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(236)));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Botão de teste da impressora (LAS) — valida a etiqueta SEM produzir kit.
            // ESCONDIDO a pedido (29/08/2026): o botão e o fluxo inteiro (OnTestarLas)
            // continuam no código — para reativar, basta Visible = true.
            btnTestarLas = new PrimaryButton();
            btnTestarLas.Text = "Testar impressora (LAS)";
            btnTestarLas.Ghost = true;
            btnTestarLas.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnTestarLas.Size = new Size(Theme.S(200), Theme.S(30));
            btnTestarLas.Font = Theme.Small;
            btnTestarLas.Click += new EventHandler(OnTestarLas);
            btnTestarLas.Visible = false;
            tl.Controls.Add(btnTestarLas, 2, 0);

            tl.Controls.Add(MicroLabel("KIT A PRODUZIR"), 0, 1);
            tl.Controls.Add(MicroLabel("QUANTIDADE"), 1, 1);

            cmbKit = new StyledComboBox();
            cmbKit.Anchor = AnchorStyles.Left | AnchorStyles.Right;   // preenche a largura, centraliza na vertical
            cmbKit.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbKit.DataSource = SampleData.Kits();
            cmbKit.KeyDown += new KeyEventHandler(OnEntradaKeyDown);
            tl.Controls.Add(cmbKit, 0, 2);

            numQtd = new NumericUpDown();
            numQtd.BorderStyle = BorderStyle.None;
            numQtd.Font = Theme.Input;
            numQtd.TextAlign = HorizontalAlignment.Center;
            numQtd.Minimum = 1;
            numQtd.Maximum = MaxKits;
            numQtd.Value = 1;
            numQtd.Dock = DockStyle.Fill;
            numQtd.KeyDown += new KeyEventHandler(OnEntradaKeyDown);
            _numHost = new FieldHost();
            _numHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _numHost.Height = Theme.S(34);
            _numHost.Margin = new Padding(0, 0, Theme.S(14), 0);
            _numHost.Controls.Add(numQtd);
            tl.Controls.Add(_numHost, 1, 2);

            btnPreview = new PrimaryButton();
            btnPreview.Text = "Gerar pré-visualização";
            btnPreview.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnPreview.Height = Theme.S(40);
            btnPreview.Click += new EventHandler(OnPreview);
            tl.Controls.Add(btnPreview, 2, 2);

            // O teclado percorre o fluxo na ordem do gesto (kit -> quantidade -> gerar);
            // o teste de impressora é ação rara e fica por último.
            cmbKit.TabIndex = 0;
            _numHost.TabIndex = 1;
            btnPreview.TabIndex = 2;
            btnTestarLas.TabIndex = 9;

            card.Controls.Add(tl);
            return card;
        }

        // Busca a lista de kits no MV e troca a do combo. Silencioso de propósito: é
        // enriquecimento, e falhar aqui não pode atrapalhar quem entrou para produzir com a
        // lista que já está na tela.
        private async void AtualizarListaDeKits()
        {
            if (_host.Session == null) return;             // preview de tela / captura: sem MV

            bool tenho = false;
            try
            {
                // Já lida nesta sessão: aplica de memória, sem falar com o servidor.
                if (_kitsMv != null && _kitsSessao == _host.Session) { AplicarKits(_kitsMv); return; }
                if (OcupadoNoMv) return;                   // form de produção vivo: não abrir outro

                // Espera curta: se o MV está ocupado, a lista fixa serve e a leitura fica
                // para a próxima visita ao módulo.
                tenho = await MvGate.EntrarAsync("produzir-lista-kits", 5000);
                if (!tenho || IsDisposed || Disposing) return;

                var lista = await MorphisClient.ListarKitsAsync(_host.Session, Estoque);
                if (IsDisposed || Disposing || lista == null || lista.Count == 0) return;
                _kitsMv = lista; _kitsSessao = _host.Session;
                AplicarKits(lista);
            }
            catch (Exception ex)
            {
                // Inclui sessão expirada: a lista fixa continua valendo e quem trata a
                // expiração é a primeira ação de verdade do usuário, com aviso na tela.
                Logger.Log("Produção de kits: lista do MV indisponível, seguindo com a lista fixa (" + ex.Message + ").");
            }
            finally { if (tenho) MvGate.Sair(); }
        }

        // Troca o conteúdo do combo preservando o kit que já estava escolhido.
        private void AplicarKits(System.Collections.Generic.List<KitOption> lista)
        {
            if (cmbKit == null || !cmbKit.Enabled) return;   // travado durante preview/produção
            var atual = cmbKit.SelectedItem as KitOption;
            cmbKit.DataSource = lista;
            if (atual == null) return;
            foreach (KitOption k in lista)
                if (k.CdProduto == atual.CdProduto) { cmbKit.SelectedItem = k; return; }
        }

        // Enter no kit/quantidade dispara a pré-visualização (o gesto natural do fluxo,
        // sem precisar tabular até o botão nem pegar o mouse).
        private void OnEntradaKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            if (cmbKit.DroppedDown) return;   // com a lista aberta, Enter só confirma o item
            e.Handled = true; e.SuppressKeyPress = true;
            if (btnPreview.Enabled) OnPreview(btnPreview, EventArgs.Empty);
        }

        // ---------- Etapa 2: cartão de pré-visualização (grade ou vazio) ----------
        private Control BuildPreviewCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.CardBg;

            grid = NovaGrid();
            grid.Visible = false;

            lblVazio = new Label();
            lblVazio.Dock = DockStyle.Fill;
            lblVazio.TextAlign = ContentAlignment.MiddleCenter;
            lblVazio.Font = Theme.Body;
            lblVazio.ForeColor = Theme.TextSoft;
            lblVazio.BackColor = Theme.CardBg;
            lblVazio.Text = "Escolha um kit acima e clique em “Gerar pré-visualização”\npara ver os itens, lotes e saldos antes de produzir.";

            host.Controls.Add(grid);
            host.Controls.Add(lblVazio);

            tl.Controls.Add(host, 0, 1);
            tl.SetColumnSpan(host, 2);

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(12));
            return banner;
        }

        private Control BuildActions()
        {
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.AutoSize = true;
            flow.BackColor = Theme.ContentBg;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = false;
            flow.Margin = new Padding(0);
            flow.Padding = new Padding(0);

            btnConfirmar = new PrimaryButton();
            btnConfirmar.Text = "Confirmar produção";
            btnConfirmar.BaseColor = Theme.Success;
            btnConfirmar.HoverColor = Theme.SuccessHover;
            btnConfirmar.PressColor = Theme.Success;
            btnConfirmar.Size = new Size(Theme.S(220), Theme.S(44));
            btnConfirmar.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnConfirmar.Visible = false;
            btnConfirmar.Click += new EventHandler(OnConfirmar);

            btnCancelar = new PrimaryButton();
            btnCancelar.Text = "Cancelar";
            btnCancelar.Ghost = true;
            btnCancelar.Size = new Size(Theme.S(140), Theme.S(44));
            btnCancelar.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnCancelar.Visible = false;
            btnCancelar.Click += new EventHandler(OnCancelar);

            btnEtiqueta = new PrimaryButton();
            btnEtiqueta.Text = "Imprimir etiqueta";
            btnEtiqueta.Ghost = true;
            btnEtiqueta.Size = new Size(Theme.S(200), Theme.S(44));
            btnEtiqueta.Margin = new Padding(0, 0, Theme.S(12), 0);
            btnEtiqueta.Visible = false;
            btnEtiqueta.Click += new EventHandler(OnEtiqueta);

            lblProduzido = new Label();
            lblProduzido.AutoSize = false;
            lblProduzido.Size = new Size(Theme.S(180), Theme.S(44));
            lblProduzido.TextAlign = ContentAlignment.MiddleLeft;
            lblProduzido.Font = Theme.BodyBold;
            lblProduzido.ForeColor = Theme.Success;
            lblProduzido.BackColor = Theme.ContentBg;
            lblProduzido.Text = "";
            lblProduzido.Visible = false;

            flow.Controls.Add(btnConfirmar);
            flow.Controls.Add(btnCancelar);
            flow.Controls.Add(btnEtiqueta);
            flow.Controls.Add(lblProduzido);
            return flow;
        }

        // ---------- helpers de UI ----------
        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            l.Margin = new Padding(2, 0, 0, Theme.S(5));
            return l;
        }

        private GridPro NovaGrid()
        {
            // Grade padrão do app (double buffer, cabeçalho claro, seleção discreta,
            // hover estável, sem foco pontilhado); aqui só o que é desta tela.
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            // zebra de leitura (linhas alternadas); a seleção herda SelBg do padrão
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            g.Columns.Add(Col("prod", "Produto", 300, true, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("qtd", "Qtd", 60, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("lote", "Lote", 130, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("saldo", "Saldo", 80, false, DataGridViewContentAlignment.MiddleCenter));
            g.Columns.Add(Col("ok", "Situação", 96, false, DataGridViewContentAlignment.MiddleCenter));

            g.CellPainting += new DataGridViewCellPaintingEventHandler(Grid_CellPainting);
            return g;
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

        // Pinta a coluna "Situação" como pílula colorida (OK verde / FALTA vermelho).
        // A pílula é a única exceção aos cantos retos do app: continua em cápsula.
        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "ok") return;

            // Fundo: como este e.Handled tira a célula do pintor padrão (o GridPro o
            // respeita), o hover da linha é reproduzido aqui — senão a faixa sob o
            // mouse ficaria "furada" na coluna da pílula.
            bool selecionada = (e.State & DataGridViewElementStates.Selected) != 0;
            if (!selecionada && e.RowIndex == grid.LinhaHover)
            {
                using (var hb = new SolidBrush(Theme.HoverBg))
                    e.Graphics.FillRectangle(hb, e.CellBounds);
            }
            else e.PaintBackground(e.CellBounds, true);
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Border);   // mantém a linha 1px

            string val = e.Value == null ? "" : e.Value.ToString();
            bool ok = val == "OK";
            Color cor = ok ? Theme.Success : Theme.Error;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Theme.S(58), h = Theme.S(21);
            var pill = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - w) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h);
            using (var path = Theme.RoundedRect(pill, h / 2))
            using (var br = new SolidBrush(cor))
                g.FillPath(br, path);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, val, Theme.Caption, pill, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.Handled = true;
        }

        // ---------- 1) PREVIEW ----------
        private async void OnPreview(object sender, EventArgs e)
        {
            var kit = cmbKit.SelectedItem as KitOption;
            if (kit == null) return;
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            // reinicia o estado (nova sessão de form / novo ciclo)
            _finalizado = false; _kitsProduzidos = null;
            btnConfirmar.Visible = false; btnConfirmar.Enabled = true;
            btnCancelar.Visible = false;
            btnEtiqueta.Visible = false; lblProduzido.Visible = false;
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true;

            // Trava as entradas DURANTE a leitura (evita trocar o kit no meio do await
            // e a tabela/diálogo ficarem com um kit diferente do escolhido).
            cmbKit.Enabled = false; numQtd.Enabled = false;
            btnPreview.Enabled = false; btnPreview.Text = "Gerando...";
            banner.SetState(StatusKind.Busy, "Lendo a fórmula e os lotes no servidor...");

            bool tenho = false;
            try
            {
                // Portão único do MV: o monitor de solicitações varre em segundo plano e o
                // servidor não aceita duas conversas na mesma sessão.
                tenho = await MvGate.EntrarAsync("produzir-preview", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    cmbKit.Enabled = true; numQtd.Enabled = true;
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes.");
                    return;
                }

                int numKits = (int)numQtd.Value;
                _previewKitNome = kit.DsProduto;      // TRAVA o que será confirmado depois
                _previewKitCd = kit.CdProduto;
                _previewQtd = numKits;

                _previewAberto = true;   // form de produção vivo no servidor a partir daqui
                _client = new MorphisClient(_host.Session);
                await _client.WorkspaceInitAsync();
                await _client.CallFormAsync("M_PRODUZIR_KIT", "MV.12.01.01.06.01.#");
                var preview = await _client.MontarPreviewAsync(Estoque, kit.CdProduto, numKits);
                if (IsDisposed || Disposing) return;   // trocou de módulo durante o await

                int faltas = 0;
                foreach (var p in preview)
                {
                    string lote = p.Lote != null ? p.Lote.CdLote : "(sem lote)";
                    string saldo = p.Lote != null ? p.Lote.SaldoAtual.ToString() : "-";
                    int r = grid.Rows.Add(p.Produto.DsProduto, p.Quantidade, lote, saldo, p.Suficiente ? "OK" : "FALTA");
                    if (!p.Suficiente)
                    {
                        // sem saldo: o texto fica vermelho TAMBÉM com a linha selecionada
                        // (é justamente a linha que o farmacêutico clica para inspecionar)
                        grid.Rows[r].DefaultCellStyle.ForeColor = Theme.Error;
                        grid.Rows[r].DefaultCellStyle.SelectionForeColor = Theme.Error;
                        faltas++;
                    }
                }

                bool temItens = preview.Count > 0;
                grid.Visible = temItens; lblVazio.Visible = !temItens;
                grid.LimparSelecao();   // não destacar a 1ª linha automaticamente

                string kitsTxt = _previewQtd == 1 ? "1 kit" : _previewQtd + " kits";
                if (temItens && faltas == 0)
                {
                    btnConfirmar.Visible = true; btnConfirmar.Enabled = true;
                    btnCancelar.Visible = true; btnCancelar.Enabled = true;
                    // entradas continuam travadas: só muda gerando outro preview
                    banner.SetState(StatusKind.Success,
                        "Todos os itens com saldo, clique em “Confirmar produção” para produzir " + kitsTxt + ". (Nada foi salvo ainda.)");
                }
                else if (temItens)
                {
                    cmbKit.Enabled = true; numQtd.Enabled = true;   // libera p/ ajustar e tentar de novo
                    banner.SetState(StatusKind.Error,
                        "Saldo insuficiente de " + faltas + (faltas == 1 ? " item" : " itens") + ". (Nada foi gravado.)");
                }
                else
                {
                    cmbKit.Enabled = true; numQtd.Enabled = true;
                    banner.SetState(StatusKind.Warning, "O servidor não retornou itens para este kit.");
                }
            }
            catch (SessaoExpiradaException)
            {
                // nada foi gravado no preview: pode voltar direto para o login
                _previewAberto = false;
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
                return;
            }
            catch (Exception ex)
            {
                _previewAberto = false;   // não ficou form pendente: libera o monitor
                if (IsDisposed || Disposing) return;
                cmbKit.Enabled = true; numQtd.Enabled = true;   // preview falhou: deixa editar e tentar de novo
                banner.SetState(StatusKind.Error, "Erro ao gerar a pré-visualização: " + ex.Message);
            }
            finally
            {
                if (tenho) MvGate.Sair();
                if (IsDisposed || Disposing) _previewAberto = false;   // trocou de módulo: o form foi abandonado
                else { btnPreview.Enabled = true; btnPreview.Text = "Gerar pré-visualização"; }
            }
        }

        // ---------- 2) CONFIRMAR PRODUÇÃO (cria o kit) ----------
        private async void OnConfirmar(object sender, EventArgs e)
        {
            if (_finalizado || _client == null) return;

            // Desabilita JÁ, antes do modal: um clique/Enter em fila não dispara de novo.
            btnConfirmar.Enabled = false;

            string barras = _previewQtd == 1 ? "1 código de barras" : _previewQtd + " códigos de barras (1 por kit)";
            string aviso = _previewQtd > 1
                ? "\n\nSerão " + _previewQtd + " produções SEPARADAS, gerando " + barras + "."
                : "";
            // Modal do próprio app. O caminho seguro continua protegido: "Voltar" é o
            // CancelButton (Esc/fechar não produzem) e o botão de confirmar desta tela
            // já foi desabilitado acima (clique/Enter em fila não dispara de novo).
            bool sim = StyledDialog.Confirmar(this.FindForm(), "Confirmar produção",
                "Isto vai CRIAR e BAIXAR o estoque dos itens:\n\n   " + _previewKitNome + "   ×" + _previewQtd
                + aviso + "\n\nEsta ação NÃO pode ser desfeita. Confirmar a produção?",
                "Sim, produzir", "Voltar");

            if (!sim) { btnConfirmar.Enabled = true; return; }

            _finalizado = true;                 // trava por preview (anti-duplo-clique)
            _producaoEmAndamento = true;        // trava global (shell bloqueia navegação)
            btnPreview.Enabled = false; cmbKit.Enabled = false; numQtd.Enabled = false;
            btnCancelar.Enabled = false;        // não dá mais para cancelar durante a gravação
            btnConfirmar.Text = "Produzindo...";

            bool tenho = false;
            try
            {
                // Gravação irreversível: exige exclusividade no MV (o monitor de solicitações
                // não pode estar conversando na mesma sessão).
                tenho = await MvGate.EntrarAsync("produzir-confirmar", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                {
                    _finalizado = false; _producaoEmAndamento = false;
                    btnConfirmar.Enabled = true; btnConfirmar.Text = "Confirmar produção";
                    btnCancelar.Enabled = true;
                    banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta — NADA foi produzido. Tente confirmar de novo em instantes.");
                    return;
                }

                if (_previewQtd == 1)
                {
                    // 1 kit: usa a sessão já montada na pré-visualização (mantém a etiqueta).
                    banner.SetState(StatusKind.Busy, "Produzindo e gerando o comprovante... aguarde, não feche o programa.");
                    string url = await _client.FinalizarAsync();
                    if (IsDisposed || Disposing) return;
                    AbrirNoNavegador(url);

                    btnConfirmar.Visible = false; btnCancelar.Visible = false;
                    lblProduzido.Text = "✓ Kit produzido"; lblProduzido.Visible = true;
                    btnEtiqueta.Text = "Imprimir etiqueta";
                    btnEtiqueta.Visible = true; btnEtiqueta.Enabled = true;
                    btnPreview.Enabled = true;
                    banner.SetState(StatusKind.Success,
                        "Kit produzido e comprovante aberto no navegador. Para a etiqueta, clique em “Imprimir etiqueta” (precisa do agente LAS na máquina da farmácia).");
                }
                else
                {
                    // N kits: N produções separadas (cada uma com seu código de barras + etiqueta).
                    banner.SetState(StatusKind.Busy, "Produzindo " + _previewQtd + " kits (um de cada vez)... aguarde, não feche o programa.");
                    var res = await MorphisClient.ProduzirVariosKitsAsync(_host.Session, Estoque, _previewKitCd, _previewQtd);
                    if (IsDisposed || Disposing) return;

                    _kitsProduzidos = res.Kits;
                    foreach (var kp in res.Kits) AbrirNoNavegador(kp.ComprovanteUrl);   // 1 comprovante por kit
                    int comEtq = res.ComEtiqueta;

                    btnConfirmar.Visible = false; btnCancelar.Visible = false;
                    if (comEtq > 0)
                    {
                        btnEtiqueta.Text = "Imprimir " + comEtq + " etiquetas";
                        btnEtiqueta.Visible = true; btnEtiqueta.Enabled = true;
                    }

                    if (res.Erro == null)
                    {
                        lblProduzido.Text = "✓ " + res.Produzidos + " kits produzidos"; lblProduzido.Visible = true;
                        btnPreview.Enabled = true;
                        banner.SetState(StatusKind.Success,
                            res.Produzidos + " kits produzidos — " + res.Produzidos + " comprovantes abertos no navegador (1 código de barras cada)." +
                            (comEtq > 0 ? " Clique em “Imprimir " + comEtq + " etiquetas” para enviar as etiquetas à impressora." : " (As etiquetas não foram geradas — imprima pelo sistema, se precisar.)"));
                    }
                    else
                    {
                        lblProduzido.Visible = res.Produzidos > 0;
                        lblProduzido.Text = "✓ " + res.Produzidos + " de " + _previewQtd;
                        // não reabilita btnPreview: parou no meio (estado ambíguo)
                        banner.SetState(StatusKind.Warning,
                            "Produzidos " + res.Produzidos + " de " + _previewQtd + " kits (comprovantes abertos). " + res.Erro +
                            " Verifique no sistema e NÃO clique novamente; para continuar, troque de módulo e volte.");
                    }
                }
            }
            catch (SessaoExpiradaException)
            {
                // Sessão caiu DURANTE a gravação: estado ambíguo. Avisa de forma modal
                // (o usuário não pode perder este aviso) e só então volta para o login.
                _producaoEmAndamento = false;
                if (!IsDisposed && !Disposing)
                {
                    StyledDialog.Aviso(_host, "Sessão expirada",
                        "A sessão com o MV Soul caiu, confirme em “Consultar kits” se os kits foram criados.",
                        "Ir para o login");
                    _host.SessaoExpirou();
                }
                return;
            }
            catch (Exception ex)
            {
                // Falha AMBÍGUA: pode ter criado algum kit. NÃO rearma a produção.
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error,
                        "Falha ao finalizar: " + ex.Message + ". Pode ter sido criado algum kit — verifique no sistema e NÃO clique novamente. Para produzir outro, troque de módulo e volte.");
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _producaoEmAndamento = false;   // libera a navegação
                _previewAberto = false;         // o form de produção não está mais pendente
            }
        }

        // Abre a URL do PDF (comprovante) numa aba do navegador — NÃO baixa o arquivo.
        // Prioridade: Cent Browser (farmácia) -> Edge -> padrão do Windows.
        private void AbrirNoNavegador(string url)
        {
            try { Navegador.Abrir(url); }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Warning,
                        "O kit foi produzido, mas não foi possível abrir o comprovante no navegador: " + ex.Message + "  URL: " + url);
            }
        }

        // ---------- CANCELAR (descarta a pré-visualização, sem gravar nada) ----------
        private void OnCancelar(object sender, EventArgs e)
        {
            _finalizado = false;
            _client = null;                 // abandona a sessão de form (nada foi gravado)
            _previewAberto = false;         // libera o monitor de solicitações
            btnConfirmar.Visible = false; btnCancelar.Visible = false;
            btnEtiqueta.Visible = false; lblProduzido.Visible = false;
            grid.Rows.Clear(); grid.LimparSelecao();
            grid.Visible = false; lblVazio.Visible = true;
            cmbKit.Enabled = true; numQtd.Enabled = true;
            banner.SetState(StatusKind.Info,
                "Pré-visualização cancelada. Nada foi gravado.");
        }

        // ---------- TESTAR IMPRESSORA (LAS) — sem produzir kit ----------
        private async void OnTestarLas(object sender, EventArgs e)
        {
            btnTestarLas.Enabled = false;
            banner.SetState(StatusKind.Busy, "Procurando a impressora (LAS) e enviando uma etiqueta de TESTE...");
            try
            {
                string msg = await LasClient.ImprimirTesteAsync();
                if (IsDisposed || Disposing) return;
                bool ok = msg.StartsWith("Etiqueta de TESTE");
                banner.SetState(ok ? StatusKind.Success : StatusKind.Warning, msg + "  (Detalhes salvos em logs\\app.log, na pasta do programa.)");
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error,
                        "O teste de impressão falhou: " + ex.Message + "  (Detalhes salvos em logs\\app.log, na pasta do programa — envie esse arquivo ao suporte do Auxiliar.)");
            }
            finally { if (!IsDisposed && !Disposing) btnTestarLas.Enabled = true; }
        }

        // ---------- 3) IMPRIMIR ETIQUETA(S) (LAS) ----------
        // 1 kit: gera o ZPL da sessão atual (_client) e envia. Vários kits: envia os ZPLs já
        // coletados na produção (_kitsProduzidos). Em ambos, precisa do agente LAS na máquina.
        private async void OnEtiqueta(object sender, EventArgs e)
        {
            btnEtiqueta.Enabled = false;
            banner.SetState(StatusKind.Busy, "Gerando a(s) etiqueta(s) e enviando à impressora...");
            try
            {
                // monta a lista de ZPLs a enviar
                var zpls = new System.Collections.Generic.List<EtiquetaZpl>();
                if (_kitsProduzidos != null && _kitsProduzidos.Count > 0)
                {
                    foreach (var kp in _kitsProduzidos) if (kp.Etiqueta != null) zpls.Add(kp.Etiqueta);
                }
                else if (_client != null)
                {
                    // Gerar o ZPL é mais uma conversa com o MV: passa pelo portão único.
                    if (!await MvGate.EntrarAsync("produzir-etiqueta", 60000))
                    {
                        if (!IsDisposed && !Disposing)
                        {
                            btnEtiqueta.Enabled = true;
                            banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente a etiqueta de novo em instantes.");
                        }
                        return;
                    }
                    EtiquetaZpl etq;
                    try { etq = await _client.ObterEtiquetaZplAsync(); }   // 1 kit: gera agora
                    finally { MvGate.Sair(); }
                    if (IsDisposed || Disposing) return;
                    if (etq != null) zpls.Add(etq);
                }

                if (zpls.Count == 0)
                {
                    banner.SetState(StatusKind.Warning, "Não há etiqueta para imprimir.");
                    return;
                }

                LasImpressora printer = await LasClient.DescobrirImpressoraAsync();
                if (IsDisposed || Disposing) return;
                if (printer == null)
                {
                    banner.SetState(StatusKind.Warning,
                        "Kit(s) e comprovante(s) estão OK. Só falta a(s) etiqueta(s): " + LasClient.ExplicarFalha()
                        + " Depois, reimprima em “Consultar kits”. (Detalhes em logs\\app.log.)");
                    return;
                }

                int enviadas = 0;
                foreach (var z in zpls)
                {
                    await LasClient.EnviarZplAsync(printer, z.Zpl, z.PrinterId, z.Copies);
                    if (IsDisposed || Disposing) return;
                    enviadas++;
                }
                banner.SetState(StatusKind.Success,
                    (enviadas == 1 ? "Etiqueta enviada" : enviadas + " etiquetas enviadas") + " à impressora.");
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Warning,
                        "Não foi possível imprimir a(s) etiqueta(s): " + ex.Message + ". O(s) kit(s) e comprovante(s) já estão OK.");
            }
            finally { if (!IsDisposed && !Disposing) btnEtiqueta.Enabled = true; }
        }
    }
}

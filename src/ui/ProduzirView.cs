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
    public class ProduzirView : UserControl
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
        private DataGridView grid;
        private Label lblVazio;
        private Label lblResumo;

        private const int Estoque = 5;       // sempre Farmácia Central
        private const int MaxKits = 20;      // teto de segurança (era 999)

        private MorphisClient _client;       // sessão do form mantida entre preview e finalização
        private System.Collections.Generic.List<KitProduzido> _kitsProduzidos; // p/ "Imprimir etiquetas" (vários kits)
        private bool _finalizado;            // trava: nunca finalizar duas vezes (por preview)

        // Trava global: produção sendo gravada no servidor. A shell consulta isto
        // para bloquear troca de módulo / logout / fechar a janela nesse intervalo.
        private static bool _producaoEmAndamento;
        public static bool ProducaoEmAndamento { get { return _producaoEmAndamento; } }

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
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152)); // cartão de entrada
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
        }

        // ---------- Etapa 1: cartão de entrada ----------
        private Control BuildInputCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 16);
            card.Padding = new Padding(20, 16, 20, 18);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 3;
            tl.RowCount = 3;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titulo = CardTitle("1.  Escolha o kit e a quantidade");
            titulo.Margin = new Padding(0, 0, 0, 12);
            tl.Controls.Add(titulo, 0, 0);
            tl.SetColumnSpan(titulo, 2);

            // Botão de teste da impressora (LAS) — valida a etiqueta SEM produzir kit.
            btnTestarLas = new PrimaryButton();
            btnTestarLas.Text = "Testar impressora (LAS)";
            btnTestarLas.Ghost = true;
            btnTestarLas.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnTestarLas.Size = new Size(200, 30);
            btnTestarLas.Font = Theme.Small;
            btnTestarLas.Click += new EventHandler(OnTestarLas);
            tl.Controls.Add(btnTestarLas, 2, 0);

            tl.Controls.Add(MicroLabel("KIT A PRODUZIR"), 0, 1);
            tl.Controls.Add(MicroLabel("QUANTIDADE"), 1, 1);

            cmbKit = new StyledComboBox();
            cmbKit.Anchor = AnchorStyles.Left | AnchorStyles.Right;   // preenche a largura, centraliza na vertical
            cmbKit.Margin = new Padding(0, 0, 14, 0);
            cmbKit.DataSource = SampleData.Kits();
            tl.Controls.Add(cmbKit, 0, 2);

            numQtd = new NumericUpDown();
            numQtd.BorderStyle = BorderStyle.None;
            numQtd.Font = Theme.Input;
            numQtd.TextAlign = HorizontalAlignment.Center;
            numQtd.Minimum = 1;
            numQtd.Maximum = MaxKits;
            numQtd.Value = 1;
            numQtd.Dock = DockStyle.Fill;
            _numHost = new FieldHost();
            _numHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _numHost.Height = 34;
            _numHost.Margin = new Padding(0, 0, 14, 0);
            _numHost.Controls.Add(numQtd);
            tl.Controls.Add(_numHost, 1, 2);

            btnPreview = new PrimaryButton();
            btnPreview.Text = "Gerar pré-visualização";
            btnPreview.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnPreview.Height = 40;
            btnPreview.Click += new EventHandler(OnPreview);
            tl.Controls.Add(btnPreview, 2, 2);

            card.Controls.Add(tl);
            return card;
        }

        // ---------- Etapa 2: cartão de pré-visualização (grade ou vazio) ----------
        private Control BuildPreviewCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 14);
            card.Padding = new Padding(18, 14, 18, 16);

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 2;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titulo = CardTitle("2.  Confira a pré-visualização");
            titulo.Margin = new Padding(0, 0, 0, 10);
            tl.Controls.Add(titulo, 0, 0);

            lblResumo = new Label();
            lblResumo.AutoSize = true;
            lblResumo.Font = Theme.BodyBold;
            lblResumo.ForeColor = Theme.TextSoft;
            lblResumo.BackColor = Theme.CardBg;
            lblResumo.Anchor = AnchorStyles.Right;
            lblResumo.Margin = new Padding(0, 2, 0, 0);
            lblResumo.Text = "";
            tl.Controls.Add(lblResumo, 1, 0);

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
            banner.Margin = new Padding(0, 0, 0, 12);
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
            btnConfirmar.Size = new Size(220, 44);
            btnConfirmar.Margin = new Padding(0, 0, 12, 0);
            btnConfirmar.Visible = false;
            btnConfirmar.Click += new EventHandler(OnConfirmar);

            btnCancelar = new PrimaryButton();
            btnCancelar.Text = "Cancelar";
            btnCancelar.Ghost = true;
            btnCancelar.Size = new Size(140, 44);
            btnCancelar.Margin = new Padding(0, 0, 12, 0);
            btnCancelar.Visible = false;
            btnCancelar.Click += new EventHandler(OnCancelar);

            btnEtiqueta = new PrimaryButton();
            btnEtiqueta.Text = "Imprimir etiqueta";
            btnEtiqueta.Ghost = true;
            btnEtiqueta.Size = new Size(200, 44);
            btnEtiqueta.Margin = new Padding(0, 0, 12, 0);
            btnEtiqueta.Visible = false;
            btnEtiqueta.Click += new EventHandler(OnEtiqueta);

            lblProduzido = new Label();
            lblProduzido.AutoSize = false;
            lblProduzido.Size = new Size(180, 44);
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
        private Label CardTitle(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.H3; l.ForeColor = Theme.TextDark;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            return l;
        }

        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            l.Margin = new Padding(2, 0, 0, 5);
            return l;
        }

        private DataGridView NovaGrid()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill; g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.None;
            g.RowHeadersVisible = false; g.AllowUserToAddRows = false; g.AllowUserToResizeRows = false;
            g.AllowUserToResizeColumns = false; g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false; g.AllowUserToOrderColumns = false;
            g.EnableHeadersVisualStyles = false; g.GridColor = Theme.BorderSoft; g.Font = Theme.Body;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Primary;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 36; g.RowTemplate.Height = 32;
            g.DefaultCellStyle.Padding = new Padding(8, 0, 6, 0);
            g.DefaultCellStyle.SelectionBackColor = Theme.Light;
            g.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
            g.DefaultCellStyle.ForeColor = Theme.TextDark;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;
            g.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.Light;
            g.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.TextDark;

            g.Columns.Add(Col("prod", "Produto", 300, true, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("qtd", "Qtd", 60, false, DataGridViewContentAlignment.MiddleRight));
            g.Columns.Add(Col("lote", "Lote", 130, false, DataGridViewContentAlignment.MiddleLeft));
            g.Columns.Add(Col("saldo", "Saldo", 80, false, DataGridViewContentAlignment.MiddleRight));
            g.Columns.Add(Col("ok", "Situação", 96, false, DataGridViewContentAlignment.MiddleCenter));

            g.CellPainting += new DataGridViewCellPaintingEventHandler(Grid_CellPainting);
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

        // Pinta a coluna "Situação" como pílula colorida (OK verde / FALTA vermelho).
        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "ok") return;

            e.PaintBackground(e.CellBounds, true);
            string val = e.Value == null ? "" : e.Value.ToString();
            bool ok = val == "OK";
            Color cor = ok ? Theme.Success : Theme.Error;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = 58, h = 21;
            var pill = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - w) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h);
            using (var path = Theme.RoundedRect(pill, 10))
            using (var br = new SolidBrush(cor))
                g.FillPath(br, path);
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
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true; lblResumo.Text = "";

            // Trava as entradas DURANTE a leitura (evita trocar o kit no meio do await
            // e a tabela/diálogo ficarem com um kit diferente do escolhido).
            cmbKit.Enabled = false; numQtd.Enabled = false;
            btnPreview.Enabled = false; btnPreview.Text = "Gerando...";
            banner.SetState(StatusKind.Busy, "Lendo a fórmula e os lotes no servidor...");

            try
            {
                int numKits = (int)numQtd.Value;
                _previewKitNome = kit.DsProduto;      // TRAVA o que será confirmado depois
                _previewKitCd = kit.CdProduto;
                _previewQtd = numKits;

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
                    if (!p.Suficiente) { grid.Rows[r].DefaultCellStyle.ForeColor = Theme.Error; faltas++; }
                }

                bool temItens = preview.Count > 0;
                grid.Visible = temItens; lblVazio.Visible = !temItens;
                grid.ClearSelection();   // não destacar a 1ª linha automaticamente

                string kitsTxt = _previewQtd == 1 ? "1 kit" : _previewQtd + " kits";
                if (temItens && faltas == 0)
                {
                    lblResumo.Text = kitsTxt + "  ·  " + preview.Count + " itens  ·  todos com saldo";
                    btnConfirmar.Visible = true; btnConfirmar.Enabled = true;
                    btnCancelar.Visible = true; btnCancelar.Enabled = true;
                    // entradas continuam travadas: só muda gerando outro preview
                    banner.SetState(StatusKind.Success,
                        "Todos os itens com saldo, clique em “Confirmar produção” para produzir " + kitsTxt + ". (Nada foi salvo ainda.)");
                }
                else if (temItens)
                {
                    cmbKit.Enabled = true; numQtd.Enabled = true;   // libera p/ ajustar e tentar de novo
                    lblResumo.Text = faltas + " sem saldo p/ " + kitsTxt;
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
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                cmbKit.Enabled = true; numQtd.Enabled = true;   // preview falhou: deixa editar e tentar de novo
                banner.SetState(StatusKind.Error, "Erro ao gerar a pré-visualização: " + ex.Message);
            }
            finally
            {
                if (!IsDisposed && !Disposing) { btnPreview.Enabled = true; btnPreview.Text = "Gerar pré-visualização"; }
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
            var resp = MessageBox.Show(this,
                "Isto vai CRIAR e BAIXAR o estoque dos itens:\n\n   " + _previewKitNome + "   ×" + _previewQtd
                + aviso + "\n\nEsta ação NÃO pode ser desfeita. Confirmar a produção?",
                "Confirmar produção", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (resp != DialogResult.Yes) { btnConfirmar.Enabled = true; return; }

            _finalizado = true;                 // trava por preview (anti-duplo-clique)
            _producaoEmAndamento = true;        // trava global (shell bloqueia navegação)
            btnPreview.Enabled = false; cmbKit.Enabled = false; numQtd.Enabled = false;
            btnCancelar.Enabled = false;        // não dá mais para cancelar durante a gravação
            btnConfirmar.Text = "Produzindo...";

            try
            {
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
                _producaoEmAndamento = false;   // libera a navegação
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
                        "O kit foi produzido, mas não consegui abrir o comprovante no navegador: " + ex.Message + "  URL: " + url);
            }
        }

        // ---------- CANCELAR (descarta a pré-visualização, sem gravar nada) ----------
        private void OnCancelar(object sender, EventArgs e)
        {
            _finalizado = false;
            _client = null;                 // abandona a sessão de form (nada foi gravado)
            btnConfirmar.Visible = false; btnCancelar.Visible = false;
            btnEtiqueta.Visible = false; lblProduzido.Visible = false;
            grid.Rows.Clear(); grid.Visible = false; lblVazio.Visible = true; lblResumo.Text = "";
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
                banner.SetState(ok ? StatusKind.Success : StatusKind.Warning, msg + "  (Detalhes salvos em logs/app.log, ao lado do programa.)");
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    banner.SetState(StatusKind.Error,
                        "O teste de impressão falhou: " + ex.Message + "  (Detalhes salvos em logs/app.log, ao lado do programa — me envie esse arquivo.)");
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
                    var etq = await _client.ObterEtiquetaZplAsync();   // 1 kit: gera agora
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
                        + " Depois, reimprima em “Consultar kits”. (Detalhes em logs/app.log.)");
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
                        "Não consegui imprimir a(s) etiqueta(s): " + ex.Message + ". O(s) kit(s) e comprovante(s) já estão OK.");
            }
            finally { if (!IsDisposed && !Disposing) btnEtiqueta.Enabled = true; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Item do combo de dia (nome + número 1=Segunda..7=Domingo).
    public class DiaItem
    {
        public string Nome; public int Num;
        public DiaItem(string nome, int num) { Nome = nome; Num = num; }
        public override string ToString() { return Nome; }
    }

    // Item do combo de semana do CAF (1..5 = semana do mês).
    public class SemanaItem
    {
        public int Num;
        public SemanaItem(int num) { Num = num; }
        public override string ToString() { return "Semana " + Num; }
    }

    // Item do combo de tipo de contagem do CAF.
    public class TipoCafItem
    {
        public TipoContagemCaf Tipo;
        public TipoCafItem(TipoContagemCaf t) { Tipo = t; }
        public override string ToString() { return Tipo.Nome; }
    }

    // Módulo "Conferência de Lotes": gera o PDF de conferência de um estoque.
    //  - Farmácia Central (5): listas fixas por dia da semana + período (ContagemListas).
    //  - CAF (2): contagem semanal — semana do mês (1..5) + tipo de material/medicamento,
    //    com listas EDITÁVEIS pela própria tela (ContagemCaf, dados\contagem-caf.txt).
    // Somente leitura — não altera estoque.
    public class ConferenciaView : UserControl, IComandosDeTela
    {
        private readonly MainWindow _host;
        private StyledComboBox cmbEstoque;
        private StyledComboBox cmbDia;
        private StyledComboBox cmbPeriodo;
        private StyledComboBox cmbSemana;
        private StyledComboBox cmbTipo;
        private Label lblSlot1;
        private Label lblSlot2;
        private PrimaryButton btnGerar;
        private PrimaryButton btnEditar;
        private StatusBanner banner;
        private Label lblFaltam;
        private GridPro grid;

        private bool _ready;
        private bool _mutando;     // repopulando combos — segura os eventos
        private bool _gerando;     // geração em andamento — segura o botão e o banner

        // Flag de desenvolvimento (--conf-preview --caf): abre já no modo CAF.
        public static bool PreviewCaf;

        private const int EstoqueFarmacia = 5;

        private bool ModoCaf { get { return cmbEstoque != null && cmbEstoque.SelectedIndex == 1; } }

        public ConferenciaView(MainWindow host)
        {
            _host = host;
            this.AutoScaleMode = AutoScaleMode.Inherit;   // herda o DPI da janela
            this.BackColor = Theme.ContentBg;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.ContentBg;
            root.ColumnCount = 1;
            root.RowCount = 3;
            // A coluna única é Percent: sem isso o TableLayoutPanel deixa a coluna crescer
            // até o "preferido" dos cartões e o conteúdo estoura o viewport à direita.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // AutoSize, não altura fixa: a fonte cresce com o DPI e uma altura em pixel
            // fixo cortava o conteúdo do cartão nas escalas maiores.
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // cartão de entrada
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // lista do que será conferido

            root.Controls.Add(BuildInputCard(), 0, 0);
            root.Controls.Add(BuildBanner(), 0, 1);
            root.Controls.Add(BuildListaCard(), 0, 2);

            this.Controls.Add(root);

            // Liga os eventos de mudança; a seleção/resumo inicial é feita no Load,
            // pois SelectedItem só é resolvido após o handle do combo ser criado.
            cmbEstoque.SelectedIndexChanged += new EventHandler(OnEstoqueMudou);
            cmbDia.SelectedIndexChanged += new EventHandler(OnSelecaoMudou);
            cmbPeriodo.SelectedIndexChanged += new EventHandler(OnSelecaoMudou);
            cmbSemana.SelectedIndexChanged += new EventHandler(OnSemanaMudou);
            cmbTipo.SelectedIndexChanged += new EventHandler(OnSelecaoMudou);
            this.Load += new EventHandler(OnLoad);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            _mutando = true;

            // pré-seleciona o dia de hoje (1=Segunda..7=Domingo)
            int hoje = (int)DateTime.Now.DayOfWeek;       // 0=Domingo..6=Sábado
            int diaNum = hoje == 0 ? 7 : hoje;
            for (int i = 0; i < cmbDia.Items.Count; i++)
                if (((DiaItem)cmbDia.Items[i]).Num == diaNum) { cmbDia.SelectedIndex = i; break; }

            // pré-seleciona a semana do mês (1..5) para o CAF
            int semanaHoje = ((DateTime.Now.Day - 1) / 7) + 1;
            if (semanaHoje > 5) semanaHoje = 5;
            for (int i = 0; i < cmbSemana.Items.Count; i++)
                if (((SemanaItem)cmbSemana.Items[i]).Num == semanaHoje) { cmbSemana.SelectedIndex = i; break; }
            RecarregarTipos(null);

            if (PreviewCaf) cmbEstoque.SelectedIndex = 1;

            _mutando = false;
            _ready = true;
            AplicarModo();
        }

        private Control BuildInputCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, Theme.S(14));
            card.Padding = new Padding(Theme.S(20), Theme.S(16), Theme.S(20), Theme.S(18));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 6;
            tl.RowCount = 3;
            // Orçamento p/ janela mínima (base 96 DPI): 170+136+134+112+112 = 664 — medido
            // na captura a 1000px: 678 clipava a borda do último botão. O "Editar listas"
            // mora aqui, ao lado do "Gerar PDF"; os combos cederam a largura (a lista
            // ABERTA do tipo mostra o nome inteiro pelo DropDownWidth).
            // ESTOQUE precisa de 170: em 96 DPI (monitor da farmácia) a fonte rende mais
            // larga e "Farmácia Central" saía "Farmácia Ce…" com 150 (validado por
            // screenshot: 164 ainda cortava). A diferença veio do período|tipo, cujas
            // faces são curtas ("Diurno"/"Noturno") ou já truncam por desenho (contagens
            // da CAF, com a lista aberta mostrando o nome inteiro).
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(170)));   // estoque
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(136)));   // dia | semana
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(134)));   // período | tipo
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112)));   // Gerar PDF
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(112)));   // Editar listas
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tl.Controls.Add(MicroLabel("ESTOQUE"), 0, 1);
            lblSlot1 = MicroLabel("DIA DA SEMANA");
            tl.Controls.Add(lblSlot1, 1, 1);
            lblSlot2 = MicroLabel("PERÍODO");
            tl.Controls.Add(lblSlot2, 2, 1);

            cmbEstoque = new StyledComboBox();
            cmbEstoque.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbEstoque.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbEstoque.DataSource = new List<string> { "Farmácia Central", "CAF" };
            tl.Controls.Add(cmbEstoque, 0, 2);

            // Coluna 1: dia da semana (farmácia) OU semana do mês (CAF) na MESMA célula,
            // ambos ancorados igual ao combo de estoque (centralizados); só um visível.
            cmbDia = new StyledComboBox();
            cmbDia.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbDia.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbDia.DataSource = new List<DiaItem> {
                new DiaItem("Segunda-feira", 1), new DiaItem("Terça-feira", 2), new DiaItem("Quarta-feira", 3),
                new DiaItem("Quinta-feira", 4), new DiaItem("Sexta-feira", 5), new DiaItem("Sábado", 6), new DiaItem("Domingo", 7)
            };
            tl.Controls.Add(cmbDia, 1, 2);

            cmbSemana = new StyledComboBox();
            cmbSemana.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbSemana.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbSemana.DataSource = new List<SemanaItem> {
                new SemanaItem(1), new SemanaItem(2), new SemanaItem(3), new SemanaItem(4), new SemanaItem(5)
            };
            cmbSemana.Visible = false;
            tl.Controls.Add(cmbSemana, 1, 2);

            // Coluna 2: período (farmácia) OU tipo de contagem (CAF) na MESMA célula.
            cmbPeriodo = new StyledComboBox();
            cmbPeriodo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbPeriodo.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbPeriodo.DataSource = new List<string> { "Diurno", "Noturno" };
            tl.Controls.Add(cmbPeriodo, 2, 2);

            cmbTipo = new StyledComboBox();
            cmbTipo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbTipo.Margin = new Padding(0, 0, Theme.S(14), 0);
            cmbTipo.Visible = false;
            // A coluna cedeu largura para os botões: a lista ABERTA mostra o nome inteiro.
            cmbTipo.DropDownWidth = Theme.S(260);
            tl.Controls.Add(cmbTipo, 2, 2);

            btnGerar = new PrimaryButton();
            btnGerar.Text = "Gerar PDF";   // curto p/ caber na janela mínima; o tooltip explica
            btnGerar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnGerar.Height = Theme.S(40);
            btnGerar.Click += new EventHandler(OnGerar);
            tl.Controls.Add(btnGerar, 3, 2);

            btnEditar = new PrimaryButton();
            btnEditar.Ghost = true;
            btnEditar.Text = "Editar listas";
            btnEditar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            btnEditar.Height = Theme.S(40);
            btnEditar.Margin = new Padding(Theme.S(14), btnEditar.Margin.Top, 0, btnEditar.Margin.Bottom);
            btnEditar.Click += new EventHandler(OnGerenciar);
            tl.Controls.Add(btnEditar, 4, 2);

            var dica = new ToolTip();
            dica.SetToolTip(btnGerar, "Gera o PDF da conferência de lotes com a lista escolhida");
            dica.SetToolTip(btnEditar, "Editar as listas de contagem (o que entra em cada dia/semana)");
            this.Disposed += new EventHandler(delegate { dica.Dispose(); });

            card.Controls.Add(tl);
            return card;
        }

        private Control BuildBanner()
        {
            banner = new StatusBanner();
            banner.Dock = DockStyle.Top;
            banner.Margin = new Padding(0, 0, 0, Theme.S(14));
            return banner;
        }

        // Cartão com a LISTA do que será conferido (código + nome), sem título nem bloco
        // lateral — a pedido, sobrou só a grade. Nomes vêm do ProdutoNomes (semente +
        // aprendidos); o aviso de produto sem nome aparece embaixo, e só quando existe.
        private Control BuildListaCard()
        {
            var card = new Card();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 0);
            card.Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(16));   // igual às demais telas

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1;
            tl.RowCount = 2;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // aviso raro de produto sem nome

            grid = NovaGrid();
            tl.Controls.Add(grid, 0, 0);

            lblFaltam = new Label();
            lblFaltam.Font = Theme.Small;
            lblFaltam.ForeColor = Theme.TextSoft;
            lblFaltam.BackColor = Theme.CardBg;
            lblFaltam.AutoSize = true;
            lblFaltam.Margin = new Padding(2, Theme.S(8), 0, 0);
            lblFaltam.Visible = false;
            tl.Controls.Add(lblFaltam, 0, 1);

            card.Controls.Add(tl);
            return card;
        }

        private GridPro NovaGrid()
        {
            // GridPro já traz double buffer (adeus flicker ao repovoar a cada troca de
            // combo), cabeçalho claro, hover e seleção padrão; aqui só o que é da tela.
            var g = new GridPro();
            g.Dock = DockStyle.Fill;
            g.AllowUserToResizeColumns = false;
            g.AllowUserToOrderColumns = false;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridZebra;

            var cCod = new DataGridViewTextBoxColumn();
            cCod.Name = "codigo"; cCod.HeaderText = "Código"; cCod.Width = Theme.S(100);
            cCod.SortMode = DataGridViewColumnSortMode.NotSortable;
            // Número curto centralizado, com o cabeçalho junto — coluna curta alinhada à
            // esquerda ao lado do texto longo parecia torta.
            cCod.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cCod.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            g.Columns.Add(cCod);

            var cNome = new DataGridViewTextBoxColumn();
            cNome.Name = "produto"; cNome.HeaderText = "Produto";
            cNome.SortMode = DataGridViewColumnSortMode.NotSortable;
            cNome.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(cNome);

            return g;
        }


        private Label MicroLabel(string t)
        {
            var l = new Label();
            l.Text = t; l.Font = Theme.Caption; l.ForeColor = Theme.TextSoft;
            l.BackColor = Theme.CardBg; l.AutoSize = true;
            l.Margin = new Padding(2, 0, 0, Theme.S(5));
            return l;
        }

        // ===== modos =====

        private void OnEstoqueMudou(object sender, EventArgs e)
        {
            if (!_ready || _mutando) return;
            AplicarModo();
        }

        private void AplicarModo()
        {
            bool caf = ModoCaf;
            lblSlot1.Text = caf ? "SEMANA DO MÊS" : "DIA DA SEMANA";
            lblSlot2.Text = caf ? "TIPO DE CONTAGEM" : "PERÍODO";
            cmbDia.Visible = !caf; cmbPeriodo.Visible = !caf;
            cmbSemana.Visible = caf; cmbTipo.Visible = caf;
            // "Editar listas" abre o gerenciador (serve aos dois estoques).
            AtualizarResumo(true);
        }

        private void OnSemanaMudou(object sender, EventArgs e)
        {
            if (!_ready || _mutando) return;
            RecarregarTipos(null);
            AtualizarResumo(true);
        }

        private void OnSelecaoMudou(object sender, EventArgs e)
        {
            if (!_ready || _mutando) return;
            AtualizarResumo(true);
        }

        private int SemanaSelecionada()
        {
            var s = cmbSemana.SelectedItem as SemanaItem;
            return s != null ? s.Num : 1;
        }

        // Repopula o combo de tipos da semana atual; tenta manter/selecionar 'nome'.
        private void RecarregarTipos(string nome)
        {
            bool eraMutando = _mutando;
            _mutando = true;
            string alvo = nome;
            if (alvo == null)
            {
                var atual = cmbTipo.SelectedItem as TipoCafItem;
                if (atual != null) alvo = atual.Tipo.Nome;
            }

            var tipos = ContagemCaf.TiposDaSemana(SemanaSelecionada());
            var itens = new List<TipoCafItem>();
            foreach (var t in tipos) itens.Add(new TipoCafItem(t));
            cmbTipo.DataSource = itens;

            if (alvo != null)
                for (int i = 0; i < itens.Count; i++)
                    if (string.Compare(itens[i].Tipo.Nome, alvo, StringComparison.OrdinalIgnoreCase) == 0)
                    { cmbTipo.SelectedIndex = i; break; }

            _mutando = eraMutando;
        }

        // ===== resumo =====

        // Códigos + rótulo da seleção atual, conforme o modo.
        private List<int> CodigosSelecionados(out string rotulo)
        {
            if (ModoCaf)
            {
                var t = cmbTipo.SelectedItem as TipoCafItem;
                if (t == null) { rotulo = "Semana " + SemanaSelecionada(); return null; }
                rotulo = "Semana " + SemanaSelecionada() + " / " + t.Tipo.Nome;
                return new List<int>(t.Tipo.Codigos);
            }
            var dia = cmbDia.SelectedItem as DiaItem;
            string periodo = cmbPeriodo.SelectedItem as string;
            if (dia == null || periodo == null) { rotulo = ""; return null; }
            rotulo = dia.Nome + " / " + periodo;
            return ContagemFarmacia.Codigos(dia.Num, periodo);
        }

        // Preenche a lista (código + nome) da seleção e habilita/desabilita o botão.
        // tocarBanner=false preserva a mensagem atual (ex.: o sucesso logo após gerar).
        private void AtualizarResumo(bool tocarBanner)
        {
            if (!_ready) return;
            if (_gerando) tocarBanner = false;   // não sobrescreve o banner "Gerando..."
            string rotulo;
            var codigos = CodigosSelecionados(out rotulo);

            int n = codigos != null ? codigos.Count : 0, semNome = 0;

            grid.SuspendLayout();
            grid.Rows.Clear();
            if (codigos != null)
            {
                foreach (int cd in codigos)
                {
                    string nome = ProdutoNomes.Get(cd);
                    int r = grid.Rows.Add(cd.ToString(), nome != null ? nome : "—");
                    if (nome == null)
                    {
                        semNome++;
                        grid.Rows[r].Cells["produto"].Style.ForeColor = Theme.TextSoft;
                    }
                }
            }
            grid.LimparSelecao();
            grid.ResumeLayout();

            lblFaltam.Visible = semNome > 0;
            if (semNome > 0)
                lblFaltam.Text = semNome + " produto(s) ainda sem nome (—). Os nomes são aprendidos automaticamente ao gerar a conferência na rede do hospital.";

            btnEditar.Enabled = !_gerando;

            if (n > 0)
            {
                btnGerar.Enabled = !_gerando;
                if (tocarBanner)
                    banner.SetState(StatusKind.Info,
                        "Lista de " + rotulo + " pronta. Clique em “Gerar PDF”.");
            }
            else
            {
                btnGerar.Enabled = false;
                if (!tocarBanner) return;
                if (ModoCaf && cmbTipo.SelectedItem == null)
                    banner.SetState(StatusKind.Warning,
                        "Nenhum tipo de contagem cadastrado para a Semana " + SemanaSelecionada() + ". Clique em “Editar listas”.");
                else
                    banner.SetState(StatusKind.Warning,
                        "“" + rotulo + "” está sem códigos. Clique em “Editar listas” para montar a lista.");
            }
        }

        // ===== edição das listas (Farmácia e CAF) =====

        // Abre o gerenciador de listas (tela única para os dois estoques). Ao fechar,
        // recarrega os tipos do CAF (podem ter sido criados/renomeados/excluídos) e
        // atualiza a lista mostrada (os códigos podem ter mudado).
        private void OnGerenciar(object sender, EventArgs e)
        {
            if (_gerando) return;
            var dia = cmbDia.SelectedItem as DiaItem;
            string periodo = cmbPeriodo.SelectedItem as string;
            var tipo = cmbTipo.SelectedItem as TipoCafItem;

            using (var d = new GerenciarListasDialog(
                _host.Session,
                ModoCaf,
                dia != null ? dia.Num : 1,
                periodo,
                SemanaSelecionada(),
                tipo != null ? tipo.Tipo.Nome : null))
            {
                d.ShowDialog(this.FindForm());
            }

            string alvo = null;
            var atual = cmbTipo.SelectedItem as TipoCafItem;
            if (atual != null) alvo = atual.Tipo.Nome;
            RecarregarTipos(alvo);
            AtualizarResumo(true);
        }

        // ===== geração =====

        private async void OnGerar(object sender, EventArgs e)
        {
            if (_gerando) return;   // reentrância: uma geração por vez
            if (_host.Session == null) { banner.SetState(StatusKind.Error, "Sessão não iniciada. Faça login novamente."); return; }

            string rotulo;
            var codigos = CodigosSelecionados(out rotulo);
            if (codigos == null || codigos.Count == 0)
            {
                banner.SetState(StatusKind.Warning, "Não há lista de produtos para " + rotulo + ".");
                return;
            }
            int estoque = ModoCaf ? ContagemCaf.Estoque : EstoqueFarmacia;

            _gerando = true;
            btnGerar.Enabled = false; btnGerar.Text = "Gerando...";
            banner.SetState(StatusKind.Busy, "Gerando o relatório de " + rotulo + " (" + codigos.Count + " produtos)...");
            bool tenho = false;
            try
            {
                // Portão único: o monitor de solicitações também fala com o MV em segundo plano
                // e o servidor não aceita duas conversas na mesma sessão.
                tenho = await MvGate.EntrarAsync("conferencia", 60000);
                if (IsDisposed || Disposing) return;
                if (!tenho)
                { banner.SetState(StatusKind.Warning, "O servidor está respondendo outra consulta. Tente de novo em instantes."); return; }

                string url = await MorphisClient.GerarConferenciaAsync(_host.Session, estoque, codigos);
                if (IsDisposed || Disposing) return;
                Navegador.Abrir(url);   // abre o PDF numa aba (Cent Browser -> Edge -> padrão)
                banner.SetState(StatusKind.Success,
                    "Conferência de " + rotulo + " gerada e aberta no navegador.");
            }
            catch (SessaoExpiradaException)
            {
                if (!IsDisposed && !Disposing) _host.SessaoExpirou();   // volta para o login
                return;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) banner.SetState(StatusKind.Error, "Erro ao gerar a conferência: " + ex.Message);
            }
            finally
            {
                if (tenho) MvGate.Sair();
                _gerando = false;
                if (!IsDisposed && !Disposing)
                {
                    btnGerar.Text = "Gerar PDF";
                    AtualizarResumo(false);   // reabilita conforme a seleção atual
                }
            }
        }

        // ===== comandos globais (F5 / Ctrl+F), roteados pela MainWindow =====

        // F5: recarrega as listas do disco (os tipos do CAF podem ter mudado por fora)
        // e refaz o resumo — o mesmo caminho de quando o gerenciador de listas fecha.
        public void ComandoAtualizar()
        {
            if (!_ready || _gerando) return;
            RecarregarTipos(null);   // mantém o tipo atual selecionado, se ele ainda existir
            AtualizarResumo(true);
        }

        public void ComandoFocarBusca()
        {
            // esta tela não tem campo de busca
        }
    }
}

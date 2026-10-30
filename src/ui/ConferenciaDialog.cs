using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SoulmvKit.Core;

namespace SoulmvKit.UI
{
    // Conferência antes de fechar a baixa (aba Atender): o que foi pedido contra o que esta
    // baixa entrega, um produto por linha, na ordem da solicitação impressa (fora do pedido no
    // fim). Um selo colorido diz a situação de cada item e, no topo, quantos há de cada uma.
    // "Fechar a Baixa" (ou F12 de novo) confirma; Voltar/Esc volta para a grade. Enter NÃO
    // confirma: o leitor de código de barras manda Enter sozinho.
    public class ConferenciaDialog : MolduraDialog
    {
        private const int CProduto = 0, CDescricao = 1, CUnidade = 2, CSolicitado = 3, CJaAtendido = 4,
                          CAtendido = 5, CLotes = 6, CSituacao = 7;
        private static readonly Font FonteAtendido = new Font("Segoe UI", 10f, FontStyle.Bold);

        private readonly ResumoAtender _r;
        private readonly string _subtitulo;
        private GridPro grid;
        private PrimaryButton btnVoltar, btnFechar;

        public ConferenciaDialog(string subtitulo, ResumoAtender r) : base(new Size(1100, 620))
        {
            _r = r; _subtitulo = subtitulo;
            BuildUI();
            Preencher();
            this.MinimumSize = new Size(S(640), S(400));
            this.KeyDown += new KeyEventHandler(OnTecla);
        }

        // ===== situação: texto, glifo (Segoe MDL2) e cores =====
        private static void Estilo(SituacaoConferida s, out string glifo, out Color cor, out Color fundo)
        {
            switch (s)
            {
                case SituacaoConferida.Completo: glifo = ""; cor = Theme.Success; fundo = Theme.SuccessBg; break;   // CheckMark
                case SituacaoConferida.Parcial: glifo = ""; cor = Theme.Warning; fundo = Theme.WarningBg; break;    // Down
                case SituacaoConferida.AMais: glifo = ""; cor = Theme.Warning; fundo = Theme.WarningBg; break;      // Up
                case SituacaoConferida.ForaDoPedido: glifo = ""; cor = Theme.Primary; fundo = Theme.InfoBg; break;  // Add
                default: glifo = ""; cor = Theme.Error; fundo = Theme.ErrorBg; break;                                // Remove
            }
        }

        private static string TextoSituacao(ItemConferido it)
        {
            switch (it.Situacao)
            {
                case SituacaoConferida.Completo: return "Completo";
                case SituacaoConferida.Parcial: return "Parcial · falta " + AtenderSolicitacao.Num(-it.Diferenca);
                case SituacaoConferida.AMais: return "A mais · +" + AtenderSolicitacao.Num(it.Diferenca);
                case SituacaoConferida.ForaDoPedido: return "Fora do pedido";
                default: return "Não atendido";
            }
        }

        private static string Contagem(int n, SituacaoConferida s)
        {
            switch (s)
            {
                case SituacaoConferida.Completo: return n + (n == 1 ? " completo" : " completos");
                case SituacaoConferida.Parcial: return n + (n == 1 ? " parcial" : " parciais");
                case SituacaoConferida.AMais: return n + " a mais";
                case SituacaoConferida.ForaDoPedido: return n + " fora do pedido";
                default: return n + (n == 1 ? " não atendido" : " não atendidos");
            }
        }

        // ===== montagem =====
        private void BuildUI()
        {
            var pnlBody = new Panel();
            pnlBody.Dock = DockStyle.Fill;
            pnlBody.BackColor = Theme.CardBg;
            pnlBody.Padding = new Padding(S(24), S(0), S(24), S(16));

            var tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.BackColor = Theme.CardBg;
            tl.ColumnCount = 1;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowCount = 3;
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // contagens
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabela
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // nota + botões

            // Contagens por situação (só as que existem), na ordem em que importam.
            var selos = new FlowLayoutPanel();
            selos.AutoSize = true; selos.WrapContents = true; selos.BackColor = Theme.CardBg;
            selos.Dock = DockStyle.Fill;
            selos.Margin = new Padding(0, 0, 0, S(12));
            var ordem = new[] { SituacaoConferida.Completo, SituacaoConferida.Parcial, SituacaoConferida.AMais,
                                SituacaoConferida.ForaDoPedido, SituacaoConferida.NaoAtendido };
            bool primeiro = true;
            foreach (var s in ordem)
            {
                int n = _r.Conta(s);
                if (n == 0) continue;
                string glifo; Color cor, fundo;
                Estilo(s, out glifo, out cor, out fundo);
                var selo = new Selo();
                selo.Margin = new Padding(primeiro ? 0 : S(8), 0, 0, S(4));
                selo.Mostrar(Contagem(n, s), glifo, cor, fundo);
                selos.Controls.Add(selo);
                primeiro = false;
            }
            tl.Controls.Add(selos, 0, 0);

            grid = new GridPro();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.AllowUserToResizeColumns = false;
            grid.AllowUserToOrderColumns = false;
            grid.ShowCellToolTips = true;
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            AtenderView.Compactar(grid);
            grid.RowTemplate.Height = S(28);   // cabe o selo da situação
            // Pesos como na grade da aba (ver AtenderView.Repartir): ocupam a largura toda, e o
            // mínimo de cada coluna é o do conteúdo.
            Col("Produto", 80, false, "9999999");
            Col("Descrição", 400, false, null);
            Col("Unidade", 96, false, "FR C/100ML");
            Col("Solicitado", 88, true, "9999");
            Col("Já atend.", 84, true, "9999").HeaderCell.ToolTipText = "Atendido em baixa anterior";
            Col("Atendido", 88, true, "9999");
            Col("Lote", 150, false, "W00075854");
            var sit = Col("Situação", 150, false, null);
            string gl; Color co, fu;
            Estilo(SituacaoConferida.Parcial, out gl, out co, out fu);   // o selo mais largo (ver OnPintar)
            sit.MinimumWidth = Math.Max(sit.MinimumWidth, Selo.Medir("Parcial · falta 999", gl, Selo.FonteCelula).Width + S(24) + S(12));
            bool temJa = false;
            foreach (var it in _r.Itens) if (it.JaAtendido > 0) temJa = true;
            grid.Columns[CJaAtendido].Visible = temJa;
            AtenderView.RepartirSempre(grid);
            grid.CellFormatting += new DataGridViewCellFormattingEventHandler(OnFormatar);
            grid.CellPainting += new DataGridViewCellPaintingEventHandler(OnPintar);
            tl.Controls.Add(grid, 0, 1);

            // Botões à direita.
            var rodape = new TableLayoutPanel();
            rodape.Dock = DockStyle.Fill; rodape.AutoSize = true; rodape.BackColor = Theme.CardBg;
            rodape.ColumnCount = 3; rodape.RowCount = 1;
            rodape.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rodape.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rodape.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rodape.Margin = new Padding(0, S(14), 0, 0);

            btnVoltar = new PrimaryButton();
            btnVoltar.Text = "Voltar"; btnVoltar.Ghost = true;
            btnVoltar.Width = S(110); btnVoltar.Height = S(38);
            btnVoltar.Margin = new Padding(S(12), 0, S(8), 0);
            btnVoltar.Click += new EventHandler(delegate { Close(); });
            rodape.Controls.Add(btnVoltar, 1, 0);

            btnFechar = new PrimaryButton();
            btnFechar.Text = "Fechar a Baixa";
            btnFechar.Width = S(170); btnFechar.Height = S(38);
            btnFechar.Margin = new Padding(0);
            btnFechar.Click += new EventHandler(delegate { Confirmar(); });
            new ToolTip().SetToolTip(btnFechar, "F12 — fecha a baixa no MV com estes lotes e quantidades.");
            rodape.Controls.Add(btnFechar, 2, 0);
            tl.Controls.Add(rodape, 0, 2);

            pnlBody.Controls.Add(tl);
            this.Controls.Add(pnlBody);
            this.Controls.Add(Cabecalho("Conferência da baixa", _subtitulo));
        }

        private DataGridViewTextBoxColumn Col(string titulo, int peso, bool direita, string amostra)
        {
            var c = AtenderView.NovaColuna(titulo, peso, direita, amostra);
            grid.Columns.Add(c);
            return c;
        }

        // O que pede atenção primeiro (auditoria de 30/10/2026): parcial, a mais, incluído fora do
        // pedido, não atendido — e os completos por último. Dentro de cada grupo, a ordem da
        // impressa.
        private static int Ordem(SituacaoConferida s)
        {
            switch (s)
            {
                case SituacaoConferida.Parcial: return 0;
                case SituacaoConferida.AMais: return 1;
                case SituacaoConferida.ForaDoPedido: return 2;
                case SituacaoConferida.NaoAtendido: return 3;
                default: return 4;
            }
        }

        private void Preencher()
        {
            var itens = new List<ItemConferido>(_r.Itens);
            var posicao = new Dictionary<ItemConferido, int>();
            for (int i = 0; i < itens.Count; i++) posicao[itens[i]] = i;
            itens.Sort(delegate(ItemConferido a, ItemConferido b)
            {
                int c = Ordem(a.Situacao).CompareTo(Ordem(b.Situacao));
                return c != 0 ? c : posicao[a].CompareTo(posicao[b]);
            });
            grid.SuspendLayout();
            foreach (var it in itens)
            {
                bool fora = it.Situacao == SituacaoConferida.ForaDoPedido;
                int r = grid.Rows.Add(
                    it.Produto.ToString(),
                    it.Descricao,
                    it.Unidade,
                    fora ? "" : AtenderSolicitacao.Num(it.Solicitado),
                    it.JaAtendido > 0 ? AtenderSolicitacao.Num(it.JaAtendido) : "",
                    it.Atendido > 0 ? AtenderSolicitacao.Num(it.Atendido) : "",
                    Lotes(it),
                    TextoSituacao(it));
                grid.Rows[r].Tag = it;
                if (it.Lotes.Count > 1) grid.Rows[r].Cells[CLotes].ToolTipText = Lotes(it);
            }
            grid.ResumeLayout();
        }

        // Um lote: só o lote (a quantidade já está em Atendido). Vários: cada um com a sua.
        private static string Lotes(ItemConferido it)
        {
            if (it.Lotes.Count == 0) return "";
            if (it.Lotes.Count == 1) return it.Lotes[0].Key;
            var partes = new List<string>();
            foreach (var kv in it.Lotes) partes.Add(kv.Key + " (" + AtenderSolicitacao.Num(kv.Value) + ")");
            return string.Join("  ·  ", partes.ToArray());
        }

        private void OnFormatar(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var it = grid.Rows[e.RowIndex].Tag as ItemConferido;
            if (it == null) return;
            if (e.ColumnIndex == CAtendido)
            {
                string glifo; Color cor, fundo;
                Estilo(it.Situacao, out glifo, out cor, out fundo);
                if (it.Atendido <= 0) cor = Theme.TextSoft;
                else if (it.Situacao == SituacaoConferida.ForaDoPedido) cor = Theme.TextDark;
                e.CellStyle.Font = FonteAtendido;
                e.CellStyle.ForeColor = cor; e.CellStyle.SelectionForeColor = cor;
            }
            else if (e.ColumnIndex == CSolicitado || e.ColumnIndex == CJaAtendido || e.ColumnIndex == CLotes || e.ColumnIndex == CUnidade)
            {
                e.CellStyle.ForeColor = Theme.TextSoft; e.CellStyle.SelectionForeColor = Theme.TextSoft;
            }
        }

        // A coluna Situação é um selo colorido no lugar do texto.
        private void OnPintar(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != CSituacao) return;
            var it = grid.Rows[e.RowIndex].Tag as ItemConferido;
            if (it == null) return;
            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            if (!sel && e.RowIndex == grid.LinhaHover)
            {
                using (var b = new SolidBrush(Theme.HoverBg)) e.Graphics.FillRectangle(b, e.CellBounds);
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Border);
            }
            else e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);

            string glifo; Color cor, fundo;
            Estilo(it.Situacao, out glifo, out cor, out fundo);
            string texto = TextoSituacao(it);
            int h = S(20);
            int w = Math.Min(Selo.Medir(texto, glifo, Selo.FonteCelula).Width + S(24), e.CellBounds.Width - S(12));
            var r = new Rectangle(e.CellBounds.X + S(6), e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h);
            Selo.Desenhar(e.Graphics, r, texto, glifo, Selo.FonteCelula, cor, fundo);
            e.Handled = true;
        }

        // ===== teclas e confirmação =====
        private void Confirmar()
        {
            this.DialogResult = DialogResult.OK;
            Close();
        }

        private void OnTecla(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.F12) { e.SuppressKeyPress = true; Confirmar(); }
            else if (e.KeyCode == Keys.Enter && !btnVoltar.Focused && !btnFechar.Focused) e.SuppressKeyPress = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            btnVoltar.Focus();
        }
    }
}

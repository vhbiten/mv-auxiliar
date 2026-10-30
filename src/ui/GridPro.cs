using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Grade padrão do app. Concentra o comportamento que TODA grade deve ter:
    //  - double buffer (sem flicker ao repintar/rolar);
    //  - sem o retângulo de foco pontilhado na célula;
    //  - hover de linha estável (MouseMove + HitTest: não gruda na última linha
    //    quando o mouse sai para a área vazia, e se recalcula ao rolar a roda);
    //  - cabeçalho flat claro com linha inferior 1px;
    //  - seleção discreta (SelBg, texto na cor normal);
    //  - a 1ª exibição não deixa a linha 0 "selecionada" sozinha.
    // As views continuam configurando colunas e estilos específicos por cima.
    public class GridPro : DataGridView
    {
        private int _linhaHover = -1;
        private bool _limpouInicial;

        // false: a view pinta o próprio hover (grades com pintura custom completa)
        public bool HoverPintado = true;
        // false: a view quer controlar a seleção inicial por conta própria
        public bool SemSelecaoInicial = true;

        public GridPro()
        {
            this.DoubleBuffered = true;
            this.EnableHeadersVisualStyles = false;   // sem isso o tema do Windows ignora o estilo do cabeçalho
            this.BorderStyle = BorderStyle.None;
            this.BackgroundColor = Theme.CardBg;
            this.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            this.GridColor = Theme.GridLine;
            this.RowHeadersVisible = false;
            this.AllowUserToAddRows = false;
            this.AllowUserToDeleteRows = false;
            this.AllowUserToResizeRows = false;
            this.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.MultiSelect = false;
            this.ReadOnly = true;
            this.RowTemplate.Height = Theme.S(34);
            this.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            this.ColumnHeadersHeight = Theme.S(36);
            this.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

            this.ColumnHeadersDefaultCellStyle.BackColor = Theme.GridHeaderBg;
            this.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextDark;
            this.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.GridHeaderBg;
            this.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.TextDark;
            this.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
            this.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0);

            this.DefaultCellStyle.BackColor = Theme.CardBg;
            this.DefaultCellStyle.ForeColor = Theme.TextDark;
            this.DefaultCellStyle.SelectionBackColor = Theme.SelBg;
            this.DefaultCellStyle.SelectionForeColor = Theme.TextDark;
            this.DefaultCellStyle.Font = Theme.Body;
            this.DefaultCellStyle.Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0);
        }

        public int LinhaHover { get { return _linhaHover; } }

        // Célula em EDIÇÃO (coluna Pedido etc.): o WinForms pinta o TextBox do editor
        // com o branco da célula-base, e com o número centralizado sobravam TIRAS
        // BRANCAS verticais rasgando o realce azul da linha selecionada — bug que já
        // voltou mais de uma vez porque cada camada (painel de edição, editor, estilo
        // reaplicado DEPOIS do evento) tem cor própria, e piorou quando a base impôs uma
        // cor DIFERENTE da que a view usa (a Previsão de Consumo tem SelecaoBg próprio,
        // mais escuro que o Theme.SelBg — o quadro do editor destoava dentro da célula).
        // Regra de UMA fonte de verdade: se a view pintou o editor no handler dela, a
        // cor DELA vale; senão vale o SelBg do tema — e TODAS as camadas recebem a MESMA.
        private Color _corEdicao = Theme.SelBg;   // cor unificada da célula em edição

        // Cor fixa do editor para a view que pinta a célula em edição por conta própria (aba
        // Atender: editor BRANCO dentro da moldura da célula atual — o branco escolhido no
        // handler era trocado pelo SelBg da regra abaixo). Empty = a regra abaixo.
        public Color CorEditor = Color.Empty;

        protected override void OnEditingControlShowing(DataGridViewEditingControlShowingEventArgs e)
        {
            base.OnEditingControlShowing(e);   // os handlers das views rodam primeiro
            var tb = e.Control as TextBox;
            Color cor = Theme.SelBg;
            if (CorEditor != Color.Empty) cor = CorEditor;
            else if (tb != null && tb.BackColor != Color.White && tb.BackColor != SystemColors.Window)
                cor = tb.BackColor;            // a view escolheu: respeita
            _corEdicao = cor;
            e.CellStyle.BackColor = cor;
            e.CellStyle.SelectionBackColor = cor;
            e.CellStyle.ForeColor = Theme.TextDark;
            e.CellStyle.SelectionForeColor = Theme.TextDark;
            if (tb != null)
            {
                tb.BackColor = cor;
                tb.ForeColor = Theme.TextDark;
                tb.BorderStyle = BorderStyle.None;
            }
            if (this.EditingPanel != null) this.EditingPanel.BackColor = cor;
        }

        // Limpa seleção E a célula corrente (ClearSelection sozinho deixa o CurrentCell
        // fantasma, que volta como seleção real no próximo render).
        public void LimparSelecao()
        {
            try { this.CurrentCell = null; } catch { }
            this.ClearSelection();
        }

        // ---- hover ----
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = this.HitTest(e.X, e.Y);
            AtualizarHover(hit.RowIndex);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            AtualizarHover(-1);
        }

        protected override void OnScroll(ScrollEventArgs e)
        {
            base.OnScroll(e);
            var pt = this.PointToClient(Cursor.Position);
            if (this.ClientRectangle.Contains(pt))
            {
                var hit = this.HitTest(pt.X, pt.Y);
                AtualizarHover(hit.RowIndex);
            }
        }

        // Linhas inseridas/removidas embaixo do mouse parado (acordeão): o hover apontava para
        // a linha que estava ali antes até o mouse se mexer.
        public void RecalcularHover()
        {
            if (!this.IsHandleCreated) return;
            var pt = this.PointToClient(Cursor.Position);
            AtualizarHover(this.ClientRectangle.Contains(pt) ? this.HitTest(pt.X, pt.Y).RowIndex : -1);
        }

        private void AtualizarHover(int nova)
        {
            if (nova < 0) nova = -1;
            if (nova == _linhaHover) return;
            int antiga = _linhaHover;
            _linhaHover = nova;
            if (antiga >= 0 && antiga < this.Rows.Count) this.InvalidateRow(antiga);
            if (nova >= 0 && nova < this.Rows.Count) this.InvalidateRow(nova);
        }

        // ---- pintura ----
        protected override void OnRowPrePaint(DataGridViewRowPrePaintEventArgs e)
        {
            e.PaintParts &= ~DataGridViewPaintParts.Focus;   // sem pontilhado de foco
            base.OnRowPrePaint(e);
        }

        protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
        {
            base.OnCellPainting(e);   // os handlers da view rodam primeiro; se pintaram, respeita
            if (e.Handled) return;

            // cabeçalho flat: fundo claro + linha inferior 1px
            if (e.RowIndex == -1)
            {
                using (var b = new SolidBrush(Theme.GridHeaderBg))
                    e.Graphics.FillRectangle(b, e.CellBounds);
                using (var p = new Pen(Theme.GridLine))
                    e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Bottom - 1,
                                           e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
                e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
                e.Handled = true;
                return;
            }

            // Célula em EDIÇÃO: o fundo INTEIRO sai daqui, na cor unificada do editor. O
            // painel de edição é posicionado DENTRO do padding da célula (S(6) por lado
            // no padrão da base) e a fresta entre ele e a borda deixava passar BRANCO —
            // medido por pixel: 6px de cada lado — não importando o BackColor que se
            // desse ao painel. Pintando por baixo, qualquer fresta mostra a mesma cor.
            if (e.RowIndex >= 0 && this.IsCurrentCellInEditMode
                && this.CurrentCellAddress.X == e.ColumnIndex && this.CurrentCellAddress.Y == e.RowIndex)
            {
                using (var b = new SolidBrush(_corEdicao))
                    e.Graphics.FillRectangle(b, e.CellBounds);
                using (var p = new Pen(Theme.GridLine))   // preserva a linha divisória inferior
                    e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Bottom - 1,
                                           e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
                e.Handled = true;
                return;
            }

            // hover discreto (linha não selecionada); botões/imagens mantêm o próprio desenho
            if (HoverPintado && e.RowIndex >= 0 && e.RowIndex == _linhaHover
                && (e.State & DataGridViewElementStates.Selected) == 0)
            {
                using (var b = new SolidBrush(Theme.HoverBg))
                    e.Graphics.FillRectangle(b, e.CellBounds);
                e.Paint(e.ClipBounds, e.PaintParts & ~(DataGridViewPaintParts.Background | DataGridViewPaintParts.Focus));
                e.Handled = true;
            }
        }

        // A 1ª vez que a grade aparece, o WinForms torna a célula 0 corrente e a
        // seleciona sozinho — aqui isso é desfeito uma única vez.
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            TalvezLimparInicial();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TalvezLimparInicial();
        }

        private void TalvezLimparInicial()
        {
            if (_limpouInicial || !SemSelecaoInicial) return;
            if (!this.Visible || !this.IsHandleCreated) return;
            _limpouInicial = true;
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (!this.IsDisposed) LimparSelecao();
            });
        }
    }
}

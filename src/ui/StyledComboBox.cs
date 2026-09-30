using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // ComboBox redesenhado: itens altos e confortáveis, realce SÓ na lista aberta,
    // texto nítido (TextRenderer) e — importante — a CAIXA FECHADA é repintada
    // (WndProc após WM_PAINT) para trocar a seta cinza nativa por um chevron da
    // marca e uma borda flat. A face fechada é sempre neutra (fundo branco); o
    // foco vira borda azul de 2px — nunca aquele "texto marcado" permanente.
    public class StyledComboBox : ComboBox
    {
        private const int WM_PAINT = 0x000F;
        private readonly int BtnW = Theme.S(24);   // largura da área do chevron

        private bool _hover;

        public StyledComboBox()
        {
            this.DropDownStyle = ComboBoxStyle.DropDownList;
            this.FlatStyle = FlatStyle.Flat;
            this.DrawMode = DrawMode.OwnerDrawFixed;
            this.ItemHeight = Theme.S(30);
            this.IntegralHeight = false;
            this.MaxDropDownItems = 12;
            this.DropDownHeight = 340;
            this.Font = Theme.Input;
            this.BackColor = Color.White;
            this.ForeColor = Theme.TextDark;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; this.Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; this.Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            // A face FECHADA também passa por aqui (estado ComboBoxEdit) — e o WinForms a
            // marca como Selected sempre que o combo tem foco. Sem separar os dois casos,
            // o campo fica com fundo ciano permanente, parecendo seleção travada.
            bool edicao = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
            bool selecionado = !edicao && (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selecionado ? Theme.SelBg : Color.White;
            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);

            string texto = this.GetItemText(this.Items[e.Index]);
            var area = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, texto, this.Font, area, Theme.TextDark,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT)
                PaintFace();
        }

        // Cobre a seta/borda nativas e desenha o chevron + a borda da marca.
        private void PaintFace()
        {
            if (!this.IsHandleCreated) return;
            using (var g = Graphics.FromHwnd(this.Handle))
            {
                bool on = this.Enabled;
                var btn = new Rectangle(this.Width - BtnW, 1, BtnW - 1, this.Height - 2);

                // fundo da área do botão (esconde a seta nativa) — sempre neutro
                using (var br = new SolidBrush(Color.White))
                    g.FillRectangle(br, btn);

                // chevron (v) centralizado na área do botão
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color arrow = on ? Theme.Primary : Theme.Border;
                int cx = this.Width - BtnW / 2 - 1;
                int cy = this.Height / 2;
                int dx = Theme.S(4), dy = Theme.S(2);
                using (var pen = new Pen(arrow, 1.8f * Theme.Escala))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[] {
                        new Point(cx - dx, cy - dy),
                        new Point(cx,      cy + dy),
                        new Point(cx + dx, cy - dy)
                    });
                }
                g.SmoothingMode = SmoothingMode.None;

                // borda flat: foco = 2px azul; hover = azul médio; repouso = borda de campo
                if (this.Focused && on)
                {
                    using (var pen = new Pen(Theme.Primary, 2f))
                        g.DrawRectangle(pen, 1, 1, this.Width - 3, this.Height - 3);
                }
                else
                {
                    Color bc = !on ? Theme.BorderSoft : (_hover ? Theme.Accent : Theme.Border);
                    using (var pen = new Pen(bc))
                        g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
                }
            }
        }
    }
}

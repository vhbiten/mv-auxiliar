using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // ComboBox redesenhado: itens altos e confortáveis, realce no hover/seleção,
    // texto nítido (TextRenderer) e — importante — a CAIXA FECHADA é repintada
    // (WndProc após WM_PAINT) para trocar a seta cinza nativa por um chevron da
    // marca e uma borda flat, combinando com o resto da UI.
    public class StyledComboBox : ComboBox
    {
        private const int WM_PAINT = 0x000F;
        private const int BtnW = 24;        // largura da área do chevron

        private bool _hover;

        public StyledComboBox()
        {
            this.DropDownStyle = ComboBoxStyle.DropDownList;
            this.FlatStyle = FlatStyle.Flat;
            this.DrawMode = DrawMode.OwnerDrawFixed;
            this.ItemHeight = 30;
            this.IntegralHeight = false;
            this.MaxDropDownItems = 12;
            this.DropDownHeight = 340;
            this.Font = Theme.Input;
            this.BackColor = Color.White;
            this.ForeColor = Theme.TextDark;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; this.Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; this.Invalidate(); base.OnMouseLeave(e); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            bool selecionado = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selecionado ? Theme.Light : Color.White;   // realce ciano claro
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
                g.SmoothingMode = SmoothingMode.AntiAlias;

                bool on = this.Enabled;
                var btn = new Rectangle(this.Width - BtnW, 1, BtnW - 1, this.Height - 2);

                // fundo da área do botão (esconde a seta nativa)
                Color face = on ? (_hover ? Theme.InfoBg : Color.White) : Color.White;
                using (var br = new SolidBrush(face))
                    g.FillRectangle(br, btn);

                // chevron (v) centralizado na área do botão
                Color arrow = on ? Theme.Primary : Theme.Border;
                int cx = this.Width - BtnW / 2 - 1;
                int cy = this.Height / 2;
                using (var pen = new Pen(arrow, 1.8f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[] {
                        new Point(cx - 4, cy - 2),
                        new Point(cx,     cy + 2),
                        new Point(cx + 4, cy - 2)
                    });
                }

                // borda flat ao redor de todo o controle
                Color bc = on ? Theme.Border : Theme.BorderSoft;
                using (var pen = new Pen(bc))
                    g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
            }
        }
    }
}

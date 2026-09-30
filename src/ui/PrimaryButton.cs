using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Botão flat de cantos retos. Dois estilos:
    //  - Preenchido (padrão): fundo da marca, hover um degrau mais ESCURO, texto branco.
    //  - Ghost (secundário): fundo branco + borda da marca, texto azul (ações de menor peso).
    // Foco por teclado: anel sólido (branco interno no cheio, borda 2px no ghost).
    public class PrimaryButton : Button
    {
        private bool _hover;
        private bool _pressed;
        public int Radius = 0;                          // mantido por compatibilidade; o desenho é reto
        public bool Ghost = false;                      // estilo secundário (contorno)
        public Color BaseColor = Theme.Primary;         // #005691
        public Color HoverColor = Theme.PrimaryMid;     // #004A7D (escurece no hover — monotônico)
        public Color PressColor = Theme.PrimaryHover;   // #003366 (mais escuro ao pressionar)

        public PrimaryButton()
        {
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.ForeColor = Color.White;
            this.Font = Theme.BodyBold;
            this.Cursor = Cursors.Hand;
            this.BackColor = Color.White;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var full = this.ClientRectangle;
            var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                           TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            if (Ghost)
            {
                Color fill = !this.Enabled ? Color.White
                           : (_pressed ? Theme.SelBg : (_hover ? Theme.InfoBg : Color.White));
                Color line = !this.Enabled ? Theme.BorderSoft : BaseColor;
                Color txt  = !this.Enabled ? Theme.TextSoft : BaseColor;
                using (var br = new SolidBrush(fill)) g.FillRectangle(br, full);
                // foco = borda 2px; repouso = 1px
                if (this.Focused && this.Enabled)
                {
                    using (var pen = new Pen(BaseColor, 2f))
                        g.DrawRectangle(pen, 1, 1, this.Width - 3, this.Height - 3);
                }
                else
                {
                    using (var pen = new Pen(line, 1f))
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                TextRenderer.DrawText(g, this.Text, this.Font, rect, txt, center);
            }
            else
            {
                Color fill = !this.Enabled ? Theme.DisabledFill
                           : (_pressed ? PressColor : (_hover ? HoverColor : BaseColor));
                Color txt  = !this.Enabled ? Theme.TextSoft : Color.White;
                using (var br = new SolidBrush(fill)) g.FillRectangle(br, full);
                TextRenderer.DrawText(g, this.Text, this.Font, rect, txt, center);
                if (this.Focused && this.Enabled)
                {
                    using (var pen = new Pen(Color.White, 1f))
                        g.DrawRectangle(pen, 2, 2, this.Width - 5, this.Height - 5);
                }
            }
        }
    }
}

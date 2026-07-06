using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Botão flat arredondado. Dois estilos:
    //  - Preenchido (padrão): fundo da marca + hover/press, texto branco.
    //  - Ghost (secundário): fundo branco + borda da marca, texto azul (ações de menor peso).
    public class PrimaryButton : Button
    {
        private bool _hover;
        private bool _pressed;
        public int Radius = 8;
        public bool Ghost = false;                      // estilo secundário (contorno)
        public Color BaseColor = Theme.Primary;         // #005691
        public Color HoverColor = Theme.Accent;         // #337AB7 (mais claro no hover)
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent != null ? this.Parent.BackColor : Color.White);

            var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                           TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            using (var path = Theme.RoundedRect(rect, Radius))
            {
                if (Ghost)
                {
                    Color fill = !this.Enabled ? Color.White : (_pressed ? Theme.Light : (_hover ? Theme.InfoBg : Color.White));
                    Color line = !this.Enabled ? Theme.Border : BaseColor;
                    Color txt  = !this.Enabled ? Theme.Border : BaseColor;
                    using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                    using (var pen = new Pen(line, 1.4f)) g.DrawPath(pen, path);
                    TextRenderer.DrawText(g, this.Text, this.Font, rect, txt, center);
                }
                else
                {
                    Color fill = !this.Enabled ? Theme.DisabledFill : (_pressed ? PressColor : (_hover ? HoverColor : BaseColor));
                    Color txt  = !this.Enabled ? Theme.TextMuted : Color.White;
                    using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                    TextRenderer.DrawText(g, this.Text, this.Font, rect, txt, center);
                }
            }

            if (this.Focused && this.Enabled)
            {
                int fr = Radius - 3; if (fr < 1) fr = 1;
                var ring = Rectangle.Inflate(rect, -3, -3);
                using (var path = Theme.RoundedRect(ring, fr))
                using (var pen = new Pen(Color.FromArgb(150, Ghost ? BaseColor : Color.White)))
                {
                    pen.DashStyle = DashStyle.Dot;
                    g.DrawPath(pen, path);
                }
            }
        }
    }
}

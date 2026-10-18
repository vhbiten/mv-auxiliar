using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Botão quadrado só com ícone (glifo Segoe MDL2), contorno na cor da marca — mesmo
    // vocabulário do PrimaryButton.Ghost. A dica (ToolTip) diz o que o botão faz, já que
    // não há texto. Desabilitado: ícone e contorno apagados.
    public class IconButton : Control
    {
        private static readonly Font FonteIcone = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular);
        private readonly ToolTip _dica = new ToolTip();
        private string _glifo;
        private bool _hover, _press;

        public IconButton(string glifo, string dica)
        {
            _glifo = glifo;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            this.Size = new Size(Theme.S(36), Theme.S(36));
            this.Cursor = Cursors.Hand;
            this.TabStop = true;
            this.BackColor = Theme.CardBg;
            Dica = dica;
        }

        public string Glifo { get { return _glifo; } set { _glifo = value; this.Invalidate(); } }

        public string Dica
        {
            set { _dica.SetToolTip(this, value); this.AccessibleName = value; }
        }

        protected override void OnEnabledChanged(EventArgs e) { this.Cursor = Enabled ? Cursors.Hand : Cursors.Default; this.Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; this.Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; this.Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; this.Focus(); this.Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; this.Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled) { e.Handled = true; OnClick(EventArgs.Empty); return; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color fundo = !Enabled ? Theme.CardBg : (_press ? Theme.SelBg : (_hover ? Theme.InfoBg : Theme.CardBg));
            Color cor = Enabled ? Theme.Primary : Theme.Border;
            using (var br = new SolidBrush(fundo)) g.FillRectangle(br, this.ClientRectangle);
            int esp = (this.Focused && this.ShowFocusCues) ? Math.Max(2, Theme.S(2)) : Math.Max(1, Theme.S(1));
            using (var pen = new Pen(cor, esp))
            {
                pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
            }
            TextRenderer.DrawText(g, _glifo, FonteIcone, this.ClientRectangle, cor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _dica.Dispose();
            base.Dispose(disposing);
        }
    }
}

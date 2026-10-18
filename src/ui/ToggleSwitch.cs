using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Interruptor liga/desliga (estilo Windows 11): trilho arredondado — cheio na cor da
    // marca quando ligado, contorno quando desligado — e o botão branco que corre.
    // Clique, Espaço ou Enter alternam. Anel de foco só na navegação por teclado.
    public class ToggleSwitch : Control
    {
        private bool _ligado;
        private bool _hover;

        public event EventHandler AlteradoPeloUsuario;

        public ToggleSwitch()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor |
                          ControlStyles.Selectable, true);
            this.Size = new Size(Theme.S(44), Theme.S(24));
            this.Cursor = Cursors.Hand;
            this.TabStop = true;
            this.BackColor = Theme.CardBg;
        }

        public bool Ligado
        {
            get { return _ligado; }
            set { _ligado = value; this.Invalidate(); }
        }

        private void Alternar()
        {
            _ligado = !_ligado;
            this.Invalidate();
            if (AlteradoPeloUsuario != null) AlteradoPeloUsuario(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e) { this.Focus(); Alternar(); base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Alternar(); return; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; this.Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; this.Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fundo = new SolidBrush(this.BackColor)) g.FillRectangle(fundo, this.ClientRectangle);

            // trilho até a borda do controle (meio pixel para dentro, por causa do traço):
            // a borda direita do interruptor coincide com a dos botões e textos da linha
            float borda = Math.Max(1f, Theme.Escala);
            var trilho = new RectangleF(borda / 2f, borda / 2f, this.Width - borda - 1, this.Height - borda - 1);
            float raio = trilho.Height / 2f;
            using (var path = Pilula(trilho, raio))
            {
                if (_ligado)
                    using (var br = new SolidBrush(_hover ? Theme.PrimaryMid : Theme.Primary)) g.FillPath(br, path);
                else
                    using (var pen = new Pen(_hover ? Theme.TextMuted : Theme.Border, borda)) g.DrawPath(pen, path);
            }

            float d = trilho.Height - Theme.S(8);
            float x = _ligado ? trilho.Right - Theme.S(4) - d : trilho.Left + Theme.S(4);
            float y = trilho.Top + (trilho.Height - d) / 2f;
            using (var br = new SolidBrush(_ligado ? Color.White : Theme.TextSoft)) g.FillEllipse(br, x, y, d, d);

            if (this.Focused && this.ShowFocusCues)
                using (var pen = new Pen(Theme.Primary, borda))
                using (var anel = Pilula(new RectangleF(0, 0, this.Width - 1, this.Height - 1), (this.Height - 1) / 2f))
                    g.DrawPath(pen, anel);
        }

        private static GraphicsPath Pilula(RectangleF r, float raio)
        {
            var p = new GraphicsPath();
            float dd = raio * 2;
            p.AddArc(r.X, r.Y, dd, dd, 90, 180);
            p.AddArc(r.Right - dd, r.Y, dd, dd, 270, 180);
            p.CloseFigure();
            return p;
        }
    }
}

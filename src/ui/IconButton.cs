using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Botão quadrado só com ícone (glifo Segoe MDL2 ou desenhado por código), contorno na cor
    // da marca — mesmo vocabulário do PrimaryButton.Ghost. A dica (ToolTip) diz o que o botão
    // faz, já que não há texto. Desabilitado: ícone e contorno apagados.
    public class IconButton : Control
    {
        private static readonly Font FonteIcone = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular);
        private readonly ToolTip _dica = new ToolTip();
        private string _glifo;
        private readonly Action<Graphics, Rectangle, Color> _desenho;
        private bool _hover, _press;

        public IconButton(string glifo, string dica) : this(glifo, null, dica) { }

        // desenho: pinta o ícone na área dada, na cor dada (no lugar do glifo).
        public IconButton(Action<Graphics, Rectangle, Color> desenho, string dica) : this(null, desenho, dica) { }

        private IconButton(string glifo, Action<Graphics, Rectangle, Color> desenho, string dica)
        {
            _glifo = glifo;
            _desenho = desenho;
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
            if (_desenho != null)
            {
                int lado = Theme.S(18);
                _desenho(g, new Rectangle((Width - lado) / 2, (Height - lado) / 2, lado, lado), cor);
                return;
            }
            TextRenderer.DrawText(g, _glifo, FonteIcone, this.ClientRectangle, cor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // ===== ícones no desenho dos do MV Soul (barra de ferramentas da baixa), cheios =====

        // Borracha (o "Limpar" do MV): paralelogramo inclinado, a parte de cima cheia e a ponta
        // de baixo só no contorno.
        public static void DesenharBorracha(Graphics g, Rectangle a, Color cor)
        {
            var estado = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x0 = a.X, y0 = a.Y + a.Height * 0.06f, w = a.Width, h = a.Height * 0.88f;
            float inc = 0.34f;                                   // quanto o topo avança para a direita
            Func<float, float> esq = delegate(float t) { return x0 + w * inc * (1 - t); };
            Func<float, float> dir = delegate(float t) { return x0 + w * (1 - inc * t); };
            Func<float, float> y = delegate(float t) { return y0 + h * t; };
            float corte = 0.64f;                                 // onde a parte cheia termina
            float esp = Math.Max(1.4f, a.Width * 0.09f);         // espessura do contorno da ponta
            float tEsp = esp / h;

            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddPolygon(new[] {
                    new PointF(esq(0), y(0)), new PointF(dir(0), y(0)),
                    new PointF(dir(1), y(1)), new PointF(esq(1), y(1)) });
                // o vazado da ponta (o modo Alternate recorta)
                float t1 = corte + tEsp * 0.8f, t2 = 1 - tEsp;
                path.AddPolygon(new[] {
                    new PointF(esq(t1) + esp, y(t1)), new PointF(dir(t1) - esp, y(t1)),
                    new PointF(dir(t2) - esp, y(t2)), new PointF(esq(t2) + esp, y(t2)) });
                using (var b = new SolidBrush(cor)) g.FillPath(b, path);
            }
            g.Restore(estado);
        }

        // Lixeira (o "Excluir" do MV): alça, tampa mais larga e corpo cheio de cantos de baixo
        // arredondados.
        public static void DesenharLixeira(Graphics g, Rectangle a, Color cor)
        {
            var estado = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = a.X, y = a.Y, w = a.Width, h = a.Height;
            using (var b = new SolidBrush(cor))
            {
                g.FillRectangle(b, x + w * 0.37f, y + h * 0.02f, w * 0.26f, h * 0.08f);    // alça
                g.FillRectangle(b, x + w * 0.10f, y + h * 0.10f, w * 0.80f, h * 0.10f);    // tampa
                float cx = x + w * 0.18f, cy = y + h * 0.26f, cw = w * 0.64f, ch = h * 0.72f;
                float r = w * 0.12f;
                using (var path = new GraphicsPath())
                {
                    path.AddLine(cx, cy, cx + cw, cy);
                    path.AddArc(cx + cw - 2 * r, cy + ch - 2 * r, 2 * r, 2 * r, 0, 90);
                    path.AddArc(cx, cy + ch - 2 * r, 2 * r, 2 * r, 90, 90);
                    path.CloseFigure();
                    g.FillPath(b, path);
                }
            }
            g.Restore(estado);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _dica.Dispose();
            base.Dispose(disposing);
        }
    }
}

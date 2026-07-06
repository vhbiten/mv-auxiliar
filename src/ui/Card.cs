using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Cartão: painel branco com cantos arredondados (anti-aliased) e borda sutil,
    // pintado sobre o fundo do pai. Não usa Region (cantos serrilhados); em vez
    // disso preenche o miolo branco e deixa o pai aparecer nos cantos. Os controles
    // filhos com BackColor=Transparent mostram o branco do cartão.
    public class Card : Panel
    {
        public int Radius = 12;
        public bool Shadow = false;

        public Card()
        {
            this.DoubleBuffered = true;
            // A propriedade BackColor = branco é o que os filhos transparentes herdam;
            // o fundo real é pintado em OnPaintBackground (cantos = cor do pai).
            this.BackColor = Theme.CardBg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parentBg = this.Parent != null ? this.Parent.BackColor : Theme.ContentBg;
            g.Clear(parentBg);

            var r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            if (Shadow)
                Theme.DrawSoftShadow(g, new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), Radius, 6, 7);

            using (var path = Theme.RoundedRect(r, Radius))
            using (var br = new SolidBrush(Theme.CardBg))
            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
        }
    }
}

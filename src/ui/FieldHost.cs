using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Moldura arredondada para um campo (ex.: NumericUpDown sem borda dentro),
    // dando a ele o mesmo visual flat/bordado dos demais campos.
    public class FieldHost : Panel
    {
        public int Radius = 6;

        public FieldHost()
        {
            this.DoubleBuffered = true;
            this.BackColor = Color.White;
            this.Padding = new Padding(9, 6, 6, 6);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent != null ? this.Parent.BackColor : Color.White);
            var r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (var path = Theme.RoundedRect(r, Radius))
            using (var br = new SolidBrush(Color.White))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
        }
    }
}
